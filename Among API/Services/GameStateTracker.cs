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
                _log.LogWarning($"[GameStateTracker] Tick failed: {ex}");
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

                            _log.LogInfo($"[GameStateTracker] Lobby created (code {code}, region {region}, host {host}, players {count}, maxPlayers {maxPlayers}).");
                            _lastPlayerCount = count >= 0 ? count : -1;
                            try { LobbyCreated?.Invoke(this, new LobbyInfo(code, region, "", 0, host, count, maxPlayers, playerNames, playerLevels, playerPings, gameVersion, mapName, language, chatType)); } catch { }
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

        // Path 1: AmongUsClient.GameHostOpts.MaxPlayers
        var gameHostOpts = GameAssembly.GetInstanceMember(client, "GameHostOpts");
        FileLogger.Info($"[GameStateTracker] MaxPlayers: GameHostOpts={gameHostOpts?.GetType().Name ?? "null"}");
        if (gameHostOpts != null)
        {
            var max = ReadMaxPlayersFromOptions(gameHostOpts, "GameHostOpts");
            if (max > 0) return max;
        }

        // Path 2: AmongUsClient.GameOptions (field in some versions)
        var clientGameOpts = GameAssembly.GetInstanceMember(client, "GameOptions");
        FileLogger.Info($"[GameStateTracker] MaxPlayers: client.GameOptions={clientGameOpts?.GetType().Name ?? "null"}");
        if (clientGameOpts != null)
        {
            var max = ReadMaxPlayersFromOptions(clientGameOpts, "client.GameOptions");
            if (max > 0) return max;
        }

        // Path 3: AmongUsClient.NormalOptions (property in some versions)
        var normalOptions = GameAssembly.GetInstanceMember(client, "NormalOptions");
        FileLogger.Info($"[GameStateTracker] MaxPlayers: NormalOptions={normalOptions?.GetType().Name ?? "null"}");
        if (normalOptions != null)
        {
            var max = ReadMaxPlayersFromOptions(normalOptions, "NormalOptions");
            if (max > 0) return max;
        }

        // Path 4: PlayerControl.LocalPlayer.GameOptions.MaxPlayers
        var playerControlType = GameAssembly.Type("PlayerControl");
        var localPlayer = playerControlType != null ? GameAssembly.GetStaticMember(playerControlType, "LocalPlayer") : null;
        FileLogger.Info($"[GameStateTracker] MaxPlayers: LocalPlayer={localPlayer?.GetType().Name ?? "null"}");
        if (localPlayer != null)
        {
            var localGameOpts = GameAssembly.GetInstanceMember(localPlayer, "GameOptions");
            FileLogger.Info($"[GameStateTracker] MaxPlayers: LocalPlayer.GameOptions={localGameOpts?.GetType().Name ?? "null"}");
            if (localGameOpts != null)
            {
                var max = ReadMaxPlayersFromOptions(localGameOpts, "LocalPlayer.GameOptions");
                if (max > 0) return max;
            }

            // Try LocalPlayer.Data -> PlayerInfo -> GameOptions path
            var data = GameAssembly.GetInstanceProp(localPlayer, "Data");
            if (data != null)
            {
                var dataGameOpts = GameAssembly.GetInstanceMember(data, "GameOptions");
                FileLogger.Info($"[GameStateTracker] MaxPlayers: LocalPlayer.Data.GameOptions={dataGameOpts?.GetType().Name ?? "null"}");
                if (dataGameOpts != null)
                {
                    var max = ReadMaxPlayersFromOptions(dataGameOpts, "LocalPlayer.Data.GameOptions");
                    if (max > 0) return max;
                }
            }
        }

        // Path 5: GameData.Instance.MaxPlayers / MaxPlayerCount
        var gameDataType = GameAssembly.Type("GameData");
        var gameDataInstance = gameDataType != null ? GameAssembly.GetStaticProp(gameDataType, "Instance") : null;
        FileLogger.Info($"[GameStateTracker] MaxPlayers: GameData.Instance={gameDataInstance?.GetType().Name ?? "null"}");
        if (gameDataInstance != null)
        {
            var max = GameAssembly.ToInt(GameAssembly.GetInstanceMember(gameDataInstance, "MaxPlayers"));
            FileLogger.Info($"[GameStateTracker] MaxPlayers: GameData.Instance.MaxPlayers={max}");
            if (max > 0) return max;

            var max2 = GameAssembly.ToInt(GameAssembly.GetInstanceMember(gameDataInstance, "MaxPlayerCount"));
            FileLogger.Info($"[GameStateTracker] MaxPlayers: GameData.Instance.MaxPlayerCount={max2}");
            if (max2 > 0) return max2;
        }

        // Path 6: ShipStatus / GameData direct fields on client
        var clientMaxPlayers = GameAssembly.ToInt(GameAssembly.GetInstanceMember(client, "MaxPlayers"));
        FileLogger.Info($"[GameStateTracker] MaxPlayers: client.MaxPlayers={clientMaxPlayers}");
        if (clientMaxPlayers > 0) return clientMaxPlayers;

        FileLogger.Warn("[GameStateTracker] MaxPlayers: all attempts failed, returning default 15");
        return 15;
    }

    private static int ReadMaxPlayersFromOptions(object optionsObj, string source)
    {
        // Try standard property/field names
        foreach (var name in new[] { "MaxPlayers", "maxPlayers", "MaxPlayerCount", "PlayerCap" })
        {
            var val = GameAssembly.ToInt(GameAssembly.GetInstanceMember(optionsObj, name));
            FileLogger.Info($"[GameStateTracker] MaxPlayers: {source}.{name}={val}");
            if (val > 0) return val;
        }

        // Try GameOptions-specific field: numPlayers
        var numPlayers = GameAssembly.ToInt(GameAssembly.GetInstanceMember(optionsObj, "numPlayers"));
        FileLogger.Info($"[GameStateTracker] MaxPlayers: {source}.numPlayers={numPlayers}");
        if (numPlayers > 0) return numPlayers;

        return 0;
    }

    private static List<int> GetAllPlayerLevels()
    {
        var levels = new List<int>();
        FileLogger.Info("[GameStateTracker] GetAllPlayerLevels: Starting");
        try
        {
            var gameDataType = GameAssembly.Type("GameData");
            FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: GameData type={gameDataType?.FullName ?? "null"}");

            var gameDataInstance = gameDataType != null ? GameAssembly.GetStaticProp(gameDataType, "Instance") : null;
            FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: GameData.Instance={gameDataInstance?.GetType().FullName ?? "null"}");
            if (gameDataInstance == null) { FileLogger.Warn("[GameStateTracker] GetAllPlayerLevels: GameData.Instance is null, returning"); return levels; }

            var allPlayers = GameAssembly.GetInstanceProp(gameDataInstance, "AllPlayers");
            FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: AllPlayers={allPlayers?.GetType().FullName ?? "null"}");
            if (allPlayers == null) { FileLogger.Warn("[GameStateTracker] GetAllPlayerLevels: AllPlayers is null, returning"); return levels; }

            var count = GameAssembly.ToInt(GameAssembly.GetInstanceProp(allPlayers, "Count"));
            FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: AllPlayers.Count={count}");
            if (count <= 0 || count > 15) { FileLogger.Warn($"[GameStateTracker] GetAllPlayerLevels: count {count} out of range, returning"); return levels; }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: Getting player {i} via get_Item...");
                    var playerInfo = GameAssembly.CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                    FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: Player {i}={playerInfo?.GetType().FullName ?? "null"}");
                    if (playerInfo == null) { FileLogger.Warn($"[GameStateTracker] GetAllPlayerLevels: player {i} is null, skipping"); continue; }

                    FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: Getting level for player {i}...");
                    var level = GameAssembly.GetPlayerLevel(playerInfo);
                    FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: Player {i} level={level}");
                    levels.Add(level);
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"[GameStateTracker] GetAllPlayerLevels: player[{i}] FAILED: {ex}");
                    levels.Add(0);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[GameStateTracker] GetAllPlayerLevels FAILED: {ex}");
        }
        FileLogger.Info($"[GameStateTracker] GetAllPlayerLevels: Done, returning {levels.Count} levels");
        return levels;
    }

    private static List<int> GetAllPlayerPings()
    {
        var pings = new List<int>();
        FileLogger.Info("[GameStateTracker] GetAllPlayerPings: Starting");
        try
        {
            var gameDataType = GameAssembly.Type("GameData");
            FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: GameData type={gameDataType?.FullName ?? "null"}");

            var gameDataInstance = gameDataType != null ? GameAssembly.GetStaticProp(gameDataType, "Instance") : null;
            FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: GameData.Instance={gameDataInstance?.GetType().FullName ?? "null"}");
            if (gameDataInstance == null) { FileLogger.Warn("[GameStateTracker] GetAllPlayerPings: GameData.Instance is null, returning"); return pings; }

            var allPlayers = GameAssembly.GetInstanceProp(gameDataInstance, "AllPlayers");
            FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: AllPlayers={allPlayers?.GetType().FullName ?? "null"}");
            if (allPlayers == null) { FileLogger.Warn("[GameStateTracker] GetAllPlayerPings: AllPlayers is null, returning"); return pings; }

            var count = GameAssembly.ToInt(GameAssembly.GetInstanceProp(allPlayers, "Count"));
            FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: AllPlayers.Count={count}");
            if (count <= 0 || count > 15) { FileLogger.Warn($"[GameStateTracker] GetAllPlayerPings: count {count} out of range, returning"); return pings; }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: Getting player {i} via get_Item...");
                    var playerInfo = GameAssembly.CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                    FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: Player {i}={playerInfo?.GetType().FullName ?? "null"}");
                    if (playerInfo == null) { FileLogger.Warn($"[GameStateTracker] GetAllPlayerPings: player {i} is null, skipping"); continue; }

                    FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: Getting ping for player {i}...");
                    var ping = GameAssembly.GetPlayerPing(playerInfo);
                    FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: Player {i} ping={ping}");
                    pings.Add(ping);
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"[GameStateTracker] GetAllPlayerPings: player[{i}] FAILED: {ex}");
                    pings.Add(0);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[GameStateTracker] GetAllPlayerPings FAILED: {ex}");
        }
        FileLogger.Info($"[GameStateTracker] GetAllPlayerPings: Done, returning {pings.Count} pings");
        return pings;
    }
}
