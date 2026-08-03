using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmongLauncher.Ipc;
using AmongLauncher.Models;
using AmongLauncher.Views;

namespace AmongLauncher;

public partial class MainWindow
{
    private readonly MainView _mainView = new();
    private readonly SettingsView _settingsView = new();
    private readonly WelcomeView _welcomeView = new();
    private readonly LibraryView _libraryView = new();

    public MainView MainView => _mainView;
    private readonly PipeServer _pipeServer = new();
    private readonly HttpClient _httpClient = new();
    private string? _moddedPath;
    private readonly Services.Lobby.LobbyBackendClient _backend;
    private readonly Services.Lobby.LobbyWebSocketClient _ws;
    // Kept for its ctor side-effects: subscribes _ws.Kicked / _ws.Rejoin.
    private readonly Services.Lobby.LobbyCommandService _commands;
    private Services.Lobby.LobbyHeartbeatService? _heartbeat;
    private readonly Config.LauncherConfig _config;
    private string _userId = "";
    private LobbyInfo? _activeLobby;
    private Views.HostControlPanelView? _hostPanel;
    private readonly List<string> _lobbyPlayerNames = new();
    private TaskCompletionSource<bool>? _gameReadyTcs;
    private bool _joining;

    public ModalOverlay ModalOverlayControl => ModalOverlay;

    public MainWindow() : this(null) { }

