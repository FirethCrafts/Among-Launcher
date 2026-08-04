namespace AmongApi.Services;

public record LobbyInfo(string Code, string Region, string RegionIp, int RegionPort, string Host, int PlayerCount);
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
        string region;
        string host;

        try
        {
            inLobby = IsInLobby();
            code = LobbyCode();
            count = PlayerCount();
            isHost = IsHost();
            region = GameAssembly.CurrentRegionName();
            host = GameAssembly.LocalPlayerName();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[GameStateTracker] State read failed: {ex.Message}");
            return;
        }

        lock (_lock)
        {
            // Only meaningful while in a lobby; kept for the leave transition, when the
            // connection may already be torn down and the host check would be unreliable.
            if (inLobby)
                _lastWasHost = isHost;

            if (inLobby && !_wasInLobby)
            {
                if (_lastWasHost)
                {
                    _log.LogInfo($"[GameStateTracker] Lobby created (code {code}, region {region}, host {host}, players {count}).");
                    _lastPlayerCount = count >= 0 ? count : -1;
                    LobbyCreated?.Invoke(this, new LobbyInfo(code, region, "", 0, host, count));
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
        var client = GameAssembly.AmongUsClient();
        if (client == null)
            return false;

        var hostId = GameAssembly.ToInt(GameAssembly.GetInstanceMember(client, "HostId"));
        var innerNetClient = GameAssembly.Type("InnerNet.InnerNetClient");
        var currentClient = GameAssembly.ToInt(GameAssembly.GetStaticMember(innerNetClient, "CurrentClient"));
        return currentClient >= 0 && hostId == currentClient;
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
}
