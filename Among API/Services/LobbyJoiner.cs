using System.Collections.Concurrent;

namespace AmongApi.Services;

public record JoinResult(bool Success, string? Error);

public class LobbyJoiner : IDisposable
{
    private const int JoinConfirmTimeoutMs = 45_000;
    private const int PollIntervalMs = 500;
    private const int GameReadyWaitMs = 30_000;
    private const int PumpMaxDurationMs = GameReadyWaitMs + JoinConfirmTimeoutMs;

    private readonly ConcurrentQueue<JoinRequest> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;

    private sealed class JoinRequest
    {
        public string Code = "";
        public string Region = "";
        public string RegionIp = "";
        public int RegionPort;
        public TaskCompletionSource<JoinResult>? Tcs;
    }

    public LobbyJoiner(ManualLogSource _)
    {
        _pumpTask = Task.Run(PumpAsync);
    }

    public async Task<JoinResult> JoinAsync(string code, string region, string regionIp, int regionPort)
    {
        code = code.Trim().ToUpperInvariant();
        region = region.Trim();
        regionIp = regionIp.Trim();

        if (code.Length == 0)
            return new JoinResult(false, "Empty lobby code");

        var request = new JoinRequest
        {
            Code = code,
            Region = region,
            RegionIp = regionIp,
            RegionPort = regionPort,
            Tcs = new TaskCompletionSource<JoinResult>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
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

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        while (_queue.TryDequeue(out var req))
            req.Tcs?.TrySetResult(new JoinResult(false, "Joiner disposed"));
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
                    FileLogger.Info($"[LobbyJoiner] Processing join for '{request.Code}'");
                    var result = await ProcessJoinAsync(request, cts.Token);
                    request.Tcs.TrySetResult(result);
                    FileLogger.Info($"[LobbyJoiner] Result: success={result.Success} error={result.Error ?? "none"}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error($"[LobbyJoiner] Pump failed: {ex.Message}");
            }

            try { await Task.Delay(100, cts.Token); }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<JoinResult> ProcessJoinAsync(JoinRequest request, CancellationToken ct)
    {
        FileLogger.Info("[LobbyJoiner] Waiting for game to be ready...");
        if (!await WaitForGameReady(ct))
        {
            return new JoinResult(false, "Game did not reach ready state");
        }

        JoinResult startResult;
        try
        {
            await MainThreadDispatcher.EnqueueAsync(() =>
            {
                if (GameAssembly.InLobby())
                {
                    FileLogger.Info("[LobbyJoiner] In lobby, leaving...");
                    LeaveLobby();
                }
            });

            await Task.Delay(1500, ct);

            startResult = await MainThreadDispatcher.EnqueueAsync(() => ExecuteJoin(request));
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[LobbyJoiner] Dispatch failed: {ex.Message}");
            return new JoinResult(false, $"Dispatch failed: {ex.Message}");
        }

        if (!startResult.Success)
            return startResult;

        FileLogger.Info("[LobbyJoiner] Waiting for lobby...");
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(JoinConfirmTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(PollIntervalMs, ct);

            if (GameAssembly.InLobby())
            {
                var code = CurrentLobbyCode();
                FileLogger.Info($"[LobbyJoiner] InLobby=true, code={code}");
                return new JoinResult(true, null);
            }
        }

        return new JoinResult(false, "Join timed out");
    }

    private async Task<bool> WaitForGameReady(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(GameReadyWaitMs);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);

            var client = GameAssembly.AmongUsClient();
            if (client == null) continue;

            try
            {
                var gameStateEnum = GameAssembly.Type("InnerNet.InnerNetClient")?.GetNestedType("GameStates");
                if (gameStateEnum == null) continue;

                var state = GameAssembly.GetInstanceProp(client, "GameState");
                if (state == null) continue;

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
        }

        FileLogger.Warn("[LobbyJoiner] Game ready wait timed out, proceeding anyway");
        return true;
    }

    private JoinResult ExecuteJoin(JoinRequest request)
    {
        FileLogger.Info($"[LobbyJoiner] ExecuteJoin: code={request.Code} region={request.Region} ip={request.RegionIp}:{request.RegionPort}");

        if (GameAssembly.InLobby())
        {
            FileLogger.Info("[LobbyJoiner] In lobby, leaving...");
            LeaveLobby();
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

            var serverManager = GameAssembly.GetStaticProp(serverManagerType, "Instance");
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
