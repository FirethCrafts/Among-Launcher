using System.Collections.Concurrent;

namespace AmongApi.Services;

public record JoinResult(bool Success, string? Error);

/// <summary>
/// In-game direct lobby join. Dispatches the full join sequence to the Unity
/// main thread via SynchronizationContext.Post, ensuring StartCoroutine and
/// other Unity APIs are called safely.
/// </summary>
public class LobbyJoiner : IDisposable
{
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
                    FileLogger.Info($"[LobbyJoiner] Dequeued join request for '{request.Code}'");

                    // Dispatch the entire join sequence to the Unity main thread
                    var syncCtx = SynchronizationContext.Current;
                    if (syncCtx != null)
                    {
                        FileLogger.Info("[LobbyJoiner] Dispatching join to main thread via SynchronizationContext");
                        JoinResult result = null!;
                        var done = new ManualResetEventSlim(false);
                        syncCtx.Post(_ =>
                        {
                            try
                            {
                                result = ExecuteJoin(request);
                            }
                            catch (Exception ex)
                            {
                                FileLogger.Error($"[LobbyJoiner] ExecuteJoin on main thread failed: {ex.Message}");
                                result = new JoinResult(false, ex.Message);
                            }
                            finally
                            {
                                done.Set();
                            }
                        }, null);

                        // Wait for the main thread to finish (with timeout)
                        if (!done.Wait(TimeSpan.FromSeconds(15)))
                        {
                            FileLogger.Warn("[LobbyJoiner] Main thread dispatch timed out after 15s");
                            result = new JoinResult(false, "Main thread dispatch timed out");
                        }

                        request.Tcs.TrySetResult(result);
                    }
                    else
                    {
                        FileLogger.Warn("[LobbyJoiner] No SynchronizationContext; running join on pump thread (may fail)");
                        JoinResult result;
                        try
                        {
                            result = ExecuteJoin(request);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Error($"[LobbyJoiner] Join execution failed: {ex.Message}");
                            result = new JoinResult(false, ex.Message);
                        }
                        request.Tcs.TrySetResult(result);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error($"[LobbyJoiner] Pump tick failed: {ex.Message}");
            }
            try
            {
                await Task.Delay(100, cts.Token);
            }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private JoinResult ExecuteJoin(JoinRequest request)
    {
        FileLogger.Info($"[LobbyJoiner] ExecuteJoin: code={request.Code}, region={request.Region}, ip={request.RegionIp}:{request.RegionPort}");
        FileLogger.Info($"[LobbyJoiner] Thread: {Environment.CurrentManagedThreadId}");

        if (AlreadyInLobby())
        {
            FileLogger.Warn("[LobbyJoiner] Already in a lobby");
            return new JoinResult(false, "Already in a lobby");
        }

        // 1. Get ServerManager
        var serverManagerType = GameAssembly.Type("ServerManager");
        var serverManager = serverManagerType?.BaseType == null
            ? null
            : GameAssembly.GetStaticProp(serverManagerType.BaseType, "Instance");
        FileLogger.Info($"[LobbyJoiner] ServerManager: {serverManager != null}");
        if (serverManager == null)
            return new JoinResult(false, "ServerManager unavailable");

        // 2-4. Set custom region if provided
        if (request.RegionIp.Length > 0 || request.Region.Length > 0)
        {
            var host = StripScheme(request.RegionIp);
            var ip = WithScheme(request.RegionIp);
            var port = (ushort)(request.RegionPort > 0 ? request.RegionPort : 443);
            var regionName = request.Region.Length > 0 ? request.Region : host;

            var serverInfoType = GameAssembly.Type("ServerInfo");
            var serverInfo = GameAssembly.CreateInstance(serverInfoType, new object?[] { "Http-1", ip, port, false });
            if (serverInfo == null) return new JoinResult(false, "Failed to construct ServerInfo");

            var arrayType = GameAssembly.GenericType("Il2CppReferenceArray`1", serverInfoType!);
            var plainArray = Array.CreateInstance(serverInfoType!, 1);
            plainArray.SetValue(serverInfo, 0);
            var servers = GameAssembly.CreateInstance(arrayType, new object?[] { plainArray });
            if (servers == null) return new JoinResult(false, "Failed to construct ServerInfo array");

            var staticHttpRegionInfoType = GameAssembly.Type("StaticHttpRegionInfo");
            var noTranslation = GameAssembly.EnumValue(GameAssembly.Type("StringNames"), "NoTranslation");
            var regionObj = GameAssembly.CreateInstance(staticHttpRegionInfoType,
                new object?[] { regionName, noTranslation, host, servers, null });
            if (regionObj == null) return new JoinResult(false, "Failed to construct StaticHttpRegionInfo");

            var regionInfoType = GameAssembly.Type("IRegionInfo");
            var pointer = GameAssembly.GetInstanceProp(regionObj, "Pointer");
            if (pointer is not IntPtr nativePointer)
                return new JoinResult(false, "Could not read region native pointer");
            var regionAsInfo = GameAssembly.CreateInstance(regionInfoType, new object?[] { nativePointer });
            if (regionAsInfo == null) return new JoinResult(false, "Failed to wrap region as IRegionInfo");

            GameAssembly.CallInstanceMethod(serverManager, "AddOrUpdateRegion", new object?[] { regionAsInfo });
            GameAssembly.CallInstanceMethod(serverManager, "SetRegion", new object?[] { regionAsInfo });
            FileLogger.Info($"[LobbyJoiner] Region '{regionName}' set");
        }

        // 5. Decode lobby code
        var gameCodeType = GameAssembly.Type("InnerNet.GameCode");
        var gameIdObj = GameAssembly.CallStaticMethod(gameCodeType, "GameNameToInt",
            new object?[] { request.Code }, new[] { typeof(string) });
        if (gameIdObj == null) return new JoinResult(false, "Failed to decode lobby code");
        var gameId = GameAssembly.ToInt(gameIdObj);
        FileLogger.Info($"[LobbyJoiner] Code '{request.Code}' -> gameId {gameId}");

        // 6. Start join coroutine
        var client = GameAssembly.AmongUsClient();
        FileLogger.Info($"[LobbyJoiner] AmongUsClient: {client != null}");
        if (client == null)
            return new JoinResult(false, "AmongUsClient not available");

        var enumerator = GameAssembly.CallInstanceMethod(client, "CoJoinOnlineGameFromCode",
            new object?[] { gameId, false }, new[] { typeof(int), typeof(bool) });
        FileLogger.Info($"[LobbyJoiner] CoJoinOnlineGameFromCode: {enumerator != null}");
        if (enumerator == null)
            return new JoinResult(false, "Join coroutine could not be created");

        var coroutine = GameAssembly.CallInstanceMethod(client, "StartCoroutine", new object?[] { enumerator });
        FileLogger.Info($"[LobbyJoiner] StartCoroutine: {coroutine != null}");
        if (coroutine == null)
            return new JoinResult(false, "StartCoroutine failed");

        FileLogger.Info("[LobbyJoiner] Join coroutine started successfully");
        return new JoinResult(true, null);
    }

    private static bool AlreadyInLobby() => GameAssembly.InLobby();

    private static string StripScheme(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed["https://".Length..];
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return trimmed["http://".Length..];
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
