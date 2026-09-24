using System.Collections.Concurrent;

namespace AmongApi.Services;

public record JoinResult(bool Success, string? Error);

public class LobbyJoiner : IDisposable
{
    private const int JoinConfirmTimeoutMs = 45_000;
    private const int PollIntervalMs = 500;
    private const int GameReadyWaitMs = 30_000;
    private const int PumpMaxDurationMs = GameReadyWaitMs + JoinConfirmTimeoutMs;
    private const int LeaveWaitTimeoutMs = 10_000;
    private const int LeavePollIntervalMs = 250;
    private const int LeaveSettleMs = 750;
    private const int LeaveSettleFloorMs = 2_000;

    /// <summary>
    /// How long a remembered cancellation can suppress a not-yet-enqueued join.
    /// </summary>
    private const int CancelledCodeTtlMs = 15_000;

    private readonly ConcurrentQueue<JoinRequest> _queue = new();

    /// <summary>
    /// Recently cancelled lobby codes. `cancel_join` is delivered fire-and-forget,
    /// so a cancel can arrive before its matching `join_lobby` has been enqueued.
    /// When no pending request matches we record the code here so the later
    /// enqueue can still observe the cancellation. Entries are pruned after
    /// <see cref="CancelledCodeTtlMs"/>; the dictionary is therefore bounded.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cancelledCodes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;

    /// <summary>Request currently being processed by the serial pump.</summary>
    private volatile JoinRequest? _current;

    private sealed class JoinRequest
    {
        public string Code = "";
        public string Region = "";
        public string RegionIp = "";
        public int RegionPort;
        public TaskCompletionSource<JoinResult>? Tcs;
        /// <summary>Per-request cancellation, linked to the pump lifetime.</summary>
        public CancellationTokenSource? Cts;
        /// <summary>
        /// Set true immediately before the Unity join is dispatched. Once true a
        /// join coroutine is committed and cannot be aborted, so cancellation is
        /// ignored from that point on.
        /// </summary>
        public volatile bool Dispatched;
        /// <summary>
        /// Invoked by the pump (after the result is resolved) with the final
        /// result. Used to emit the out-of-band `join_lobby_result`.
        /// </summary>
        public Action<JoinResult>? OnComplete;
    }

    public LobbyJoiner(ManualLogSource _)
    {
        _pumpTask = Task.Run(PumpAsync);
    }

    public async Task<JoinResult> JoinAsync(string code, string region, string regionIp, int regionPort, Action<JoinResult>? onComplete = null)
    {
        code = code.Trim().ToUpperInvariant();
        region = region.Trim();
        regionIp = regionIp.Trim();

        if (code.Length == 0)
            return new JoinResult(false, "Empty lobby code");

        // Honour a `cancel_join` that raced ahead of this (not-yet-enqueued)
        // `join_lobby`. The cancel is remembered until we get here; consume the
        // entry and bail without enqueuing or touching the game.
        PruneCancelledCodes();
        if (_cancelledCodes.TryRemove(code, out var cancelledAt))
        {
            if (DateTimeOffset.UtcNow - cancelledAt < TimeSpan.FromMilliseconds(CancelledCodeTtlMs))
            {
                FileLogger.Info($"[LobbyJoiner] JoinAsync: '{code}' was cancelled before enqueue; skipping");
                var cancelledResult = new JoinResult(false, "Cancelled");
                try { onComplete?.Invoke(cancelledResult); }
                catch (Exception ex) { FileLogger.Error($"[LobbyJoiner] OnComplete failed: {ex.Message}"); }
                return cancelledResult;
            }
            // Stale entry (already removed above): fall through and join normally.
            FileLogger.Info($"[LobbyJoiner] JoinAsync: ignoring stale cancellation for '{code}'");
        }

        var request = new JoinRequest
        {
            Code = code,
            Region = region,
            RegionIp = regionIp,
            RegionPort = regionPort,
            OnComplete = onComplete,
            Tcs = new TaskCompletionSource<JoinResult>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        // Linked to the pump lifetime so Dispose() tears every pending request down.
        request.Cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _queue.Enqueue(request);

        try
        {
            return await request.Tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(PumpMaxDurationMs + 5000));
        }
        catch (TimeoutException)
        {
            return new JoinResult(false, "Join timed out waiting for result");
        }
        catch (TaskCanceledException)
        {
            return new JoinResult(false, "Join dispatch cancelled");
        }
    }

