namespace AmongApi.Services;

public record LobbyInfo(string Code, string Region, string RegionIp, int RegionPort);
public record PlayerInfo(string PlayerName, int PlayerCount);

/// <summary>
/// Polls the game for lobby / player state via reflection (GameAssembly) and
/// raises transition events. Region info is not reliably readable from here;
/// the launcher owns region configuration, so Region fields are left empty.
/// </summary>
public class GameStateTracker
{
    private const int PollIntervalMs = 500;

    private readonly ManualLogSource _log;
    private readonly object _lock = new();
    private bool _wasInLobby;
    private int _lastPlayerCount = -1;

    public event EventHandler<LobbyInfo>? LobbyCreated;
    public event EventHandler<string>? LobbyClosed;
    public event EventHandler<PlayerInfo>? PlayerJoined;
    public event EventHandler<PlayerInfo>? PlayerLeft;

    public GameStateTracker(ManualLogSource log) => _log = log;

    public void Start()
    {
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[GameStateTracker] Tick failed: {ex.Message}");
            }
            await Task.Delay(PollIntervalMs);
        }
    }

    private void Tick()
    {
        bool inLobby;
        string code;
        int count;

        try
        {
            inLobby = IsInLobby();
            code = LobbyCode();
            count = PlayerCount();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[GameStateTracker] State read failed: {ex.Message}");
            return;
        }

        lock (_lock)
        {
            if (inLobby && !_wasInLobby)
            {
                _log.LogInfo($"[GameStateTracker] Lobby created (code {code}, players {count}).");
                _lastPlayerCount = count;
                LobbyCreated?.Invoke(this, new LobbyInfo(code, "", "", 0));
            }
            else if (!inLobby && _wasInLobby)
            {
                _log.LogInfo("[GameStateTracker] Lobby closed.");
                _lastPlayerCount = -1;
                LobbyClosed?.Invoke(this, "");
            }

            if (inLobby && _lastPlayerCount >= 0 && count != _lastPlayerCount)
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

            _wasInLobby = inLobby;
        }
    }

    private static bool IsInLobby()
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
        if (!GameAssembly.EnumEquals(state, GameAssembly.EnumValue(gameStateEnum, "Joined")))
            return false;

        return GameAssembly.ToBool(GameAssembly.GetInstanceProp(client, "InOnlineScene"));
    }

    private static string LobbyCode()
    {
        var amongUsClient = GameAssembly.Type("AmongUsClient");
        var client = GameAssembly.GetStaticProp(amongUsClient, "Instance");
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
