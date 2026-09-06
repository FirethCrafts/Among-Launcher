namespace AmongApi.Services;

public record LobbyInfo(
    string Code,
    string Region,
    string RegionIp,
    int RegionPort,
    string Host,
    int PlayerCount,
    int MaxPlayers = 15,
    List<string>? PlayerNames = null,
    List<int>? PlayerLevels = null,
    List<int>? PlayerPings = null,
    string GameVersion = "",
    string MapName = "",
    string Language = "",
    string ChatType = ""
);
public record PlayerInfo(string PlayerName, int PlayerCount);

/// <summary>
/// Polls the game for lobby / player state via reflection (GameAssembly) and
/// raises transition events. Region and host names are read from the game's
/// ServerManager / PlayerControl (falling back to "UNKNOWN" when unavailable).
/// </summary>
public class GameStateTracker : IDisposable
{
    private const int PollIntervalMs = 500;

    private const int InitialDelayMs = 5000;
    private const int ExceptionLogCooldownMs = 5000;

    private readonly ManualLogSource _log;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private bool _wasInLobby;
    private bool _lastWasHost;
    private int _lastPlayerCount = -1;
    private DateTime _lastExceptionLogTime = DateTime.MinValue;

    public event EventHandler<LobbyInfo>? LobbyCreated;
    public event EventHandler<string>? LobbyClosed;
    public event EventHandler<PlayerInfo>? PlayerJoined;
    public event EventHandler<PlayerInfo>? PlayerLeft;