    /// <summary>
    /// Cancels the queued-or-in-progress join whose code matches
    /// <paramref name="code"/> (case-insensitive), or the oldest pending join
    /// when <paramref name="code"/> is empty, then awaits its result so the
    /// caller knows the pump is quiescent before starting the next join.
    /// Because the pump is strictly serial, a resolved request guarantees no
    /// join is in flight afterwards.
    /// </summary>
    /// <remarks>
    /// Cancellation only takes effect BEFORE dispatch. Once a Unity join
    /// coroutine has been started it cannot be aborted, so an in-flight join is
    /// awaited to completion instead (this is the "wait for the cancellation to
    /// complete" guarantee).
    /// </remarks>
    /// <returns>True when a request was found and cancellation requested.</returns>
    public async Task<bool> CancelPending(string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "" : code.Trim().ToUpperInvariant();

        PruneCancelledCodes();

        var target = FindPending(normalized);
        if (target == null)
        {
            // `cancel_join` is fire-and-forget, so it can arrive before its
            // `join_lobby` has been enqueued. Remember a code-scoped cancel so
            // JoinAsync can observe it. An empty code means "oldest pending" and
            // has no code to remember.
            if (normalized.Length > 0)
            {
                _cancelledCodes[normalized] = DateTimeOffset.UtcNow;
                FileLogger.Info($"[LobbyJoiner] CancelPending: no matching request; remembered '{normalized}' for a not-yet-enqueued join");
            }
            else
            {
                FileLogger.Info("[LobbyJoiner] CancelPending: no matching request (code='')");
            }
            return false;
        }

        FileLogger.Info($"[LobbyJoiner] CancelPending: cancelling '{target.Code}' (dispatched={target.Dispatched})");
        try { target.Cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (target.Tcs != null)
        {
            try
            {
                await target.Tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(PumpMaxDurationMs + 5000));
                FileLogger.Info($"[LobbyJoiner] CancelPending: '{target.Code}' settled");
            }
            catch (TimeoutException)
            {
                FileLogger.Warn($"[LobbyJoiner] CancelPending: timed out waiting for '{target.Code}' to settle");
            }
        }

        return true;
    }

    private JoinRequest? FindPending(string normalizedCode)
    {
        // Prefer the in-progress request: it blocks everything queued behind it.
        var current = _current;
        if (current != null)
        {
            if (normalizedCode.Length == 0) return current;
            if (string.Equals(current.Code, normalizedCode, StringComparison.OrdinalIgnoreCase))
                return current;
        }

        // Otherwise the oldest queued request (ConcurrentQueue enumerates FIFO).
        foreach (var req in _queue)
        {
            if (normalizedCode.Length == 0 || string.Equals(req.Code, normalizedCode, StringComparison.OrdinalIgnoreCase))
                return req;
        }

        return null;
    }

    /// <summary>
    /// Drops remembered cancellations older than <see cref="CancelledCodeTtlMs"/>
    /// so the dictionary stays bounded. Called opportunistically from both the
    /// cancel and join paths. Safe to run concurrently.
    /// </summary>
    private void PruneCancelledCodes()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(-CancelledCodeTtlMs);
        foreach (var entry in _cancelledCodes)
        {
            if (entry.Value < cutoff)
                _cancelledCodes.TryRemove(entry);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        while (_queue.TryDequeue(out var req))
        {
            req.Cts?.Cancel();
            req.Tcs?.TrySetResult(new JoinResult(false, "Joiner disposed"));
        }
        _cts.Dispose();
    }