    public MainWindow(string? deepLink)
    {
        InitializeComponent();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");
        _config = Config.LauncherConfig.Load();
        _backend = new Services.Lobby.LobbyBackendClient(_httpClient, _config);
        _ws = new Services.Lobby.LobbyWebSocketClient(_config);
        _commands = new Services.Lobby.LobbyCommandService(_ws,
            killGame: () =>
            {
                Dispatcher.Invoke(() =>
                {
                    _mainView.StopGame();
                    _mainView.UpdateModStatusText("Kicked from lobby");
                });
                return Task.CompletedTask;
            },
            rejoin: cmd => RejoinAsync(cmd));

        _mainView.GameStateChanged += OnGameStateChanged;
        _welcomeView.LoginCompleted += OnLoginCompleted;

        _pipeServer.ClientConnected += (_, _) =>
        {
            LogDebug("[Launcher] AmongAPI client connected!");
            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateConnectionStatus(true);
            });
        };

        _pipeServer.ClientDisconnected += (_, _) =>
        {
            LogDebug("[Launcher] AmongAPI client disconnected.");
            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateConnectionStatus(false);
            });
        };

        // Handler: game_ready - AmongAPI loaded and connected
        _pipeServer.RegisterHandler("game_ready", _ =>
        {
            _gameReadyTcs?.TrySetResult(true);
            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateModStatusText("Game loaded — AmongAPI active");
            });
            return Task.FromResult<object?>(new { type = "game_ready_ack", restart = false });
        });

        // Handler: AmongAPI created a lobby in-game - mirror it to the backend
        _pipeServer.RegisterHandler("lobby_created", async element =>
        {
            var p = element.GetProperty("payload");
            // After host gating only the host's mod emits lobby_created, so the local
            // signed-in user is the host. The tracker sends regionPort 0; fall back to
            // the default port when it is missing or non-positive.
            var regionPort = p.TryGetProperty("regionPort", out var rp) && rp.GetInt32() > 0
                ? rp.GetInt32()
                : 22023;
            var info = new LobbyInfo
            {
                Code = p.GetProperty("code").GetString() ?? "",
                Region = p.GetProperty("region").GetString() ?? "",
                RegionIp = p.GetProperty("regionIp").GetString() ?? "",
                RegionPort = regionPort,
                ModSet = GetInstalledModSet(),
                HostUserId = _userId
            };
            _activeLobby = info;
            _lobbyPlayerNames.Clear();
            await _backend.CreateLobbyAsync(new CreateLobbyRequest(info.Code, info.Region, info.RegionIp, info.RegionPort, info.ModSet, _userId), CancellationToken.None);
            StartHeartbeat(info.Code);
            _ = _ws.ConnectAsync(info.Code, CancellationToken.None);
            // Only the host's mod emits lobby_created, so the host check is normally authoritative.
            // Keep the empty-user fallback (string.IsNullOrEmpty) so testing without a signed-in
            // profile still surfaces the host panel; when a userId IS set, require an exact match
            // so a non-host user never sees host controls.
            if (string.IsNullOrEmpty(_userId) || _userId == info.HostUserId)
            {
                Dispatcher.Invoke(() => ShowHostPanel(info));
            }
            return new { type = "lobby_created_ack" };
        });

        // Handler: AmongAPI closed a lobby - disband it on the backend
        _pipeServer.RegisterHandler("lobby_closed", async element =>
        {
            var code = element.GetProperty("payload").GetProperty("code").GetString() ?? "";
            if (_activeLobby != null) await _backend.DisbandAsync(code, CancellationToken.None);
            StopHeartbeat();
            _ws.Disconnect();
            _activeLobby = null;
            _hostPanel = null;
            return new { type = "lobby_closed_ack" };
        });

        _pipeServer.RegisterHandler("player_joined", element => ForwardPlayerChange(element, joined: true));
        _pipeServer.RegisterHandler("player_left", element => ForwardPlayerChange(element, joined: false));

        // Handler: result of a join_lobby broadcast (surfaces errors to the UI)
        _pipeServer.RegisterHandler("join_lobby_result", async element =>
        {
            var p = element.GetProperty("payload");
            var ok = p.GetProperty("success").GetBoolean();
            if (!ok)
            {
                var error = p.TryGetProperty("error", out var e) ? e.GetString() : "unknown error";
                Dispatcher.Invoke(() =>
                {
                    if (ContentArea.Content is MainView mv)
                        mv.UpdateModStatusText($"Join failed: {error}");
                });
            }
            return null;
        });

        _pipeServer.Start();
        LogDebug("[Launcher] Pipe server started, listening for AmongAPI connections...");

        var empty = IsLauncherDirEmpty();
        ShowView(empty ? _welcomeView : _mainView, showSidebar: !empty);

        if (!empty)
            LoadSavedAvatar();

        // Register custom URI protocol and check for a deep-link payload
        Services.DeepLinkHandler.RegisterProtocol();
        App.DeepLinkReceived += link => Dispatcher.Invoke(() => HandleDeepLink(link));
        Loaded += async (_, _) =>
        {
            await _pipeServer.BroadcastMessageAsync("launcher_ready");
            HandleDeepLink(deepLink);
        };
    }

    public void HandleDeepLink(string? deepLink)
    {
        deepLink ??= Services.DeepLinkHandler.FindDeepLinkArgument();
        if (deepLink == null) return;

        var join = Services.DeepLinkHandler.TryParseJoin(deepLink);
        if (join != null)
        {
            LogDebug($"[Launcher] Join request received: code={join.Code}");
            _ = HandleJoinLinkAsync(join.Code);
            return;
        }

        var requests = Services.DeepLinkHandler.Parse(deepLink);
        if (requests.Count == 0) return;

        LogDebug($"[Launcher] Deep link detected with {requests.Count} mods.");

        var moddedPath = Dispatcher.Invoke(() => GetModdedPath());
        if (string.IsNullOrEmpty(moddedPath))
        {
            var missingModal = new ConfirmationModal();
            missingModal.Configure(
                "Modded Among Us is not installed yet.\n\nPlease install BepInEx first, then retry the link.",
                "OK");
            missingModal.Confirmed += (_, _) => ModalOverlay.Hide();
            ModalOverlay.Show("Downloading Required Mods", missingModal);
            return;
        }

        ShowDownloadModsModal(moddedPath, requests);
    }

    public async Task HandleJoinLinkAsync(string code)
    {
        if (_joining)
        {
            LogDebug("[Launcher] Join already in progress, ignoring duplicate request");
            return;
        }
        _joining = true;

        try
        {
            LogDebug($"[Launcher] Joining lobby {code}...");
            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateModStatusText($"Joining lobby {code}...");
            });

            if (!Services.Lobby.LobbyBackendClient.IsConfigured(_config))
            {
                LogDebug("[Launcher] Join aborted: backend not configured");
                Dispatcher.Invoke(() => ShowJoinError(
                    "No lobby server is configured.\n\nSet the server URL in Settings, then try the link again."));
                return;
            }

            var lobby = await _backend.GetLobbyAsync(code, CancellationToken.None);
            if (lobby == null)
            {
                LogDebug("[Launcher] Join failed: lobby not found");
                Dispatcher.Invoke(() => ShowJoinError(
                    $"Lobby '{code}' was not found on the server.\n\nIt may have closed, or the host hasn't created it yet."));
                return;
            }

            if (await JoinPipelineAsync(lobby))
            {
                LogDebug($"[Launcher] Join succeeded for lobby {code}");
                _ = _ws.ConnectAsync(code, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[Launcher] Join failed with exception: {ex}");
            Dispatcher.Invoke(() => ShowJoinError($"Could not reach the lobby server: {ex.Message}"));
        }
        finally
        {
            _joining = false;
        }
    }

    private void ShowJoinError(string message)
    {
        if (ContentArea.Content is MainView mv)
            mv.UpdateModStatusText(message);

        var modal = new ConfirmationModal();
        modal.Configure(message, "OK");
        modal.Confirmed += (_, _) => ModalOverlay.Hide();
        modal.Cancelled += (_, _) => ModalOverlay.Hide();
        ModalOverlay.Show("Join Lobby Failed", modal);
    }

    private async Task<bool> JoinPipelineAsync(LobbyInfo lobby)
    {
        var moddedPath = GetModdedPath();
        if (string.IsNullOrEmpty(moddedPath) ||
            !File.Exists(Path.Combine(moddedPath, "winhttp.dll")))
        {
            LogDebug("[Launcher] Join aborted: modded Among Us not installed");
            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateModStatusText("Modded Among Us is not installed. Run one-click setup first.");
            });
            return false;
        }

        var modSetSync = new Services.Lobby.ModSetSync(
            Path.Combine(moddedPath, "BepInEx", "plugins"),
            (_, url, dest) => DownloadModAsync(url, dest));

        var joinService = new Services.Lobby.LobbyJoinService(
            getLobby: (_, _) => Task.FromResult<LobbyInfo?>(lobby),
            ensureSetup: _ => Task.FromResult(
                GetModdedPath() != null &&
                File.Exists(Path.Combine(GetModdedPath()!, "winhttp.dll"))),
            killGame: () => StopGameAndWaitAsync(),
            launchGame: () =>
            {
                _gameReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Dispatcher.Invoke(() =>
                {
                    if (ContentArea.Content is MainView mv)
                        mv.LaunchGame();
                });
                return Task.CompletedTask;
            },
            waitForGameReady: () => WaitForGameReadyAsync(),
            sendJoinLobby: l => _pipeServer.BroadcastMessageAsync("join_lobby",
                new { code = l.Code, region = l.Region, regionIp = l.RegionIp, regionPort = l.RegionPort }),
            modSetSync);

        var outcome = await joinService.JoinLobbyAsync(lobby.Code, CancellationToken.None);
        if (!outcome.Started)
        {
            LogDebug($"[Launcher] Join failed: {outcome.Error}");
            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateModStatusText(outcome.Error ?? "Join failed");
            });
        }
        return outcome.Started;
    }

    private async Task RejoinAsync(Services.Lobby.RejoinCommand cmd)
    {
        try
        {
            LogDebug($"[Launcher] Rejoin command received for lobby {cmd.LobbyCode}");

            await StopGameAndWaitAsync();

            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateModStatusText($"Rejoining lobby {cmd.LobbyCode}...");
            });

            var lobby = new LobbyInfo
            {
                Code = cmd.LobbyCode,
                Region = cmd.Region,
                RegionIp = cmd.RegionIp,
                RegionPort = cmd.RegionPort,
                ModSet = cmd.ModSet
            };

            await JoinPipelineAsync(lobby);
        }
        catch (Exception ex)
        {
            LogDebug($"[Launcher] Rejoin failed with exception: {ex}");
            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateModStatusText($"Join failed: {ex.Message}");
            });
        }
    }

    private async Task<bool> WaitForGameReadyAsync()
    {
        if (_gameReadyTcs == null)
            return false;

        var readyTask = _gameReadyTcs.Task;
        var timeout = Task.Delay(90_000);
        // Crash guard: fire if the game process is observed running and then dies.
        // The game is launched via MainView's own manager, so we poll the process
        // table and only treat a running-then-exited transition as a crash. If the
        // game never starts, the 90s timeout still catches it.
        var exited = Task.Run(async () =>
        {
            var seenRunning = false;
            for (var i = 0; i < 180; i++)
            {
                if (IsAmongUsRunning()) seenRunning = true;
                else if (seenRunning) return true;
                await Task.Delay(500);
            }
            return false;
        });

        var done = await Task.WhenAny(readyTask, timeout, exited);
        return done == readyTask && await readyTask;
    }

    private Task<object?> ForwardPlayerChange(JsonElement element, bool joined)
    {
        var p = element.GetProperty("payload");
        var playerName = p.TryGetProperty("playerName", out var name) ? name.GetString() : null;
        var playerCount = p.TryGetProperty("playerCount", out var count) ? count.GetInt32() : -1;
        LogDebug($"[Launcher] Player event: playerName={playerName}, playerCount={playerCount} (local only until a backend player endpoint exists)");
        if (_activeLobby != null && playerCount >= 0)
            _activeLobby.PlayerCount = playerCount;

        if (!string.IsNullOrEmpty(playerName))
        {
            if (joined && !_lobbyPlayerNames.Contains(playerName))
                _lobbyPlayerNames.Add(playerName);
            else if (!joined)
                _lobbyPlayerNames.Remove(playerName);
        }

        if (_hostPanel != null)
            Dispatcher.Invoke(() => _hostPanel.UpdatePlayers(BuildPlayerList()));
        return Task.FromResult<object?>(null);
    }

    private List<LobbyPlayer> BuildPlayerList()
    {
        // Discord ID resolution is a backend concern (deferred); player names are
        // matched against the logged-in user (the host) via config UserName, falling
        // back to marking the single-player row as host.
        var players = _lobbyPlayerNames.Select(n => new LobbyPlayer("", n, false)).ToList();

        var hostName = _config.UserName;
        var hostIndex = players.FindIndex(p =>
            !string.IsNullOrEmpty(hostName) &&
            string.Equals(p.PlayerName, hostName, StringComparison.OrdinalIgnoreCase));
        if (hostIndex < 0 && players.Count == 1)
            hostIndex = 0;
        if (hostIndex >= 0)
            players[hostIndex] = players[hostIndex] with { IsHost = true };

        return players;
    }

    private void ShowHostPanel(LobbyInfo info)
    {
        var panel = new Views.HostControlPanelView(info);
        panel.RePostRequested += async (_, _) =>
        {
            try
            {
                await _backend.RepostAsync(info.Code, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogDebug($"[Launcher] Repost failed: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    if (ContentArea.Content is MainView mv)
                        mv.UpdateModStatusText($"Repost failed: {ex.Message}");
                });
            }
        };
        panel.DisbandRequested += (_, _) => _ = ConfirmDisbandAsync(info.Code);
        panel.KickRequested += async (_, targetUserId) =>
        {
            // Rows without a resolved Discord ID cannot be kicked; no-op with feedback
            // rather than calling the backend with an empty target.
            if (string.IsNullOrEmpty(targetUserId))
            {
                Dispatcher.Invoke(() =>
                {
                    if (ContentArea.Content is MainView mv)
                        mv.UpdateModStatusText("Cannot kick: player has no resolved Discord ID");
                });
                return;
            }
            await _backend.KickAsync(info.Code, targetUserId, CancellationToken.None);
        };
        panel.UpdatePlayers(BuildPlayerList());
        _hostPanel = panel;
        ShowView(panel, showSidebar: true);
    }

    private async Task ConfirmDisbandAsync(string code)
    {
        var confirmModal = new ConfirmationModal();
        confirmModal.Configure(
            $"Disband lobby {code}?\n\nAll players will be removed from the lobby.",
            "Disband",
            isDanger: true);

        confirmModal.Confirmed += async (_, _) =>
        {
            ModalOverlay.Hide();
            try
            {
                await _backend.DisbandAsync(code, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogDebug($"[Launcher] Disband failed: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    if (ContentArea.Content is MainView mv)
                        mv.UpdateModStatusText($"Disband failed: {ex.Message}");
                });
                return;
            }
            _ws.Disconnect();
            StopHeartbeat();
            _activeLobby = null;
            _hostPanel = null;
            Dispatcher.Invoke(() => _mainView.StopGame());
        };
        confirmModal.Cancelled += (_, _) => ModalOverlay.Hide();

        ModalOverlay.Show("Disband Lobby", confirmModal);
    }

    private void StartHeartbeat(string code)
    {
        _heartbeat ??= new Services.Lobby.LobbyHeartbeatService(_backend.HeartbeatAsync);
        _heartbeat.Start(code, _userId);
    }

    private void StopHeartbeat() => _heartbeat?.Stop();

    private void ShowDownloadModsModal(string moddedPath, List<Services.ModDownloadRequest> requests)
    {
        var downloadModal = new DownloadModsModal(moddedPath, requests);

        downloadModal.AllComplete += (_, success) => Dispatcher.Invoke(() =>
            HandleDownloadResult(success, moddedPath, requests));

        ModalOverlay.Show("Downloading Required Mods", downloadModal);
        _ = downloadModal.StartAsync();
    }

    private void HandleDownloadResult(bool success, string moddedPath, List<Services.ModDownloadRequest> requests)
    {
        if (success)
        {
            ModalOverlay.Hide();
            _mainView.LaunchGame();
            return;
        }

        // Pause auto-launch and offer Retry / Launch Anyway
        var retryModal = new ConfirmationModal();
        retryModal.Configure(
            "One or more mods failed to download.\n\nYou can retry the download or launch the game anyway.",
            "Launch Anyway",
            isDanger: false);

        retryModal.Confirmed += (_, _) =>
        {
            ModalOverlay.Hide();
            _mainView.LaunchGame();
        };

        retryModal.Cancelled += (_, _) =>
        {
            ModalOverlay.Hide();
            ShowDownloadModsModal(moddedPath, requests);
        };

        ModalOverlay.Show("Download Failed", retryModal);
    }

    private string? GetModdedPath()
    {
        if (!string.IsNullOrEmpty(_moddedPath) && Directory.Exists(_moddedPath))
            return _moddedPath;

        _moddedPath = Config.LauncherConfig.DefaultModdedPath();

        return Directory.Exists(_moddedPath) ? _moddedPath : null;
    }

    private List<ModSetEntry> GetInstalledModSet()
    {
        var moddedPath = GetModdedPath();
        if (string.IsNullOrEmpty(moddedPath)) return new List<ModSetEntry>();

        var pluginsDir = Path.Combine(moddedPath, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsDir)) return new List<ModSetEntry>();

        return Directory.GetFiles(pluginsDir, "*.dll")
            .Select(f => new ModSetEntry { FileName = Path.GetFileName(f) })
            .ToList();
    }

    private async Task DownloadModAsync(string url, string destPath) =>
        await Services.ModDownloader.DownloadToFileAsync(_httpClient, url, destPath, LogDebug);

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;

        var view = tag switch
        {
            "MainView" => _mainView,
            "LibraryView" => _libraryView,
            "SettingsView" => _settingsView,
            _ => ContentArea.Content
        };

        ShowView((UIElement)view, showSidebar: true);
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        var confirmModal = new ConfirmationModal();
        confirmModal.Configure(
            "Are you sure you want to log out of your account?",
            "Log Out",
            isDanger: true);

        confirmModal.Confirmed += (_, _) =>
        {
            ModalOverlay.Hide();
            // Clear avatar
            SidebarAvatar.Source = null;
            SidebarAvatar.Visibility = Visibility.Collapsed;
            ShowView(_welcomeView, showSidebar: false);
        };

        confirmModal.Cancelled += (_, _) => ModalOverlay.Hide();

        ModalOverlay.Show("Log Out", confirmModal);
    }

    private void ShowView(UIElement view, bool showSidebar)
    {
        Sidebar.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(ContentArea, showSidebar ? 1 : 0);
        Grid.SetColumnSpan(ContentArea, showSidebar ? 1 : 2);
        ContentArea.Content = view;

        if (showSidebar)
        {
            var active = view == _mainView ? HomeButton
                       : view == _libraryView ? LibraryButton
                       : SettingsButton;
            SetActiveNav(active);
        }
    }

    private void SetActiveNav(Button active)
    {
        var activeBg = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));
        foreach (var btn in new[] { HomeButton, LibraryButton, SettingsButton, LogoutButton })
        {
            var isActive = btn == active;
            btn.Foreground = new SolidColorBrush(isActive ? Colors.White : (Color)FindResource("NavIconColor"));
            btn.Background = isActive ? activeBg : Brushes.Transparent;
        }
    }

    private void OnLoginCompleted(object? sender, DiscordUserProfile profile)
    {
        _userId = profile.Id;

        // Save avatar to config for reload on next launch
        _config.AvatarUrl = profile.AvatarUrl;
        _config.UserName = profile.GlobalName ?? profile.Username;
        _config.Save();

        LoadAvatar(profile.AvatarUrl);
        ShowView(_mainView, showSidebar: true);
    }

    private void LoadAvatar(string avatarUrl)
    {
        if (string.IsNullOrEmpty(avatarUrl)) return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(avatarUrl, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            SidebarAvatar.Source = bitmap;
            SidebarAvatar.Visibility = Visibility.Visible;
        }
        catch
        {
            // Keep default logo if avatar fails to load
        }
    }

    private void LoadSavedAvatar()
    {
        if (!string.IsNullOrEmpty(_config.AvatarUrl))
        {
            LoadAvatar(_config.AvatarUrl);
        }
    }

    private void OnGameStateChanged(object? sender, bool isRunning)
    {
        Dispatcher.Invoke(() => UpdateStatusBadge(isRunning));
    }

    private void UpdateStatusBadge(bool isRunning)
    {
        if (isRunning)
        {
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            StatusText.Text = "Among Us — Running";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            StatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            StopGameButton.Visibility = Visibility.Visible;
        }
        else
        {
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x76));
            StatusText.Text = "No Game Running";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x76));
            StatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30));
            StopGameButton.Visibility = Visibility.Collapsed;
        }
    }

    private void StopGameButton_Click(object sender, RoutedEventArgs e)
    {
        // Find the MainView and call its stop method
        if (ContentArea.Content is MainView mainView)
        {
            mainView.StopGame();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static bool IsLauncherDirEmpty()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmongLauncher");

        if (!Directory.Exists(dir)) return true;

        return Directory.GetFiles(dir).Length == 0 &&
               Directory.GetDirectories(dir).Length == 0;
    }

    private static bool IsAmongUsRunning() =>
        System.Diagnostics.Process.GetProcessesByName("Among Us").Length > 0;

    private async Task StopGameAndWaitAsync()
    {
        Dispatcher.Invoke(() =>
        {
            if (ContentArea.Content is MainView mv)
                mv.StopGame();
        });
        var waited = 0;
        while (IsAmongUsRunning() && waited < 30)
        {
            await Task.Delay(500);
            waited++;
        }
    }

    private static void LogDebug(string message) => Services.LauncherLog.Write(message);
}