    public GameStateTracker(ManualLogSource log) => _log = log;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(InitialDelayMs, _cts.Token); } catch { return; }
            await LoopAsync();
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task LoopAsync()
    {
        var cts = _cts;
        if (cts == null)
            return;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastExceptionLogTime).TotalMilliseconds >= ExceptionLogCooldownMs)
                {
                    _lastExceptionLogTime = now;
                    _log.LogWarning($"[GameStateTracker] Tick failed: {ex}");
                }
            }
            try
            {
                await Task.Delay(PollIntervalMs, cts.Token);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Tick()
    {
        try
        {
            bool inLobby;
            string code;
            int count;
            bool isHost;

            try
            {
                inLobby = IsInLobby();
                code = LobbyCode();
                count = PlayerCount();
                isHost = IsHost();
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[GameStateTracker] State read failed: {ex}");
                return;
            }

            lock (_lock)
            {
                try
                {
                    if (inLobby)
                        _lastWasHost = isHost;

                    if (inLobby && !_wasInLobby)
                    {
                        if (_lastWasHost)
                        {
                            var region = "UNKNOWN";
                            var host = "UNKNOWN";
                            int maxPlayers = 15;
                            List<string>? playerNames = null;
                            List<int>? playerLevels = null;
                            List<int>? playerPings = null;
                            try { region = GameAssembly.CurrentRegionName(); } catch { }
                            try { host = GameAssembly.LocalPlayerName(); } catch { }
                            try { maxPlayers = MaxPlayers(); } catch { }
                            try { playerNames = GameAssembly.GetAllPlayerNames(); } catch { }
                            FileLogger.Info($"[GameStateTracker] Tick: Calling GetAllPlayerLevels...");
                            try { playerLevels = GetAllPlayerLevels(); } catch { }
                            FileLogger.Info($"[GameStateTracker] Tick: Calling GetAllPlayerPings...");
                            try { playerPings = GetAllPlayerPings(); } catch { }

                            var gameVersion = "";
                            var mapName = "";
                            var language = "";
                            var chatType = "";
                            try { gameVersion = GameAssembly.GameVersion(); } catch { }
                            try { mapName = GameAssembly.MapName(); } catch { }
                            try { language = GameAssembly.Language(); } catch { }
                            try { chatType = GameAssembly.ChatType(); } catch { }

                            var (regionIp, regionPort) = CurrentServerEndpoint();

                            _log.LogInfo($"[GameStateTracker] Lobby created (code {code}, region {region}, regionEndpoint {regionIp}:{regionPort}, host {host}, players {count}, maxPlayers {maxPlayers}).");
                            _lastPlayerCount = count >= 0 ? count : -1;
                            try { LobbyCreated?.Invoke(this, new LobbyInfo(code, region, regionIp, regionPort, host, count, maxPlayers, playerNames, playerLevels, playerPings, gameVersion, mapName, language, chatType)); } catch { }
                        }
                        else
                        {
                            _log.LogInfo("[GameStateTracker] Entered a lobby as a non-host; skipping lobby_created.");
                        }
                    }
                    else if (!inLobby && _wasInLobby)
                    {
                        if (_lastWasHost)
                        {
                            _log.LogInfo("[GameStateTracker] Lobby closed.");
                            _lastPlayerCount = -1;
                            try { LobbyClosed?.Invoke(this, ""); } catch { }
                        }
                        else
                        {
                            _log.LogInfo("[GameStateTracker] Left a lobby as a non-host; skipping lobby_closed.");
                        }
                        _lastWasHost = false;
                    }

                    if (inLobby && count >= 0)
                    {
                        if (_lastPlayerCount < 0)
                        {
                            _lastPlayerCount = count;
                        }
                        else if (count != _lastPlayerCount)
                        {
                            if (count > _lastPlayerCount)
                            {
                                _log.LogInfo($"[GameStateTracker] Player joined (count {count}).");
                                try { PlayerJoined?.Invoke(this, new PlayerInfo("<unknown>", count)); } catch { }
                            }
                            else
                            {
                                _log.LogInfo($"[GameStateTracker] Player left (count {count}).");
                                try { PlayerLeft?.Invoke(this, new PlayerInfo("<unknown>", count)); } catch { }
                            }
                            _lastPlayerCount = count;
                        }
                    }

                    _wasInLobby = inLobby;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"[GameStateTracker] Tick lock block failed: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[GameStateTracker] Tick failed: {ex}");
        }
    }

    private static bool IsInLobby() => GameAssembly.InLobby();

    /// <summary>
    /// True when the local client is the lobby host. Uses the research-verified
    /// signal AmongUsClient.Instance.HostId == InnerNetClient.CurrentClient
    /// (HostId is an instance property; CurrentClient is a static int on InnerNetClient).
    /// </summary>
    private static bool IsHost()
    {
        try
        {
            var client = GameAssembly.AmongUsClient();
            if (client == null)
            {
                FileLogger.Warn("[GameStateTracker] IsHost: AmongUsClient is null");
                return false;
            }

            // Try AmHost via GetInstanceMember (checks both properties and fields)
            var amHostObj = GameAssembly.GetInstanceMember(client, "AmHost");
            if (amHostObj != null)
            {
                var amHost = GameAssembly.ToBool(amHostObj);
                FileLogger.Info($"[GameStateTracker] IsHost: AmHost={amHost} (type={amHostObj.GetType().Name})");
                return amHost;
            }

            // Also try InnerNetClient.AmHost (might be on the base class)
            var innerNetClientType = GameAssembly.Type("InnerNet.InnerNetClient");
            if (innerNetClientType != null)
            {
                var amHostObj2 = GameAssembly.GetStaticMember(innerNetClientType, "AmHost");
                if (amHostObj2 != null)
                {
                    var amHost2 = GameAssembly.ToBool(amHostObj2);
                    FileLogger.Info($"[GameStateTracker] IsHost: InnerNetClient.AmHost={amHost2}");
                    return amHost2;
                }
            }

            // Fallback: HostId == CurrentClient
            if (innerNetClientType == null)
            {
                FileLogger.Warn("[GameStateTracker] IsHost fallback: InnerNetClient type is null, cannot determine host.");
                return false;
            }
            var hostIdObj = GameAssembly.GetInstanceMember(client, "HostId");
            var currentClientObj = GameAssembly.GetStaticMember(innerNetClientType, "CurrentClient");
            var hostId = GameAssembly.ToInt(hostIdObj);
            var currentClient = GameAssembly.ToInt(currentClientObj);
            FileLogger.Info($"[GameStateTracker] IsHost fallback: HostId={hostId}, CurrentClient={currentClient}");
            return currentClient >= 0 && hostId == currentClient;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[GameStateTracker] IsHost failed: {ex.Message}");
            return false;
        }
    }

    private static string LobbyCode()
    {
        var client = GameAssembly.AmongUsClient();
        if (client == null)
            return "";

        var gameId = GameAssembly.ToInt(GameAssembly.GetInstanceProp(client, "GameId"));
        var gameCode = GameAssembly.Type("InnerNet.GameCode");
        return GameAssembly.ToStr(GameAssembly.CallStaticMethod(gameCode, "IntToGameName", new object[] { gameId }, new[] { typeof(int) }));
    }

    private static int PlayerCount()
    {
        var gameData = GameAssembly.Type("GameData");
        var instance = GameAssembly.GetStaticProp(gameData, "Instance");
        if (instance == null)
            return -1;
        return GameAssembly.ToInt(GameAssembly.GetInstanceProp(instance, "PlayerCount"));
    }

    private static int MaxPlayers()
    {
        var client = GameAssembly.AmongUsClient();
        if (client == null) return 15;

        // Path 1: AmongUsClient.GameHostOpts.MaxPlayers (modern Among Us)
        var gameHostOpts = GameAssembly.GetInstanceMember(client, "GameHostOpts");
        if (gameHostOpts != null)
        {
            var max = GameAssembly.ToInt(GameAssembly.GetInstanceMember(gameHostOpts, "MaxPlayers"));
            if (max > 0) return max;
        }

        // Path 2: GameData.Instance.MaxPlayers (fallback for older versions)
        var gameDataType = GameAssembly.Type("GameData");
        var instance = gameDataType != null ? GameAssembly.GetStaticProp(gameDataType, "Instance") : null;
        if (instance != null)
        {
            var max = GameAssembly.ToInt(GameAssembly.GetInstanceMember(instance, "MaxPlayers"));
            if (max > 0) return max;
        }

        return 15; // default
    }

    /// <summary>
    /// Best-effort read of the IP/port of the server the client is connected to.
    /// Tries the ServerManager's currently selected region server, then falls back
    /// to AmongUsClient.NetworkAddress/NetworkPort (older clients). Returns ("", 0)
    /// when the values cannot be resolved via reflection and logs a warning.
    /// </summary>
    private static (string Ip, int Port) CurrentServerEndpoint()
    {
        try
        {
            var serverManagerType = GameAssembly.Type("ServerManager");
            var serverManager = serverManagerType != null ? GameAssembly.GetStaticMember(serverManagerType, "Instance") : null;
            object? server = null;

            if (serverManager != null)
            {
                // Path 1: ServerManager exposes the selected region server directly.
                server = GameAssembly.GetInstanceMember(serverManager, "CurrentServer");

                // Path 2: no CurrentServer member - take the first entry of
                // CurrentRegion.Servers (private-server regions usually have one).
                if (server == null)
                {
                    var region = GameAssembly.GetInstanceMember(serverManager, "CurrentRegion");
                    var servers = region != null ? GameAssembly.GetInstanceMember(region, "Servers") : null;
                    if (servers is System.Collections.IEnumerable list)
                    {
                        foreach (var candidate in list)
                        {
                            if (candidate == null) continue;
                            server = candidate;
                            break;
                        }
                    }
                }

                if (server != null)
                {
                    var ip = GameAssembly.ToStr(GameAssembly.GetInstanceMember(server, "Ip"));
                    var port = GameAssembly.ToInt(GameAssembly.GetInstanceMember(server, "Port"));
                    if (!string.IsNullOrEmpty(ip) && port > 0)
                        return (ip, port);
                }
            }

            // Path 3: AmongUsClient.NetworkAddress / NetworkPort (older clients).
            var client = GameAssembly.AmongUsClient();
            if (client != null)
            {
                var clientIp = GameAssembly.ToStr(GameAssembly.GetInstanceMember(client, "NetworkAddress"));
                var clientPort = GameAssembly.ToInt(GameAssembly.GetInstanceMember(client, "NetworkPort"));
                if (!string.IsNullOrEmpty(clientIp) && clientPort > 0)
                    return (clientIp, clientPort);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameStateTracker] CurrentServerEndpoint failed: {ex.Message}");
        }

        FileLogger.Warn("[GameStateTracker] Could not resolve the current server IP/port via reflection; leaving region endpoint empty.");
        return ("", 0);
    }

    private static List<int> GetAllPlayerLevels()
    {
        var levels = new List<int>();
        try
        {
            var gameDataType = GameAssembly.Type("GameData");
            var gameDataInstance = gameDataType != null ? GameAssembly.GetStaticProp(gameDataType, "Instance") : null;
            if (gameDataInstance == null) return levels;

            var allPlayers = GameAssembly.GetInstanceProp(gameDataInstance, "AllPlayers");
            if (allPlayers == null) return levels;

            var count = GameAssembly.ToInt(GameAssembly.GetInstanceProp(allPlayers, "Count"));
            if (count <= 0 || count > 15) return levels;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var playerInfo = GameAssembly.CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                    if (playerInfo == null) { levels.Add(0); continue; }
                    levels.Add(GameAssembly.GetPlayerLevel(playerInfo));
                }
                catch
                {
                    levels.Add(0);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[GameStateTracker] GetAllPlayerLevels failed: {ex.Message}");
        }
        return levels;
    }

    private static List<int> GetAllPlayerPings()
    {
        var pings = new List<int>();
        try
        {
            var gameDataType = GameAssembly.Type("GameData");
            var gameDataInstance = gameDataType != null ? GameAssembly.GetStaticProp(gameDataType, "Instance") : null;
            if (gameDataInstance == null) return pings;

            var allPlayers = GameAssembly.GetInstanceProp(gameDataInstance, "AllPlayers");
            if (allPlayers == null) return pings;

            var count = GameAssembly.ToInt(GameAssembly.GetInstanceProp(allPlayers, "Count"));
            if (count <= 0 || count > 15) return pings;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var playerInfo = GameAssembly.CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                    if (playerInfo == null) { pings.Add(0); continue; }
                    pings.Add(GameAssembly.GetPlayerPing(playerInfo));
                }
                catch
                {
                    pings.Add(0);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[GameStateTracker] GetAllPlayerPings failed: {ex.Message}");
        }
        return pings;
    }
}
