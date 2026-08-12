namespace AmongApi.Services;

public record LobbyInfo(string Code, string Region, string RegionIp, int RegionPort, string Host, int PlayerCount, int MaxPlayers = 15, List<string>? PlayerNames = null);
public record PlayerInfo(string PlayerName, int PlayerCount);

/// <summary>
/// Polls the game for lobby / player state via reflection (GameAssembly) and
/// raises transition events. Region and host names are read from the game's
/// ServerManager / PlayerControl (falling back to "UNKNOWN" when unavailable).
/// </summary>
public class GameStateTracker : IDisposable
{
    private const int PollIntervalMs = 500;

    private readonly ManualLogSource _log;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private bool _wasInLobby;
    private bool _lastWasHost;
    private int _lastPlayerCount = -1;

    public event EventHandler<LobbyInfo>? LobbyCreated;
    public event EventHandler<string>? LobbyClosed;
    public event EventHandler<PlayerInfo>? PlayerJoined;
    public event EventHandler<PlayerInfo>? PlayerLeft;

    public GameStateTracker(ManualLogSource log) => _log = log;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(LoopAsync);
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
                _log.LogWarning($"[GameStateTracker] Tick failed: {ex.Message}");
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
            _log.LogWarning($"[GameStateTracker] State read failed: {ex.Message}");
            return;
        }

        lock (_lock)
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
                    try { region = GameAssembly.CurrentRegionName(); } catch { }
                    try { host = GameAssembly.LocalPlayerName(); } catch { }
                    try { maxPlayers = MaxPlayers(); } catch { }
                    try { playerNames = GameAssembly.GetAllPlayerNames(); } catch { }
                    _log.LogInfo($"[GameStateTracker] Lobby created (code {code}, region {region}, host {host}, players {count}, maxPlayers {maxPlayers}, playerNames [{string.Join(", ", playerNames ?? new())}]).");
                    _lastPlayerCount = count >= 0 ? count : -1;
                    LobbyCreated?.Invoke(this, new LobbyInfo(code, region, "", 0, host, count, maxPlayers, playerNames));
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
                    LobbyClosed?.Invoke(this, "");
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
                        PlayerJoined?.Invoke(this, new PlayerInfo("<unknown>", count));
                    }
                    else
                    {
                        _log.LogInfo($"[GameStateTracker] Player left (count {count}).");
                        PlayerLeft?.Invoke(this, new PlayerInfo("<unknown>", count));
                    }
                    _lastPlayerCount = count;
                }
            }

            _wasInLobby = inLobby;
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
        if (client == null)
        {
            FileLogger.Warn("[GameStateTracker] MaxPlayers: AmongUsClient is null");
            return 15;
        }

        // Try GameHostOpts (host-only property in some versions)
        var gameOptions = GameAssembly.GetInstanceProp(client, "GameHostOpts");
        if (gameOptions != null)
        {
            var max = GameAssembly.ToInt(GameAssembly.GetInstanceProp(gameOptions, "MaxPlayers"));
            FileLogger.Info($"[GameStateTracker] MaxPlayers: GameHostOpts.MaxPlayers={max}");
            if (max > 0) return max;
        }

        // Try NormalOptions (common property name)
        var normalOptions = GameAssembly.GetInstanceProp(client, "NormalOptions");
        if (normalOptions != null)
        {
            var max = GameAssembly.ToInt(GameAssembly.GetInstanceProp(normalOptions, "MaxPlayers"));
            FileLogger.Info($"[GameStateTracker] MaxPlayers: NormalOptions.MaxPlayers={max}");
            if (max > 0) return max;
        }

        // Try GameOptions on PlayerControl (alternative path)
        var playerControlType = GameAssembly.Type("PlayerControl");
        var localPlayer = GameAssembly.GetStaticMember(playerControlType, "LocalPlayer");
        if (localPlayer != null)
        {
            var gameOptions2 = GameAssembly.GetInstanceProp(localPlayer, "GameOptions");
            if (gameOptions2 != null)
            {
                var max = GameAssembly.ToInt(GameAssembly.GetInstanceProp(gameOptions2, "MaxPlayers"));
                FileLogger.Info($"[GameStateTracker] MaxPlayers: PlayerControl.GameOptions.MaxPlayers={max}");
                if (max > 0) return max;
            }
        }

        // Try GameData.Instance.MaxPlayers (fallback)
        var gameDataType = GameAssembly.Type("GameData");
        var gameDataInstance = GameAssembly.GetStaticProp(gameDataType, "Instance");
        if (gameDataInstance != null)
        {
            var max = GameAssembly.ToInt(GameAssembly.GetInstanceProp(gameDataInstance, "MaxPlayers"));
            FileLogger.Info($"[GameStateTracker] MaxPlayers: GameData.Instance.MaxPlayers={max}");
            if (max > 0) return max;
        }

        FileLogger.Warn("[GameStateTracker] MaxPlayers: all attempts failed, returning default 15");
        return 15;
    }
}