    private async Task PumpAsync()
    {
        var cts = _cts;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (_queue.TryDequeue(out var request) && request.Tcs != null)
                {
                    _current = request;
                    FileLogger.Info($"[LobbyJoiner] Processing join for '{request.Code}'");

                    JoinResult result;
                    try
                    {
                        result = await ProcessJoinAsync(request, request.Cts?.Token ?? cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        result = new JoinResult(false, "Cancelled");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Error($"[LobbyJoiner] Join failed: {ex.Message}");
                        result = new JoinResult(false, ex.Message);
                    }

                    // Always resolve the TCS so JoinAsync / CancelPending never
                    // hang, even if ProcessJoinAsync threw.
                    request.Tcs.TrySetResult(result);
                    FileLogger.Info($"[LobbyJoiner] Result for '{request.Code}': success={result.Success} error={result.Error ?? "none"}");

                    try { request.OnComplete?.Invoke(result); }
                    catch (Exception ex) { FileLogger.Error($"[LobbyJoiner] OnComplete failed: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error($"[LobbyJoiner] Pump failed: {ex.Message}");
            }
            finally
            {
                _current = null;
            }

            try { await Task.Delay(100, cts.Token); }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<JoinResult> ProcessJoinAsync(JoinRequest request, CancellationToken ct)
    {
        // --- Pre-dispatch: cancellation is safe and honoured. ---
        if (ct.IsCancellationRequested || request.Dispatched)
            return new JoinResult(false, "Cancelled");

        FileLogger.Info("[LobbyJoiner] Waiting for game to be ready...");
        if (!await WaitForGameReady(ct))
        {
            return ct.IsCancellationRequested
                ? new JoinResult(false, "Cancelled")
                : new JoinResult(false, "Game did not reach ready state");
        }

        if (ct.IsCancellationRequested)
            return new JoinResult(false, "Cancelled");

        bool wasInLobby;
        try
        {
            wasInLobby = await MainThreadDispatcher.EnqueueAsync(() =>
            {
                if (GameAssembly.InLobby())
                {
                    FileLogger.Info("[LobbyJoiner] In lobby, leaving...");
                    LeaveLobby();
                    return true;
                }
                return false;
            });

            if (wasInLobby)
            {
                // Wait until the client has actually left before dispatching the
                // next join. A blind leave-then-dispatch is a crash/misjoin hazard.
                if (!await WaitForLeft(ct))
                    return new JoinResult(false, "Cancelled");
            }
        }
        catch (OperationCanceledException)
        {
            return new JoinResult(false, "Cancelled");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[LobbyJoiner] Dispatch failed: {ex.Message}");
            return new JoinResult(false, $"Dispatch failed: {ex.Message}");
        }

        if (ct.IsCancellationRequested)
            return new JoinResult(false, "Cancelled");

        JoinResult startResult;
        try
        {
            startResult = await MainThreadDispatcher.EnqueueAsync(() =>
            {
                // A Unity join coroutine is committed from here on; cancellation
                // can no longer be honoured.
                request.Dispatched = true;
                return ExecuteJoin(request);
            });
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[LobbyJoiner] Dispatch failed: {ex.Message}");
            return new JoinResult(false, $"Dispatch failed: {ex.Message}");
        }

        if (!startResult.Success)
            return startResult;

        // --- Post-dispatch: cancellation is ignored, join cannot be aborted. ---
        FileLogger.Info("[LobbyJoiner] Waiting for lobby...");
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(JoinConfirmTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollIntervalMs);

            bool inLobby = await MainThreadDispatcher.EnqueueAsync(() => GameAssembly.InLobby());
            if (!inLobby)
                continue;

            var code = await MainThreadDispatcher.EnqueueAsync(CurrentLobbyCode);
            FileLogger.Info($"[LobbyJoiner] InLobby=true, code={code}, expected={request.Code}");

            // Only report success when we are in the *requested* lobby. A stale
            // match (e.g. the old lobby) must not be reported as success.
            if (string.Equals(code, request.Code, StringComparison.OrdinalIgnoreCase))
                return new JoinResult(true, null);
        }

        return new JoinResult(false, "Join timed out");
    }

    /// <summary>
    /// Waits until <c>GameAssembly.InLobby()</c> reports false (polling ~every
    /// 250ms, up to ~10s), then applies a settle delay of ~750ms with a ~2s
    /// minimum floor measured from the moment the leave began. Returns false if
    /// cancelled while waiting.
    /// </summary>
    private static async Task<bool> WaitForLeft(CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var deadline = start.AddMilliseconds(LeaveWaitTimeoutMs);
        bool left = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
                return false;

            bool inLobby;
            try
            {
                inLobby = await MainThreadDispatcher.EnqueueAsync(() => GameAssembly.InLobby());
            }
            catch
            {
                inLobby = false;
            }

            if (!inLobby)
            {
                left = true;
                break;
            }

            try { await Task.Delay(LeavePollIntervalMs, ct); }
            catch (OperationCanceledException) { return false; }
        }

        // Timed out with the client still reporting in-lobby: the leave was not
        // verified, so report failure instead of silently proceeding with a join.
        if (!left)
        {
            FileLogger.Warn("[LobbyJoiner] WaitForLeft timed out; client still reports in-lobby.");
            return false;
        }

        var elapsedMs = (int)(DateTimeOffset.UtcNow - start).TotalMilliseconds;
        var settleMs = Math.Max(LeaveSettleMs, LeaveSettleFloorMs - elapsedMs);
        if (settleMs > 0)
        {
            try { await Task.Delay(settleMs, ct); }
            catch (OperationCanceledException) { return false; }
        }

        return true;
    }

    private async Task<bool> WaitForGameReady(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(GameReadyWaitMs);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);

            // Every IL2CPP read below runs on the Unity main-thread pump. The
            // loop itself only ever handles the managed bool result, so no
            // GameAssembly member is touched from this (thread-pool) thread.
            var ready = await MainThreadDispatcher.EnqueueAsync(() =>
            {
                var client = GameAssembly.AmongUsClient();
                if (client == null) return false;

                try
                {
                    var gameStateEnum = GameAssembly.Type("InnerNet.InnerNetClient")?.GetNestedType("GameStates");
                    if (gameStateEnum == null) return false;

                    var state = GameAssembly.GetInstanceProp(client, "GameState");
                    if (state == null) return false;

                    var notJoined = GameAssembly.EnumValue(gameStateEnum, "NotJoined");
                    var joined = GameAssembly.EnumValue(gameStateEnum, "Joined");

                    if (GameAssembly.EnumEquals(state, joined) && GameAssembly.InLobby())
                    {
                        FileLogger.Info("[LobbyJoiner] Game ready: already in lobby");
                        return true;
                    }

                    if (GameAssembly.EnumEquals(state, notJoined))
                    {
                        FileLogger.Info("[LobbyJoiner] Game ready: at main menu (NotJoined)");
                        return true;
                    }

                    FileLogger.Info($"[LobbyJoiner] Game state: {state}, waiting...");
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"[LobbyJoiner] State check failed: {ex.Message}");
                }

                return false;
            });

            if (ready)
                return true;
        }

