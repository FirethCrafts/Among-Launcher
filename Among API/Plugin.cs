using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AmongApi;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    /// <summary>
    /// Launcher↔mod IPC contract version. Bump this whenever a message shape or
    /// semantic changes in a way that requires both sides to be updated together.
    /// The launcher rejects a mod whose protocol version it does not expect.
    ///
    /// v3: pings now use <c>-1</c> as the "unknown" sentinel for non-local
    /// players (previously a clamped <c>0</c>), and per-player <c>color</c>
    /// fields were added to <c>player_joined</c>/<c>players_list</c> plus
    /// <c>playerColors</c> to <c>lobby_created</c>.
    /// </summary>
    public const int ProtocolVersion = 3;

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

        // Install the real Unity main-thread pump. Load() runs on the Unity main
        // thread via the BepInEx chainloader, but Unity's SynchronizationContext is
        // not available yet (SynchronizationContext.Current is null), so the pump's
        // MonoBehaviour.Update() is the only reliable way back onto the main thread.
        MainThreadDispatcher.Initialize();

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
            FileLogger.Info("Waiting for game to be ready...");
            if (!await WaitForGameReadyAsync())
            {
                FileLogger.Warn("Game did not reach ready state; proceeding anyway.");
            }

            // Main-thread identity is established by the pump's first Update()
            // (MainThreadDispatcher.Initialize() in Load). Do NOT re-capture a
            // SynchronizationContext here: this continuation runs on a thread-pool
            // thread, so capturing it would mislabel a pool thread as the main
            // thread and reintroduce off-thread game calls.

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

            var joiner = new LobbyJoiner(Log);
            pipe.Disconnected += (_, _) => joiner.Dispose();

            pipe.RegisterHandler("join_lobby", element =>
            {
                var payload = element.TryGetProperty("payload", out var p) ? p : default;
                var code = ReadString(payload, "code").Trim().ToUpperInvariant();
                var region = ReadString(payload, "region");
                var regionIp = ReadString(payload, "regionIp");
                var regionPort = ReadInt(payload, "regionPort");

                FileLogger.Info($"join_lobby received: code={code}, region={region}, regionIp={regionIp}, regionPort={regionPort}");

                if (code.Length == 0)
                {
                    var empty = new JoinResult(false, "Empty lobby code");
                    _ = pipe.SendMessageAsync("join_lobby_result", new { success = empty.Success, error = empty.Error, code });
                    return Task.FromResult<object?>(new { success = empty.Success, error = empty.Error, code });
                }

                // Enqueue on the serial pump and return immediately. The join runs
                // asynchronously and reports out-of-band via `join_lobby_result`.
                // NOT awaiting here keeps the IPC read loop free, so `cancel_join`
                // and `leave_lobby` can be received while a join is in progress.
                _ = joiner.JoinAsync(code, region, regionIp, regionPort, result =>
                {
                    FileLogger.Info($"Join lobby result -> code={code} success={result.Success} error={result.Error ?? "none"}");
                    _ = pipe.SendMessageAsync("join_lobby_result", new { success = result.Success, error = result.Error, code });
                });

                return Task.FromResult<object?>(new { queued = true, code });
            });

            pipe.RegisterHandler("cancel_join", async element =>
            {
                var payload = element.TryGetProperty("payload", out var p) ? p : default;
                var code = ReadString(payload, "code");
                FileLogger.Info($"cancel_join received: code='{code}'");
                var cancelled = await joiner.CancelPending(code);
                return new { cancelled };
            });

            pipe.RegisterHandler("leave_lobby", async _ =>
            {
                try
                {
                    await MainThreadDispatcher.EnqueueAsync(() => LeaveLobby());
                    FileLogger.Info("leave_lobby: ExitGame dispatched");
                    return new { success = true };
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"leave_lobby failed: {ex.Message}");
                    return new { success = false, error = ex.Message };
                }
            });

            await pipe.SendMessageAsync("game_ready", new { protocol = ProtocolVersion });
            FileLogger.Info("Game ready signal sent to launcher.");

            var tracker = new GameStateTracker(Log);
            tracker.LobbyCreated += (_, info) =>
            {
                _lastLobby = info;
                FileLogger.Info($"Lobby created: {info.Code} (region {info.Region}, host {info.Host}, players: [{string.Join(", ", info.PlayerNames ?? new())}], levels: [{string.Join(", ", info.PlayerLevels ?? new())}], pings: [{string.Join(", ", info.PlayerPings ?? new())}])");
                var activeMods = GetInstalledMods();
                var modType = activeMods.Count > 0 ? "modded" : "vanilla";
                var roster = BuildRoster(info);
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
                        playerNames = roster.Names,
                        playerLevels = roster.Levels,
                        playerPings = roster.Pings,
                        playerColors = roster.Colors,
                        mod_type = modType,
                        status = "lobby",
                        mods = activeMods,
                        game_version = info.GameVersion,
                        map_name = info.MapName,
                        language = info.Language,
                        chat_type = info.ChatType,
                        isHost = info.IsHost
                    });

                if (_autoPost && info.IsHost && !string.IsNullOrEmpty(_serverUrl))
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
            tracker.LobbyClosed += (_, closed) =>
            {
                FileLogger.Info($"Lobby closed: {_lastLobby?.Code ?? ""} (wasHost={closed.IsHost})");
                _ = pipe.SendMessageAsync("lobby_closed", new
                {
                    code = _lastLobby?.Code ?? "",
                    reason = closed.Reason,
                    isHost = closed.IsHost
                });
                _lastLobby = null;
            };
            tracker.PlayerJoined += (_, p) =>
            {
                FileLogger.Info($"Player joined: {p.PlayerName} (count {p.PlayerCount})");
                _ = pipe.SendMessageAsync("player_joined", new
                {
                    // `playerName`/`playerCount` keep the original payload shape
                    // the WPF launcher reads; `name` is the same value under the
                    // key the tauri launcher's PlayerEntry expects. `is_host` is
                    // resolved against the known lobby host so the launcher does
                    // not have to infer it from the name alone.
                    playerName = p.PlayerName,
                    name = p.PlayerName,
                    playerCount = p.PlayerCount,
                    playerLevel = ClampedSnapshotValue(p.PlayerLevels, p.PlayerNames, p.PlayerName),
                    level = ClampedSnapshotValue(p.PlayerLevels, p.PlayerNames, p.PlayerName),
                    // Pings are NOT clamped: -1 is the "unknown" sentinel the
                    // launcher maps to None (only the local player has a real ping).
                    playerPing = SnapshotValue(p.PlayerPings, p.PlayerNames, p.PlayerName),
                    ping = SnapshotValue(p.PlayerPings, p.PlayerNames, p.PlayerName),
                    color = SnapshotColor(p.PlayerColors, p.PlayerNames, p.PlayerName),
                    is_host = ResolveIsHost(p.PlayerName)
                });
                SendPlayersList(pipe, p);
            };
            tracker.PlayerLeft += (_, p) =>
            {
                FileLogger.Info($"Player left: {p.PlayerName} (count {p.PlayerCount})");
                _ = pipe.SendMessageAsync("player_left", new
                {
                    playerName = p.PlayerName,
                    name = p.PlayerName,
                    playerCount = p.PlayerCount
                });
                SendPlayersList(pipe, p);
            };
            tracker.Start();

            // ChatCommandHandler disabled: polling HudManager.Instance via reflection
            // triggers IL2CPP native NullReferenceExceptions that bypass C# try-catch,
            // causing game crashes. Use launcher UI buttons (Repost/Disband) instead.
            /*
            var commands = new ChatCommandHandler(Log);
            commands.OnRepost = () =>
            {
                var activeMods = GetInstalledMods();
                var modType = activeMods.Count > 0 ? "modded" : "vanilla";
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
                        playerNames = _lastLobby?.PlayerNames ?? new List<string>(),
                        playerLevels = _lastLobby?.PlayerLevels ?? new List<int>(),
                        playerPings = _lastLobby?.PlayerPings ?? new List<int>(),
                        mod_type = modType,
                        status = "lobby",
                        mods = activeMods,
                        game_version = _lastLobby?.GameVersion ?? "",
                        map_name = _lastLobby?.MapName ?? "",
                        language = _lastLobby?.Language ?? "",
                        chat_type = _lastLobby?.ChatType ?? ""
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
            */

            pipe.Disconnected += (_, _) => tracker.Stop();
            // commands disposed above (ChatCommandHandler disabled)

            FileLogger.Info($"Auto-post: {_autoPost}, Server URL: {_serverUrl}");
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Error: {ex.Message}");
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Emits a full `players_list` snapshot (the tauri launcher parses this as
    /// a list of PlayerEntry). Entry `is_host` is resolved by matching the name
    /// against the known lobby host so guests still mark the real host.
    /// </summary>
    private void SendPlayersList(PipeClient pipe, PlayerInfo p)
    {
        var names = p.PlayerNames;
        if (names == null || names.Count == 0)
            return;

        var entries = new List<object>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i];
            // names is raw-index aligned with AllPlayers and may hold ""
            // placeholders for empty slots; skip them, but keep `i` indexing the
            // side arrays so the emitted rows stay correctly aligned.
            if (string.IsNullOrEmpty(name))
                continue;
            var color = i < (p.PlayerColors?.Count ?? 0) ? p.PlayerColors![i] : null;
            if (string.IsNullOrEmpty(color)) color = null;
            entries.Add(new
            {
                name,
                level = i < (p.PlayerLevels?.Count ?? 0) ? Math.Max(0, p.PlayerLevels![i]) : (int?)null,
                // Pings are NOT clamped: -1 is the "unknown" sentinel (only the
                // local player's row carries a real ping).
                ping = i < (p.PlayerPings?.Count ?? 0) ? p.PlayerPings![i] : (int?)null,
                color,
                is_host = ResolveIsHost(name)
            });
        }

        _ = pipe.SendMessageAsync("players_list", entries);
    }

    /// <summary>
    /// Builds the `lobby_created` roster payload from the raw, AllPlayers-index-
    /// aligned snapshot arrays, dropping the mod's <c>""</c> placeholder rows so
    /// the launcher never seeds an empty-named player. All four arrays are
    /// filtered together so they stay index-aligned for the launcher's zip.
    /// </summary>
    private static (List<string> Names, List<int> Levels, List<int> Pings, List<string> Colors) BuildRoster(LobbyInfo info)
    {
        var names = info.PlayerNames ?? new List<string>();
        var levels = info.PlayerLevels ?? new List<int>();
        var pings = info.PlayerPings ?? new List<int>();
        var colors = info.PlayerColors ?? new List<string>();

        var outNames = new List<string>(names.Count);
        var outLevels = new List<int>(names.Count);
        var outPings = new List<int>(names.Count);
        var outColors = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            if (string.IsNullOrEmpty(names[i]))
                continue;
            outNames.Add(names[i]);
            outLevels.Add(i < levels.Count ? levels[i] : 0);
            outPings.Add(i < pings.Count ? pings[i] : -1);
            outColors.Add(i < colors.Count ? colors[i] : "");
        }
        return (outNames, outLevels, outPings, outColors);
    }

    /// <summary>
    /// Reads the value aligned to <paramref name="name"/> from a snapshot list
    /// (names and values are index-aligned by GameData.AllPlayers order).
    /// </summary>
    private static int? SnapshotValue(List<int>? values, List<string>? names, string name)
    {
        if (values == null || names == null)
            return null;
        var index = names.IndexOf(name);
        if (index < 0 || index >= values.Count)
            return null;
        return values[index];
    }

    /// <summary>
    /// Same as <see cref="SnapshotValue"/> but clamps negative values to 0.
    /// Used for levels only: the launcher parses `level` as `Option<u32>`, and a
    /// negative value would fail that parse and drop the whole message. Pings are
    /// deliberately NOT clamped (see <see cref="SnapshotValue"/>) so the -1
    /// "unknown" sentinel survives.
    /// </summary>
    private static int? ClampedSnapshotValue(List<int>? values, List<string>? names, string name)
    {
        var value = SnapshotValue(values, names, name);
        return value.HasValue ? Math.Max(0, value.Value) : (int?)null;
    }

    /// <summary>
    /// Reads the colour aligned to <paramref name="name"/> from a snapshot list
    /// (index-aligned with the names by GameData.AllPlayers order). Returns null
    /// when the colour is unknown/empty.
    /// </summary>
    private static string? SnapshotColor(List<string>? colors, List<string>? names, string name)
    {
        if (colors == null || names == null)
            return null;
        var index = names.IndexOf(name);
        if (index < 0 || index >= colors.Count)
            return null;
        var color = colors[index];
        return string.IsNullOrEmpty(color) ? null : color;
    }

    /// <summary>
    /// Resolves whether <paramref name="name"/> is the lobby host by matching
    /// it against the host name captured at lobby creation. Returns false when
    /// the host name could not be resolved (empty or "UNKNOWN").
    /// </summary>
    private bool ResolveIsHost(string name)
    {
        var hostName = _lastLobby?.Host;
        return !string.IsNullOrEmpty(hostName)
               && !hostName.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
               && string.Equals(name, hostName, StringComparison.OrdinalIgnoreCase);
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
            region_ip = lobby.RegionIp,
            region_port = lobby.RegionPort,
            host = hostName,
            mod_type = modType,
            status = "lobby",
            max_players = lobby.MaxPlayers,
            mods = activeMods,
            game_version = lobby.GameVersion,
            map_name = lobby.MapName,
            language = lobby.Language,
            chat_type = lobby.ChatType
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
        if (!GameAssembly.InLobby())
        {
            FileLogger.Info("[Plugin] Not in a lobby; skipping ExitGame.");
            return;
        }

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

    /// <summary>
    /// Polls the game for a stable "NotJoined" state, which indicates the game
    /// is fully loaded, connected to the Among Us servers, and sitting at the
    /// main menu. Requires 3 consecutive stable ticks (1.5s) to confirm,
    /// with an 8s minimum floor to avoid the previous "join during login" crash.
    /// Returns false after 30s if the game never reaches ready state.
    /// </summary>
    private static async Task<bool> WaitForGameReadyAsync()
    {
        const int PollIntervalMs = 500;
        const int MinReadyTicks = 3;          // 3 × 500ms = 1.5s of stable NotJoined
        const int MinWaitMs = 8_000;          // minimum time from plugin load
        const int TimeoutMs = 30_000;

        // Resolve the type + nested enum ONCE before the loop (not per-tick), on
        // the main-thread pump so no IL2CPP access happens off-thread.
        var gameStateEnum = await MainThreadDispatcher.EnqueueAsync(
            () => GameAssembly.Type("InnerNet.InnerNetClient")?.GetNestedType("GameStates"));
        var notJoined = gameStateEnum != null
            ? await MainThreadDispatcher.EnqueueAsync(() => GameAssembly.EnumValue(gameStateEnum, "NotJoined"))
            : null;

        var startTime = DateTime.UtcNow;
        int stableTicks = 0;

        while ((DateTime.UtcNow - startTime).TotalMilliseconds < TimeoutMs)
        {
            await Task.Delay(PollIntervalMs);

            double elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            if (elapsedMs < MinWaitMs)
                continue;  // still within the minimum wait floor

            // All IL2CPP reads run on the pump thread. null means "skip this
            // tick" (client or enum unavailable), preserving the original
            // `continue` behavior that leaves stableTicks untouched; true/false
            // is the NotJoined-ness of the current state.
            bool? isNotJoined = await MainThreadDispatcher.EnqueueAsync<bool?>(() =>
            {
                if (notJoined == null) return null;

                var client = GameAssembly.AmongUsClient();
                if (client == null) return null;

                var state = GameAssembly.GetInstanceProp(client, "GameState");
                return GameAssembly.EnumEquals(state, notJoined);
            });

            if (isNotJoined == null)
                continue;

            if (isNotJoined.Value)
            {
                stableTicks++;
                if (stableTicks >= MinReadyTicks)
                {
                    FileLogger.Info($"Game ready after {(int)elapsedMs}ms ({stableTicks} stable ticks).");
                    return true;
                }
            }
            else
            {
                stableTicks = 0;  // reset on any non-NotJoined state
            }
        }

        FileLogger.Warn("Game readiness poll timed out.");
        return false;
    }
}
