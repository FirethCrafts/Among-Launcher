using System.Collections.Concurrent;

namespace AmongApi.Services;

public record JoinResult(bool Success, string? Error);

/// <summary>
/// Joins a lobby using pure reflection - no IL2CPP compile-time types needed.
/// Waits for the game to be fully loaded before attempting join.
/// </summary>
public class LobbyJoiner : IDisposable
{
    private const int JoinConfirmTimeoutMs = 45_000;
    private const int PollIntervalMs = 500;
    private const int GameReadyWaitMs = 30_000;

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
            return await request.Tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(JoinConfirmTimeoutMs + 5000));
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
        // Wait for game to be fully loaded before dispatching join
        FileLogger.Info("[LobbyJoiner] Waiting for game to be ready...");
        if (!await WaitForGameReady(ct))
        {
            return new JoinResult(false, "Game did not reach ready state");
        }

        JoinResult startResult;
        try
        {
            startResult = await MainThreadDispatcher.EnqueueAsync(() => ExecuteJoin(request));
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[LobbyJoiner] Dispatch failed: {ex.Message}");
            return new JoinResult(false, $"Dispatch failed: {ex.Message}");
        }

        if (!startResult.Success)
            return startResult;

        // Poll for lobby
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

    /// <summary>
    /// Waits until the game has AmongUsClient and is not in a loading/connecting state.
    /// </summary>
    private async Task<bool> WaitForGameReady(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(GameReadyWaitMs);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);

            var client = GameAssembly.AmongUsClient();
            if (client == null) continue;

            // Check GameState - we want it to be "NotJoined" or in a state where we can join
            try
            {
                var gameStateEnum = GameAssembly.Type("InnerNet.InnerNetClient")?.GetNestedType("GameStates");
                if (gameStateEnum == null) continue;

                var state = GameAssembly.GetInstanceProp(client, "GameState");
                if (state == null) continue;

                var notJoined = GameAssembly.EnumValue(gameStateEnum, "NotJoined");
                var joined = GameAssembly.EnumValue(gameStateEnum, "Joined");

                // If already in a lobby, that's fine
                if (GameAssembly.EnumEquals(state, joined) && GameAssembly.InLobby())
                {
                    FileLogger.Info("[LobbyJoiner] Game ready: already in lobby");
                    return true;
                }

                // If at main menu, we can join
                if (GameAssembly.EnumEquals(state, notJoined))
                {
                    FileLogger.Info("[LobbyJoiner] Game ready: at main menu (NotJoined)");
                    return true;
                }

                // Otherwise keep waiting (connecting, loading, etc.)
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

        // Leave current lobby if in one
        if (GameAssembly.InLobby())
        {
            FileLogger.Info("[LobbyJoiner] In lobby, leaving...");
            LeaveLobby();
            Thread.Sleep(1500);
        }

        // Set region if provided
        if (!string.IsNullOrEmpty(request.RegionIp) || !string.IsNullOrEmpty(request.Region))
        {
            SetRegion(request);
        }

        // Decode code
        var gameId = DecodeCode(request.Code);
        if (gameId == 0)
        {
            FileLogger.Error($"[LobbyJoiner] Failed to decode code: {request.Code}");
            return new JoinResult(false, "Failed to decode lobby code");
        }
        FileLogger.Info($"[LobbyJoiner] Decoded {request.Code} -> {gameId}");

        // Get client
        var client = GameAssembly.AmongUsClient();
        if (client == null)
        {
            FileLogger.Error("[LobbyJoiner] AmongUsClient is null");
            return new JoinResult(false, "AmongUsClient unavailable");
        }

        // Try to join using reflection only
        return TryJoinViaReflection(client, gameId, request.Code);
    }

    private JoinResult TryJoinViaReflection(object client, int gameId, string code)
    {
        var clientType = GameAssembly.Type("AmongUsClient");
        FileLogger.Info($"[LobbyJoiner] AmongUsClient type: {clientType?.FullName ?? "null"}");

        if (clientType == null)
        {
            return new JoinResult(false, "AmongUsClient type not found");
        }

        // Log all methods
        var allMethods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        var methodNames = allMethods.Where(m => !m.IsSpecialName && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
            .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
        FileLogger.Info($"[LobbyJoiner] Methods: {string.Join(" | ", methodNames)}");

        // Try each join method using GameAssembly.CallInstanceMethod which handles IL2CPP type marshalling
        string[] joinMethods = { "CoJoinOnlineGameFromCode", "JoinGame", "CoJoinOnline", "JoinOnlineGame", "StartGame" };

        foreach (var methodName in joinMethods)
        {
            if (!allMethods.Any(m => m.Name == methodName)) continue;

            FileLogger.Info($"[LobbyJoiner] Trying {methodName}...");

            // Try CoJoinOnlineGameFromCode(int, bool)
            if (methodName == "CoJoinOnlineGameFromCode")
            {
                try
                {
                    var result = GameAssembly.CallInstanceMethod(client, methodName, new object[] { gameId, false },
                        new[] { typeof(int), typeof(bool) });
                    FileLogger.Info($"[LobbyJoiner] {methodName} returned: {result}");
                    if (result != null) return StartCoroutine(client, clientType, result);
                }
                catch (Exception ex) { FileLogger.Warn($"[LobbyJoiner] {methodName} failed: {ex.Message}"); }
            }

            // Try JoinGame(string)
            if (methodName == "JoinGame")
            {
                try
                {
                    var result = GameAssembly.CallInstanceMethod(client, methodName, new object[] { code },
                        new[] { typeof(string) });
                    FileLogger.Info($"[LobbyJoiner] JoinGame(string) returned: {result}");
                    if (result != null) return StartCoroutine(client, clientType, result);
                }
                catch (Exception ex) { FileLogger.Warn($"[LobbyJoiner] JoinGame(string) failed: {ex.Message}"); }

                try
                {
                    var result = GameAssembly.CallInstanceMethod(client, methodName, new object[] { gameId },
                        new[] { typeof(int) });
                    FileLogger.Info($"[LobbyJoiner] JoinGame(int) returned: {result}");
                    if (result != null) return StartCoroutine(client, clientType, result);
                }
                catch (Exception ex) { FileLogger.Warn($"[LobbyJoiner] JoinGame(int) failed: {ex.Message}"); }
            }

            // Try other methods with no args
            if (methodName != "CoJoinOnlineGameFromCode" && methodName != "JoinGame")
            {
                try
                {
                    var result = GameAssembly.CallInstanceMethod(client, methodName);
                    FileLogger.Info($"[LobbyJoiner] {methodName} returned: {result}");
                    if (result != null) return StartCoroutine(client, clientType, result);
                }
                catch (Exception ex) { FileLogger.Warn($"[LobbyJoiner] {methodName} failed: {ex.Message}"); }
            }
        }

        FileLogger.Error("[LobbyJoiner] All join methods failed");
        return new JoinResult(false, "No working join method found");
    }

    private JoinResult StartCoroutine(object client, Type clientType, object enumerator)
    {
        try
        {
            // Search entire inheritance chain for StartCoroutine
            Type? current = clientType;
            MethodInfo? startMethod = null;
            while (current != null && startMethod == null)
            {
                startMethod = current.GetMethod("StartCoroutine",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(IEnumerator) }, null);
                if (startMethod != null)
                {
                    FileLogger.Info($"[LobbyJoiner] Found StartCoroutine on {current.Name}");
                    break;
                }
                current = current.BaseType;
            }

            if (startMethod == null)
            {
                // Also try the Il2Cpp MonoBehaviour type
                var monoBehaviourType = GameAssembly.Type("UnityEngine.MonoBehaviour");
                if (monoBehaviourType != null)
                {
                    startMethod = monoBehaviourType.GetMethod("StartCoroutine",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(IEnumerator) }, null);
                    if (startMethod != null)
                        FileLogger.Info($"[LobbyJoiner] Found StartCoroutine on MonoBehaviour");
                }
            }

            if (startMethod == null)
            {
                FileLogger.Warn("[LobbyJoiner] StartCoroutine not found anywhere in hierarchy");
                return new JoinResult(false, "StartCoroutine not found");
            }

            var coroutine = startMethod.Invoke(client, new object[] { enumerator });
            FileLogger.Info($"[LobbyJoiner] StartCoroutine succeeded: {coroutine != null}");
            return new JoinResult(true, null);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[LobbyJoiner] StartCoroutine failed: {ex.Message}");
            return new JoinResult(false, $"StartCoroutine failed: {ex.Message}");
        }
    }

    private static MethodInfo? FindMethod(Type type, string name, Type paramType)
    {
        var current = type;
        while (current != null)
        {
            var method = current.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { paramType }, null);
            if (method != null) return method;
            current = current.BaseType;
        }
        return null;
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

    private void SetRegion(JoinRequest request)
    {
        try
        {
            var serverManagerType = GameAssembly.Type("ServerManager");
            if (serverManagerType?.BaseType == null) return;

            var serverManager = GameAssembly.GetStaticProp(serverManagerType.BaseType, "Instance");
            if (serverManager == null)
            {
                FileLogger.Warn("[LobbyJoiner] ServerManager unavailable");
                return;
            }

            var regionIp = request.RegionIp;
            var regionPort = request.RegionPort > 0 ? request.RegionPort : 443;
            var regionName = request.Region.Length > 0 ? request.Region : regionIp;

            if (string.IsNullOrEmpty(regionIp)) return;

            if (!regionIp.StartsWith("http"))
                regionIp = "https://" + regionIp;

            FileLogger.Info($"[LobbyJoiner] Setting region: {regionName} @ {regionIp}:{regionPort}");

            var serverInfoType = GameAssembly.Type("ServerInfo");
            if (serverInfoType == null)
            {
                FileLogger.Warn("[LobbyJoiner] ServerInfo type not found");
                return;
            }

            var serverInfo = GameAssembly.CreateInstance(serverInfoType,
                new object?[] { "Http-1", regionIp, (ushort)regionPort, false });
            if (serverInfo == null)
            {
                FileLogger.Warn("[LobbyJoiner] Failed to create ServerInfo");
                return;
            }

            var arrayType = GameAssembly.GenericType("Il2CppReferenceArray`1", serverInfoType);
            var plainArray = Array.CreateInstance(serverInfoType, 1);
            plainArray.SetValue(serverInfo, 0);
            var servers = GameAssembly.CreateInstance(arrayType, new object?[] { plainArray });
            if (servers == null)
            {
                FileLogger.Warn("[LobbyJoiner] Failed to create server array");
                return;
            }

            var staticHttpType = GameAssembly.Type("StaticHttpRegionInfo");
            var noTranslation = GameAssembly.EnumValue(GameAssembly.Type("StringNames"), "NoTranslation");
            var regionObj = GameAssembly.CreateInstance(staticHttpType,
                new object?[] { regionName, noTranslation, regionIp, servers, null });
            if (regionObj == null)
            {
                FileLogger.Warn("[LobbyJoiner] Failed to create region info");
                return;
            }

            var regionInfoType = GameAssembly.Type("IRegionInfo");
            var pointer = GameAssembly.GetInstanceProp(regionObj, "Pointer");
            if (pointer is not IntPtr nativePtr)
            {
                FileLogger.Warn("[LobbyJoiner] Failed to get region pointer");
                return;
            }

            var regionAsInfo = GameAssembly.CreateInstance(regionInfoType, new object?[] { nativePtr });
            if (regionAsInfo == null)
            {
                FileLogger.Warn("[LobbyJoiner] Failed to wrap as IRegionInfo");
                return;
            }

            GameAssembly.CallInstanceMethod(serverManager, "AddOrUpdateRegion", new object?[] { regionAsInfo });
            GameAssembly.CallInstanceMethod(serverManager, "SetRegion", new object?[] { regionAsInfo });

            FileLogger.Info("[LobbyJoiner] Region set successfully");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[LobbyJoiner] SetRegion failed: {ex.Message}");
        }
    }

    private int DecodeCode(string code)
    {
        try
        {
            var gameCodeType = GameAssembly.Type("InnerNet.GameCode");
            var result = GameAssembly.CallStaticMethod(gameCodeType, "GameNameToInt",
                new object?[] { code }, new[] { typeof(string) });
            return result != null ? GameAssembly.ToInt(result) : 0;
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