        FileLogger.Warn("[LobbyJoiner] Game ready wait timed out, proceeding anyway");
        return true;
    }

    private JoinResult ExecuteJoin(JoinRequest request)
    {
        FileLogger.Info($"[LobbyJoiner] ExecuteJoin: code={request.Code} region={request.Region} ip={request.RegionIp}:{request.RegionPort}");

        // The caller has already left any previous lobby and waited for the
        // client to settle (see ProcessJoinAsync/WaitForLeft). If we are somehow
        // still in a lobby we must NOT leave and immediately dispatch — that race
        // is a crash/misjoin hazard. Abort instead.
        if (GameAssembly.InLobby())
        {
            FileLogger.Warn("[LobbyJoiner] ExecuteJoin: still in a lobby; aborting join to avoid leave-then-dispatch race");
            return new JoinResult(false, "Still in a lobby; aborting join");
        }

        var regionSet = SetRegion(request);
        if (!regionSet)
        {
            FileLogger.Warn($"[LobbyJoiner] Region could not be set (region='{request.Region}', regionIp='{request.RegionIp}'); join may fail or connect to wrong server");
        }

        var gameId = DecodeCode(request.Code);
        if (gameId == 0)
        {
            FileLogger.Error($"[LobbyJoiner] Failed to decode code: {request.Code}");
            return new JoinResult(false, "Failed to decode lobby code");
        }
        FileLogger.Info($"[LobbyJoiner] Decoded {request.Code} -> {gameId}");

        var client = GameAssembly.AmongUsClient();
        if (client == null)
        {
            FileLogger.Error("[LobbyJoiner] AmongUsClient is null");
            return new JoinResult(false, "AmongUsClient unavailable");
        }

        return TryJoinViaReflection(client, gameId, request.Code);
    }

    private JoinResult TryJoinViaReflection(object client, int gameId, string code)
    {
        var clientType = client.GetType();
        FileLogger.Info($"[LobbyJoiner] AmongUsClient type: {clientType.FullName}");

        var allMethods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        var methodNames = allMethods.Where(m => !m.IsSpecialName && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
            .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
        FileLogger.Info($"[LobbyJoiner] Available methods: {string.Join(" | ", methodNames)}");

        // 1. Try CoJoinOnlineGameFromCode
        var coJoin = allMethods.FirstOrDefault(m => m.Name == "CoJoinOnlineGameFromCode");
        if (coJoin != null)
        {
            try
            {
                var pCount = coJoin.GetParameters().Length;
                FileLogger.Info($"[LobbyJoiner] Calling CoJoinOnlineGameFromCode ({pCount} params)...");

                object? result = pCount switch
                {
                    2 => coJoin.Invoke(client, new object[] { gameId, false }),
                    1 => coJoin.Invoke(client, new object[] { gameId }),
                    _ => null
                };

                if (result != null)
                    return StartCoroutine(client, clientType, result);
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"[LobbyJoiner] CoJoinOnlineGameFromCode failed: {ex.Message}");
            }
        }

        // 2. Try ConnectToGame
        var connectToGame = allMethods.FirstOrDefault(m => m.Name == "ConnectToGame");
        if (connectToGame != null)
        {
            try
            {
                FileLogger.Info("[LobbyJoiner] Calling ConnectToGame...");
                var result = connectToGame.Invoke(client, new object[] { gameId });
                if (result != null) return StartCoroutine(client, clientType, result);
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"[LobbyJoiner] ConnectToGame failed: {ex.Message}");
            }
        }

        // 3. Try JoinGame
        var joinGameMethods = allMethods.Where(m => m.Name == "JoinGame").ToList();
        foreach (var m in joinGameMethods)
        {
            try
            {
                var paramsInfo = m.GetParameters();
                if (paramsInfo.Length == 1)
                {
                    object arg = paramsInfo[0].ParameterType == typeof(string) ? code : gameId;
                    FileLogger.Info($"[LobbyJoiner] Calling JoinGame({arg})...");
                    var result = m.Invoke(client, new[] { arg });
                    if (result != null) return StartCoroutine(client, clientType, result);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"[LobbyJoiner] JoinGame failed: {ex.Message}");
            }
        }

        FileLogger.Error("[LobbyJoiner] All join methods failed");
        return new JoinResult(false, "No working join method found");
    }

    private JoinResult StartCoroutine(object client, Type clientType, object enumerator)
    {
        try
        {
            if (enumerator == null)
            {
                FileLogger.Warn("[LobbyJoiner] StartCoroutine received null enumerator");
                return new JoinResult(false, "Enumerator was null");
            }

            FileLogger.Info($"[LobbyJoiner] Invoking StartCoroutine with enumerator type: {enumerator.GetType().FullName}");

            Type? current = clientType;
            MethodInfo? startMethod = null;

            while (current != null && startMethod == null)
            {
                var methods = current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == "StartCoroutine" && m.GetParameters().Length == 1)
                    .ToList();

                if (methods.Count > 0)
                {
                    startMethod = methods.FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsInstanceOfType(enumerator))
                        ?? methods.FirstOrDefault(m => m.GetParameters()[0].ParameterType.Name.Contains("IEnumerator"))
                        ?? methods[0];

                    FileLogger.Info($"[LobbyJoiner] Found StartCoroutine on {current.Name}: {startMethod}");
                    break;
                }
                current = current.BaseType;
            }

            if (startMethod == null)
            {
                FileLogger.Warn("[LobbyJoiner] StartCoroutine not found in hierarchy");
                return new JoinResult(false, "StartCoroutine not found");
            }

            var coroutine = startMethod.Invoke(client, new[] { enumerator });
            FileLogger.Info($"[LobbyJoiner] StartCoroutine invoked successfully (result: {coroutine != null})");
            return new JoinResult(true, null);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[LobbyJoiner] StartCoroutine failed: {ex.Message}");
            return new JoinResult(false, $"StartCoroutine failed: {ex.Message}");
        }
    }

    private void LeaveLobby()
    {
        if (!GameAssembly.InLobby())
        {
            FileLogger.Info("[LobbyJoiner] Not in a lobby; skipping ExitGame.");
            return;
        }

        try
        {
            var client = GameAssembly.AmongUsClient();
            if (client == null) return;

            var disconnectReasons = GameAssembly.Type("DisconnectReasons");
            var exitGame = GameAssembly.EnumValue(disconnectReasons, "ExitGame");
            if (exitGame != null)
            {
                GameAssembly.CallInstanceMethod(client, "ExitGame", new object?[] { exitGame });
                FileLogger.Info("[LobbyJoiner] Called ExitGame");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[LobbyJoiner] LeaveLobby failed: {ex.Message}");
        }
    }

    private bool SetRegion(JoinRequest request)
    {
        try
        {
            var serverManagerType = GameAssembly.Type("ServerManager");
            if (serverManagerType == null) return false;

            var serverManager = GameAssembly.GetStaticMember(serverManagerType, "Instance");
            if (serverManager == null)
            {
                FileLogger.Warn("[LobbyJoiner] ServerManager unavailable");
                return false;
            }

            var regionName = !string.IsNullOrEmpty(request.Region) ? request.Region : request.RegionIp;
            if (string.IsNullOrEmpty(regionName))
            {
                FileLogger.Warn("[LobbyJoiner] No region name or IP provided");
                return false;
            }

            FileLogger.Info($"[LobbyJoiner] Checking built-in regions for: '{regionName}'");

            // 1. Check existing regions on ServerManager.Instance.AvailableRegions
            var availableRegions = GameAssembly.GetInstanceProp(serverManager, "AvailableRegions");
            if (availableRegions is System.Collections.IEnumerable regionList)
            {
                foreach (var reg in regionList)
                {
                    if (reg == null) continue;
                    var name = GameAssembly.ToStr(GameAssembly.GetInstanceProp(reg, "Name"));

                    if (MatchesRegion(regionName, name))
                    {
                        FileLogger.Info($"[LobbyJoiner] Matched built-in region: '{name}'");
                        GameAssembly.CallInstanceMethod(serverManager, "SetRegion", new object?[] { reg });
                        FileLogger.Info("[LobbyJoiner] Region set successfully via built-in match");
                        return true;
                    }
                }
            }
            else
            {
                FileLogger.Warn("[LobbyJoiner] AvailableRegions is null or not enumerable");
            }

            // 2. Custom region creation fallback
            var regionIp = request.RegionIp;
            if (string.IsNullOrEmpty(regionIp))
            {
                FileLogger.Warn($"[LobbyJoiner] Region '{regionName}' not found in built-in regions and no RegionIp provided for custom region");
                return false;
            }

            var regionPort = request.RegionPort > 0 ? request.RegionPort : 443;
            if (!regionIp.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                regionIp = "https://" + regionIp;

            FileLogger.Info($"[LobbyJoiner] Creating custom region: {regionName} @ {regionIp}:{regionPort}");

            var serverInfoType = GameAssembly.Type("ServerInfo");
            if (serverInfoType == null)
            {
                FileLogger.Warn("[LobbyJoiner] ServerInfo type not found");
                return false;
            }

            var serverInfo = GameAssembly.CreateInstance(serverInfoType,
                new object?[] { "Http-1", regionIp, (ushort)regionPort, false });
            if (serverInfo == null)
            {
                FileLogger.Warn("[LobbyJoiner] Failed to create ServerInfo instance");
                return false;
            }

            var staticHttpType = GameAssembly.Type("StaticHttpRegionInfo");
            if (staticHttpType == null)
            {
                FileLogger.Warn("[LobbyJoiner] StaticHttpRegionInfo type not found");
                return false;
            }

            var noTranslation = GameAssembly.EnumValue(GameAssembly.Type("StringNames"), "NoTranslation");
            var regionObj = GameAssembly.CreateInstance(staticHttpType,
                new object?[] { regionName, noTranslation, regionIp, new[] { serverInfo }, null })
                ?? GameAssembly.CreateInstance(staticHttpType, new object?[] { regionName, noTranslation, regionIp });

            if (regionObj != null)
            {
                GameAssembly.CallInstanceMethod(serverManager, "AddOrUpdateRegion", new object?[] { regionObj });
                GameAssembly.CallInstanceMethod(serverManager, "SetRegion", new object?[] { regionObj });
                FileLogger.Info("[LobbyJoiner] Custom region set successfully");
                return true;
            }

            FileLogger.Warn("[LobbyJoiner] Failed to create custom region object");
            return false;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[LobbyJoiner] SetRegion failed: {ex.Message}");
            return false;
        }
    }

    private static bool MatchesRegion(string requested, string actual)
    {
        if (string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase))
            return true;

        var req = requested.ToUpperInvariant();
        return req switch
        {
            "NA" => actual.StartsWith("North", StringComparison.OrdinalIgnoreCase),
            "EU" => actual.StartsWith("Europe", StringComparison.OrdinalIgnoreCase),
            "ASIA" => actual.StartsWith("Asia", StringComparison.OrdinalIgnoreCase),
            "OCE" or "OCEANIA" or "AUSTRALIA" => actual.StartsWith("Australia", StringComparison.OrdinalIgnoreCase),
            "SA" => actual.StartsWith("South America", StringComparison.OrdinalIgnoreCase),
            _ => req.Replace(" ", "").Equals(actual.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
        };
    }

    private int DecodeCode(string code)
    {
        try
        {
            var gameCodeType = GameAssembly.Type("InnerNet.GameCode");
            if (gameCodeType == null)
            {
                FileLogger.Error($"[LobbyJoiner] InnerNet.GameCode type not found - cannot decode code '{code}'");
                return 0;
            }
            var result = GameAssembly.CallStaticMethod(gameCodeType, "GameNameToInt",
                new object?[] { code }, new[] { typeof(string) });
            if (result == null)
            {
                FileLogger.Error($"[LobbyJoiner] GameNameToInt returned null for code '{code}'");
                return 0;
            }
            return GameAssembly.ToInt(result);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[LobbyJoiner] DecodeCode failed: {ex.Message}");
            return 0;
        }
    }

    private static string CurrentLobbyCode()
    {
        try
        {
            var client = GameAssembly.AmongUsClient();
            if (client == null) return "";
            var gameId = GameAssembly.ToInt(GameAssembly.GetInstanceProp(client, "GameId"));
            var codeType = GameAssembly.Type("InnerNet.GameCode");
            return GameAssembly.ToStr(GameAssembly.CallStaticMethod(codeType, "IntToGameName",
                new object[] { gameId }, new[] { typeof(int) }));
        }
        catch { return ""; }
    }
}
