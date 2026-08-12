using System.Net.Http;
using System.Security.Cryptography;
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

        // Capture context immediately on main thread during plugin load
        MainThreadDispatcher.CaptureContext();

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

            // Wait for game to fully load (splash + connecting + logging in)
            FileLogger.Info("Waiting 30 seconds for game to fully load...");
            Thread.Sleep(30000);
            FileLogger.Info("Done waiting.");

            await pipe.SendMessageAsync("game_ready");
            FileLogger.Info("Game ready signal sent to launcher.");

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

            var tracker = new GameStateTracker(Log);
            tracker.LobbyCreated += (_, info) =>
            {
                _lastLobby = info;
                FileLogger.Info($"Lobby created: {info.Code} (region {info.Region}, host {info.Host}, players: [{string.Join(", ", info.PlayerNames ?? new())}])");
                _ = pipe.SendMessageAsync("lobby_created",
                    new
                    {
                        code = info.Code,
                        region = info.Region,
                        regionIp = info.RegionIp,
                        regionPort = info.RegionPort,
                        host = info.Host,
                        playerCount = info.PlayerCount,
                        maxPlayers = info.MaxPlayers,
                        playerNames = info.PlayerNames ?? new List<string>()
                    });

                if (_autoPost && !string.IsNullOrEmpty(_serverUrl))
                {
                    FileLogger.Info("Auto-post: dispatching lobby POST to background thread...");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            await PostLobbyToBackend(info, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Error($"Auto-post background task failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    });
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

                    FileLogger.Info($"join_lobby received: code={code}, region={region}, regionIp={regionIp}, regionPort={regionPort}");

                    result = await joiner.JoinAsync(code, region, regionIp, regionPort);
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"join_lobby handler exception: {ex.GetType().Name}: {ex.Message}");
                    result = new JoinResult(false, ex.Message);
                }

                FileLogger.Info($"Join lobby result -> success={result.Success} error={result.Error ?? "none"}");
                _ = pipe.SendMessageAsync("join_lobby_result", new { success = result.Success, error = result.Error });
                return new { success = result.Success, error = result.Error };
            });

            var commands = new ChatCommandHandler(Log);
            commands.OnRepost = () =>
            {
                _ = pipe.SendMessageAsync("lobby_created",
                    new
                    {
                        code = _lastLobby?.Code ?? "",
                        region = _lastLobby?.Region ?? "",
                        regionIp = _lastLobby?.RegionIp ?? "",
                        regionPort = _lastLobby?.RegionPort ?? 0,
                        host = _lastLobby?.Host ?? "",
                        playerCount = _lastLobby?.PlayerCount ?? 0,
                        maxPlayers = _lastLobby?.MaxPlayers ?? 15,
                        playerNames = _lastLobby?.PlayerNames ?? new List<string>()
                    });

                if (_lastLobby != null && !string.IsNullOrEmpty(_serverUrl))
                {
                    FileLogger.Info("/repost: dispatching POST to backend...");
                    var lobby = _lastLobby;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            await PostLobbyToBackend(lobby, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Error($"/repost background task failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    });
                }
                else
                {
                    FileLogger.Warn("/repost: no active lobby or server URL not set.");
                }
            };
            commands.OnDisband = () =>
            {
                _ = pipe.SendMessageAsync("lobby_closed", new { code = _lastLobby?.Code ?? "", reason = "disband" });
                LeaveLobby();
            };
            commands.OnPostLobby = () =>
            {
                if (_lastLobby != null && !string.IsNullOrEmpty(_serverUrl))
                {
                    FileLogger.Info("/postlobby: dispatching POST to background thread...");
                    var lobby = _lastLobby;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            await PostLobbyToBackend(lobby, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Error($"/postlobby background task failed: {ex.GetType().Name}: {ex.Message}");
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

    private async Task PostLobbyToBackend(LobbyInfo lobby, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_serverUrl))
        {
            FileLogger.Warn("PostLobby: server URL is empty");
            return;
        }

        var baseUrl = _serverUrl.TrimEnd('/');
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            FileLogger.Warn($"PostLobby: invalid server URL format: {_serverUrl}");
            return;
        }

        var url = baseUrl + "/api/v1/lobbies";
        FileLogger.Info($"PostLobby: POST to {url}");

        var hostName = !string.IsNullOrWhiteSpace(lobby.Host) && !lobby.Host.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? lobby.Host
            : "Host";

        var activeMods = GetInstalledMods();
        var modType = activeMods.Count > 0 ? "modded" : "vanilla";

        var body = new
        {
            code = lobby.Code,
            region = lobby.Region,
            host = hostName,
            mod_type = modType,
            mods = activeMods,
            max_players = lobby.MaxPlayers
        };

        var json = JsonSerializer.Serialize(body);
        FileLogger.Info($"PostLobby: payload: {json}");

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct);
        FileLogger.Info($"PostLobby: response {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private static List<object> GetInstalledMods()
    {
        var mods = new List<object>();
        try
        {
            var pluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir))
                pluginsDir = Path.Combine(Directory.GetCurrentDirectory(), "BepInEx", "plugins");

            if (Directory.Exists(pluginsDir))
            {
                var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "AmongApi.dll", "0Harmony.dll", "AsmResolver.dll",
                    "BepInEx.Core.dll", "BepInEx.Preloader.Core.dll",
                    "BepInEx.Unity.Common.dll", "BepInEx.Unity.IL2CPP.dll"
                };

                foreach (var file in Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    if (excluded.Contains(fileName)) continue;

                    var hash = ComputeSha256(file);
                    var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(file).FileVersion ?? "";

                    mods.Add(new
                    {
                        name = fileName,
                        version = version,
                        file_hash = hash
                    });
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"GetInstalledMods failed: {ex.Message}");
        }
        return mods;
    }

    private static string ComputeSha256(string filePath)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var bytes = sha.ComputeHash(stream);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    private static void LeaveLobby()
    {
        try
        {
            var client = GameAssembly.AmongUsClient();
            if (client == null)
            {
                FileLogger.Warn("LeaveLobby: AmongUsClient not available");
                return;
            }

            var disconnectReasons = GameAssembly.Type("DisconnectReasons");
            var exitGame = GameAssembly.EnumValue(disconnectReasons, "ExitGame");
            if (exitGame == null)
            {
                FileLogger.Warn("LeaveLobby: DisconnectReasons.ExitGame not found");
                return;
            }

            GameAssembly.CallInstanceMethod(client, "ExitGame", new object?[] { exitGame });
            FileLogger.Info("LeaveLobby: ExitGame invoked");
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
