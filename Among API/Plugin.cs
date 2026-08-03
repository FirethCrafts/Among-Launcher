namespace AmongApi;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;
    private LobbyInfo? _lastLobby;

    public override void Load()
    {
        Log = base.Log;
        GameAssembly.Log = Log;
        FileLogger.Init();
        FileLogger.Info($"Plugin v{MyPluginInfo.PLUGIN_VERSION} loading...");
        Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Loading...");

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            FileLogger.Info("Connecting to launcher...");
            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Connecting to launcher...");

            using var pipe = new PipeClient(Log);
            var connected = await pipe.ConnectAsync();

            if (!connected)
            {
                FileLogger.Warn("Launcher not running. Mods will load from BepInEx/plugins/.");
                Log.LogWarning($"[{MyPluginInfo.PLUGIN_NAME}] Launcher not running.");
                return;
            }

            FileLogger.Info("Connected to launcher.");
            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Connected to launcher.");

            // Tell launcher the game is ready
            await pipe.SendMessageAsync("game_ready");
            FileLogger.Info("Game ready signal sent to launcher.");

            // Report lobby / player state transitions to the launcher
            var tracker = new GameStateTracker(Log);
            tracker.LobbyCreated += (_, info) =>
            {
                _lastLobby = info;
                FileLogger.Info($"Lobby created: {info.Code}");
                _ = pipe.SendMessageAsync("lobby_created",
                    new { code = info.Code, region = info.Region, regionIp = info.RegionIp, regionPort = info.RegionPort });
            };
            tracker.LobbyClosed += (_, reason) =>
            {
                FileLogger.Info($"Lobby closed: {_lastLobby?.Code ?? ""}");
                _ = pipe.SendMessageAsync("lobby_closed", new { code = _lastLobby?.Code ?? "", reason });
                _lastLobby = null;
            };
            tracker.PlayerJoined += (_, p) =>
            {
                FileLogger.Info($"Player joined: count {p.PlayerCount}");
                _ = pipe.SendMessageAsync("player_joined", new { playerName = p.PlayerName, playerCount = p.PlayerCount });
            };
            tracker.PlayerLeft += (_, p) =>
            {
                FileLogger.Info($"Player left: count {p.PlayerCount}");
                _ = pipe.SendMessageAsync("player_left", new { playerName = p.PlayerName, playerCount = p.PlayerCount });
            };
            tracker.Start();

            // In-game direct lobby join (Task 15)
            var joiner = new LobbyJoiner(Log);
            pipe.Disconnected += (_, _) => joiner.Dispose();

            pipe.RegisterHandler("join_lobby", async element =>
            {
                JoinResult result;
                try
                {
                    var payload = element.TryGetProperty("payload", out var p) ? p : default;
                    var code = ReadString(payload, "code");
                    var region = ReadString(payload, "region");
                    var regionIp = ReadString(payload, "regionIp");
                    var regionPort = ReadInt(payload, "regionPort");
                    result = await joiner.JoinAsync(code, region, regionIp, regionPort);
                }
                catch (Exception ex)
                {
                    result = new JoinResult(false, ex.Message);
                }

                FileLogger.Info($"Join lobby result -> success={result.Success} error={result.Error ?? "none"}");
                _ = pipe.SendMessageAsync("join_lobby_result", new { success = result.Success, error = result.Error });
                return new { success = result.Success, error = result.Error };
            });

            // In-game host chat commands (Task 16): /repost and /disband
            var commands = new ChatCommandHandler(Log);
            commands.OnRepost = () => _ = pipe.SendMessageAsync("lobby_created",
                new { code = _lastLobby?.Code ?? "", region = _lastLobby?.Region ?? "", regionIp = _lastLobby?.RegionIp ?? "", regionPort = _lastLobby?.RegionPort ?? 0 });
            commands.OnDisband = () =>
            {
                _ = pipe.SendMessageAsync("lobby_closed", new { code = _lastLobby?.Code ?? "", reason = "disband" });
                LeaveLobby();
            };
            commands.Start();

            // Stop polling if the launcher connection drops
            pipe.Disconnected += (_, _) => tracker.Stop();
            pipe.Disconnected += (_, _) => commands.Dispose();

            // Keep connection alive - launcher may send commands later
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Error: {ex.Message}");
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Leaves the current lobby via the game API: AmongUsClient.Instance.ExitGame
    /// (DisconnectReasons.ExitGame) — the same path the in-game "leave" button uses.
    /// All reflection calls are wrapped in try/catch; failures are logged, never thrown.
    /// </summary>
    private static void LeaveLobby()
    {
        try
        {
            var client = GameAssembly.AmongUsClient();
            if (client == null)
            {
                FileLogger.Warn("LeaveLobby: AmongUsClient not available; nothing to leave.");
                return;
            }

            var disconnectReasons = GameAssembly.Type("DisconnectReasons");
            var exitGame = GameAssembly.EnumValue(disconnectReasons, "ExitGame");
            if (exitGame == null)
            {
                FileLogger.Warn("LeaveLobby: DisconnectReasons.ExitGame not found.");
                return;
            }

            if (!GameAssembly.HasInstanceMethod(client, "ExitGame", 1))
            {
                FileLogger.Warn("LeaveLobby: ExitGame(DisconnectReasons) not found.");
                return;
            }

            GameAssembly.CallInstanceMethod(client, "ExitGame", new object?[] { exitGame });
            FileLogger.Info("LeaveLobby: ExitGame(DisconnectReasons.ExitGame) invoked.");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"LeaveLobby failed: {ex.Message}");
        }
    }

    private static string ReadString(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var prop)
            ? prop.GetString() ?? ""
            : "";

    private static int ReadInt(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var prop))
            return 0;
        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return int.TryParse(prop.GetString(), out var value) ? value : 0;
    }
}
