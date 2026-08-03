using System.Collections.Concurrent;

namespace AmongApi.Services;

public record JoinResult(bool Success, string? Error);

/// <summary>
/// In-game direct lobby join. Follows the research doc §5.5 ordered sequence:
/// ServerManager (via DestroyableSingleton&lt;ServerManager&gt;.Instance) → build a
/// StaticHttpRegionInfo + ServerInfo array → AddOrUpdateRegion → SetRegion →
/// GameCode.GameNameToInt → AmongUsClient.Instance.CoJoinOnlineGameFromCode →
/// MonoBehaviour.StartCoroutine. All game access is reflection via GameAssembly
/// (zero game-assembly references; the csproj is untouched).
///
/// THREADING: Unity APIs and coroutine starts must run on the Unity main thread,
/// but pipe handlers arrive on the plugin's async loop thread. This implementation
/// uses the pragmatic approach sanctioned for the task: JoinAsync only validates and
/// enqueues the request; a background pump (100ms tick) drains the queue and runs the
/// full region-set + join sequence. The pump thread is NOT the Unity main thread.
/// StartCoroutine is called reflection-only and any failure is caught, logged and
/// returned as a failed JoinResult (the launcher retries). A production improvement
/// is a plugin-owned MonoBehaviour created at runtime whose Update drains the queue
/// on the main thread; that requires a Unity compile reference and is out of scope
/// for the zero-game-reference constraint.
/// </summary>
public class LobbyJoiner : IDisposable
{
    private const int PumpIntervalMs = 100;
    private const int DispatchTimeoutMs = 30_000;

    private readonly ManualLogSource _log;
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

