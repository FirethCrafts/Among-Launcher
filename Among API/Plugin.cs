using System.Net.Http;
using System.Text.Json;

namespace AmongApi;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;
    private LobbyInfo? _lastLobby;
    private bool _autoPost;
    private string _serverUrl = "";
    private readonly HttpClient _http = new();

    public override void Load()
    {
        Log = base.Log;
        GameAssembly.Log = Log;
        FileLogger.Init();
        FileLogger.Info($"Plugin v{MyPluginInfo.PLUGIN_VERSION} loading...");
        Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Loading...");

        ParseLaunchArgs();

        _ = RunAsync();
    }

    private void ParseLaunchArgs()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--autopost", StringComparison.OrdinalIgnoreCase))
                {
                    _autoPost = true;
                    FileLogger.Info("Auto-post enabled via launch args.");
                }
                else if (args[i].Equals("--no-autopost", StringComparison.OrdinalIgnoreCase))
                {
                    _autoPost = false;
                    FileLogger.Info("Auto-post disabled via launch args.");
                }
                else if (args[i].StartsWith("--server-url=", StringComparison.OrdinalIgnoreCase))
                {
                    _serverUrl = args[i]["--server-url=".Length..].Trim();
                    FileLogger.Info($"Server URL from args: {_serverUrl}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Failed to parse launch args: {ex.Message}");
        }
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

            await pipe.SendMessageAsync("game_ready");
            FileLogger.Info("Game ready signal sent to launcher.");

            // Listen for server_url from launcher if not set via args
            pipe.RegisterHandler("set_server_url", element =>
            {
                var p = element.GetProperty("payload");
                if (p.TryGetProperty("url", out var urlProp))
                {
                    _serverUrl = urlProp.GetString() ?? "";
                    FileLogger.Info($"Server URL received from launcher: {_serverUrl}");
                }
                return Task.FromResult<object?>(null);
            });

            // Report lobby / player state transitions to the launcher
            var tracker = new GameStateTracker(Log);
            tracker.LobbyCreated += (_, info) =>
            {
                _lastLobby = info;
                FileLogger.Info($"Lobby created: {info.Code} (region {info.Region}, host {info.Host})");
                _ = pipe.SendMessageAsync("lobby_created",
                    new
                    {
                        code = info.Code,
                        region = info.Region,
                        regionIp = info.RegionIp,
                        regionPort = info.RegionPort,
                        host = info.Host,
                        playerCount = info.PlayerCount
                    });

                if (_autoPost && !string.IsNullOrEmpty(_serverUrl))
                {
                    _ = PostLobbyToBackend(info);
                }
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

            // Chat commands: /repost, /disband, /postlobby
            var commands = new ChatCommandHandler(Log);
            commands.OnRepost = () => _ = pipe.SendMessageAsync("lobby_created",
                new
                {
                    code = _lastLobby?.Code ?? "",
                    region = _lastLobby?.Region ?? "",
                    regionIp = _lastLobby?.RegionIp ?? "",
                    regionPort = _lastLobby?.RegionPort ?? 0,
                    host = _lastLobby?.Host ?? "",
                    playerCount = _lastLobby?.PlayerCount ?? 0
                });
            commands.OnDisband = () =>
            {
                _ = pipe.SendMessageAsync("lobby_closed", new { code = _lastLobby?.Code ?? "", reason = "disband" });
                LeaveLobby();
            };
            commands.OnPostLobby = () =>
            {
                if (_lastLobby != null && !string.IsNullOrEmpty(_serverUrl))
                {
                    FileLogger.Info("/postlobby: posting lobby to backend...");
                    var lobby = _lastLobby;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await PostLobbyToBackend(lobby);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Error($"/postlobby background task failed: {ex.Message}");
                        }
                    });
                }
                else
                {
                    FileLogger.Warn("/postlobby: no active lobby or server URL not set.");
                }
            };
            commands.Start();

            pipe.Disconnected += (_, _) => tracker.Stop();
            pipe.Disconnected += (_, _) => commands.Dispose();

            FileLogger.Info($"Auto-post: {_autoPost}, Server URL: {_serverUrl}");
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Error: {ex.Message}");
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Error: {ex.Message}");
        }
    }

    private async Task PostLobbyToBackend(LobbyInfo lobby)
    {
        try
        {
            var url = _serverUrl.TrimEnd('/') + "/api/v1/lobbies";
            var body = new
            {
                code = lobby.Code,
                region = lobby.Region,
                host = lobby.Host,
                mod_type = "modded",
                mods = new object[] { }
            };

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
                FileLogger.Info($"PostLobby: success ({(int)response.StatusCode})");
            else
                FileLogger.Warn($"PostLobby: failed ({(int)response.StatusCode} {response.ReasonPhrase})");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"PostLobby failed: {ex.Message}");
        }
    }

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