    public LobbyJoiner(ManualLogSource log)
    {
        _log = log;
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Validates the request, enqueues it for the pump, and returns the pump's
    /// actual join outcome (Success = the join coroutine was started).
    /// </summary>
    public async Task<JoinResult> JoinAsync(string code, string region, string regionIp, int regionPort)
    {
        code = code.Trim();
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
            return await request.Tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(DispatchTimeoutMs));
        }
        catch (TimeoutException)
        {
            return new JoinResult(false, "Join dispatch timed out");
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
        while (_queue.TryDequeue(out var request))
            request.Tcs?.TrySetResult(new JoinResult(false, "Joiner disposed"));
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
                    JoinResult result;
                    try
                    {
                        result = ExecuteJoin(request);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"[LobbyJoiner] Join execution failed: {ex.Message}");
                        result = new JoinResult(false, ex.Message);
                    }
                    request.Tcs.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[LobbyJoiner] Pump tick failed: {ex.Message}");
            }
            try
            {
                await Task.Delay(PumpIntervalMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs the full §5.5 join sequence via reflection. Must not throw — all failures
    /// become a failed JoinResult with a message.
    /// </summary>
    private JoinResult ExecuteJoin(JoinRequest request)
    {
        _log.LogInfo($"[LobbyJoiner] Joining lobby '{request.Code}' via region '{request.Region}' ({request.RegionIp}:{request.RegionPort})...");

        if (AlreadyInLobby())
            return new JoinResult(false, "Already in a lobby; leaving before joining is not supported");

        // 1. ServerManager singleton is inherited: DestroyableSingleton<ServerManager>.Instance.
        var serverManagerType = GameAssembly.Type("ServerManager");
        var serverManager = serverManagerType?.BaseType == null
            ? null
            : GameAssembly.GetStaticProp(serverManagerType.BaseType, "Instance");
        if (serverManager == null)
            return new JoinResult(false, "ServerManager singleton unavailable");

        // 2-4. Build + select the custom region. If no region endpoint was provided by the
        //      launcher, degrade gracefully to whatever region the game currently has selected.
        if (request.RegionIp.Length > 0 || request.Region.Length > 0)
        {
            // Build the region: StaticHttpRegionInfo(name, NoTranslation, pingServer, servers)
            // with servers = [ ServerInfo("Http-1", "https://host", port, useDtls:false) ].
            var host = StripScheme(request.RegionIp);
            var ip = WithScheme(request.RegionIp);
            var port = (ushort)(request.RegionPort > 0 ? request.RegionPort : 443);
            var regionName = request.Region.Length > 0 ? request.Region : host;

            var serverInfoType = GameAssembly.Type("ServerInfo");
            var serverInfo = GameAssembly.CreateInstance(serverInfoType,
                new object?[] { "Http-1", ip, port, false });
            if (serverInfo == null)
                return new JoinResult(false, "Failed to construct ServerInfo");
            _log.LogInfo($"[LobbyJoiner] ServerInfo constructed ({ip}:{port}).");

            // Il2CppReferenceArray<ServerInfo> is a real interop type; build it via its (T[]) ctor.
            var arrayType = GameAssembly.GenericType("Il2CppReferenceArray`1", serverInfoType!);
            var plainArray = Array.CreateInstance(serverInfoType!, 1);
            plainArray.SetValue(serverInfo, 0);
            var servers = GameAssembly.CreateInstance(arrayType, new object?[] { plainArray });
            if (servers == null)
                return new JoinResult(false, "Failed to construct ServerInfo array");

            var staticHttpRegionInfoType = GameAssembly.Type("StaticHttpRegionInfo");
            var noTranslation = GameAssembly.EnumValue(GameAssembly.Type("StringNames"), "NoTranslation");
            var regionObj = GameAssembly.CreateInstance(staticHttpRegionInfoType,
                new object?[] { regionName, noTranslation, host, servers, null });
            if (regionObj == null)
                return new JoinResult(false, "Failed to construct StaticHttpRegionInfo");
            _log.LogInfo($"[LobbyJoiner] Region '{regionName}' constructed.");

            // AddOrUpdateRegion + SetRegion take IRegionInfo, but the interop StaticHttpRegionInfo
            // does NOT CLR-inherit IRegionInfo. Wrap the same native object in an IRegionInfo
            // wrapper (via its public Pointer) so the reflection call passes the type check.
            var regionInfoType = GameAssembly.Type("IRegionInfo");
            var pointer = GameAssembly.GetInstanceProp(regionObj, "Pointer");
            if (pointer is not IntPtr nativePointer)
                return new JoinResult(false, "Could not read region native pointer");
            var regionAsInfo = GameAssembly.CreateInstance(regionInfoType, new object?[] { nativePointer });
            if (regionAsInfo == null)
                return new JoinResult(false, "Failed to wrap region as IRegionInfo");

            if (!GameAssembly.HasInstanceMethod(serverManager, "AddOrUpdateRegion", 1) ||
                !GameAssembly.HasInstanceMethod(serverManager, "SetRegion", 1))
                return new JoinResult(false, "ServerManager region methods unavailable");

            GameAssembly.CallInstanceMethod(serverManager, "AddOrUpdateRegion", new object?[] { regionAsInfo });
            GameAssembly.CallInstanceMethod(serverManager, "SetRegion", new object?[] { regionAsInfo });
            _log.LogInfo($"[LobbyJoiner] Custom region '{regionName}' registered and selected.");
        }
        else
        {
            _log.LogInfo("[LobbyJoiner] No region endpoint provided; using the game's currently selected region.");
        }

        // 5. Decode the 6-char code to an int lobby id.
        var gameCodeType = GameAssembly.Type("InnerNet.GameCode");
        var gameIdObj = GameAssembly.CallStaticMethod(gameCodeType, "GameNameToInt",
            new object?[] { request.Code }, new[] { typeof(string) });
        if (gameIdObj == null)
            return new JoinResult(false, "Failed to decode lobby code");
        var gameId = GameAssembly.ToInt(gameIdObj);
        _log.LogInfo($"[LobbyJoiner] Code '{request.Code}' -> gameId {gameId}.");

        // 6. Join via coroutine: CoJoinOnlineGameFromCode(gameId, fromEnterCode:false) + StartCoroutine.
        var amongUsClientType = GameAssembly.Type("AmongUsClient");
        var client = GameAssembly.GetStaticProp(amongUsClientType, "Instance");
        if (client == null)
            return new JoinResult(false, "AmongUsClient not available (not in main menu?)");

        var enumerator = GameAssembly.CallInstanceMethod(client, "CoJoinOnlineGameFromCode",
            new object?[] { gameId, false }, new[] { typeof(int), typeof(bool) });
        if (enumerator == null)
            return new JoinResult(false, "Join coroutine could not be created");

        // StartCoroutine has no compile-time-expressible parameter type, so the best-match
        // overload resolution picks StartCoroutine(Il2CppSystem.Collections.IEnumerator).
        var coroutine = GameAssembly.CallInstanceMethod(client, "StartCoroutine", new object?[] { enumerator });
        if (coroutine == null)
            return new JoinResult(false, "StartCoroutine failed (main-thread requirement not met?)");

        _log.LogInfo("[LobbyJoiner] Join coroutine started. Lobby join dispatched.");
        return new JoinResult(true, null);
    }

    private static bool AlreadyInLobby()
    {
        var lobbyBehaviour = GameAssembly.Type("LobbyBehaviour");
        if (GameAssembly.GetStaticProp(lobbyBehaviour, "Instance") != null)
            return true;

        var amongUsClient = GameAssembly.Type("AmongUsClient");
        var client = GameAssembly.GetStaticProp(amongUsClient, "Instance");
        if (client == null)
            return false;

        var gameStateEnum = GameAssembly.Type("InnerNet.InnerNetClient")?.GetNestedType("GameStates");
        var state = GameAssembly.GetInstanceProp(client, "GameState");
        return GameAssembly.EnumEquals(state, GameAssembly.EnumValue(gameStateEnum, "Joined"));
    }

    private static string StripScheme(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        const string https = "https://";
        const string http = "http://";
        if (trimmed.StartsWith(https, StringComparison.OrdinalIgnoreCase))
            return trimmed[https.Length..];
        if (trimmed.StartsWith(http, StringComparison.OrdinalIgnoreCase))
            return trimmed[http.Length..];
        return trimmed;
    }

    private static string WithScheme(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return "https://" + trimmed;
    }
}
