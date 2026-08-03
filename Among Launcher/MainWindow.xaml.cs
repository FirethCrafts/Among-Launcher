using System.IO;
using System.Net.Http;
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
    private readonly PipeServer _pipeServer = new();
    private readonly HttpClient _httpClient = new();
    private readonly List<Task> _pendingInstalls = new();
    private readonly object _pendingLock = new();
    private bool _restartRequested;
    private string? _moddedPath;

    public ModalOverlay ModalOverlayControl => ModalOverlay;

    public MainWindow()
    {
        InitializeComponent();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");

        _mainView.RequestShowWelcome += (_, _) => ShowView(_welcomeView, showSidebar: false);
        _mainView.GameStateChanged += OnGameStateChanged;
        _welcomeView.LoginCompleted += OnLoginCompleted;

        _pipeServer.ClientConnected += (_, _) => Dispatcher.Invoke(() =>
        {
            if (ContentArea.Content is MainView mv)
                mv.UpdateConnectionStatus(true);
        });

        _pipeServer.ClientDisconnected += (_, _) => Dispatcher.Invoke(() =>
        {
            if (ContentArea.Content is MainView mv)
                mv.UpdateConnectionStatus(false);
        });

        // Handler: AmongAPI requests mod install (downloads anytime, regardless of game state)
        _pipeServer.RegisterHandler("install_mod", async element =>
        {
            var payload = element.GetProperty("payload");
            var modId = payload.GetProperty("modId").GetString() ?? "unknown";
            var downloadUrl = payload.GetProperty("downloadUrl").GetString() ?? "";
            var fileName = payload.GetProperty("fileName").GetString() ?? "";

            LogDebug($"[Launcher] install_mod received: modId={modId}, url={downloadUrl}, file={fileName}");

            if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrEmpty(fileName))
                return new { type = "error", message = "Missing downloadUrl or fileName" };

            var moddedPath = Dispatcher.Invoke(() => GetModdedPath());
            if (string.IsNullOrEmpty(moddedPath))
                return new { type = "error", message = "Modded Among Us not installed" };

            var pluginsDir = Path.Combine(moddedPath, "BepInEx", "plugins");
            Directory.CreateDirectory(pluginsDir);

            var destPath = Path.Combine(pluginsDir, fileName);
            LogDebug($"[Launcher] Downloading {fileName} to {destPath}");

            var task = DownloadModAsync(modId, downloadUrl, destPath);

            lock (_pendingLock) { _pendingInstalls.Add(task); }

            // Report progress back to AmongAPI
            _ = task.ContinueWith(t =>
            {
                lock (_pendingLock) { _pendingInstalls.Remove(task); }

                if (t.IsCompletedSuccessfully)
                {
                    LogDebug($"[Launcher] Download complete: {fileName}");
                    Dispatcher.Invoke(() =>
                    {
                        if (ContentArea.Content is MainView mv)
                            mv.RefreshModsList();
                    });
                    _pipeServer.BroadcastMessageAsync("mod_installed",
                        new { modId, fileName, success = true });
                }
                else
                {
                    var error = t.Exception?.InnerException?.Message ?? "Download failed";
                    LogDebug($"[Launcher] Download failed: {fileName} - {error}");
                    _pipeServer.BroadcastMessageAsync("mod_installed",
                        new { modId, fileName, success = false, error });
                }

                CheckRestartAfterInstall();
            });

            return new { type = "install_mod_ack", modId, status = "downloading" };
        });

        // Handler: AmongAPI requests game restart after all installs complete
        _pipeServer.RegisterHandler("restart_after_install", _ =>
        {
            LogDebug("[Launcher] restart_after_install received");
            _restartRequested = true;

            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.StopGame();
            });

            LogDebug($"[Launcher] Pending installs: {_pendingInstalls.Count}, restart requested: {_restartRequested}");
            CheckRestartAfterInstall();

            return Task.FromResult<object?>(new { type = "restart_ack", status = "waiting_for_installs" });
        });

        // Handler: mod_status request
        _pipeServer.RegisterHandler("mod_status", async element =>
        {
            var mods = await Task.Run(() => Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    return mv.GetInstalledMods();
                return new List<ModInfo>();
            }));
            return new { type = "mod_status_response", mods = mods.Select(m => new { m.Name, m.FilePath }).ToArray() };
        });

        // Handler: game_ready
        _pipeServer.RegisterHandler("game_ready", _ =>
        {
            Dispatcher.Invoke(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.UpdateModStatusText("Game loaded — AmongAPI active");
            });
            return Task.FromResult<object?>(new { type = "game_ready_ack" });
        });

        _pipeServer.Start();

        var empty = IsLauncherDirEmpty();
        ShowView(empty ? _welcomeView : _mainView, showSidebar: !empty);

        Loaded += async (_, _) => await _pipeServer.BroadcastMessageAsync("launcher_ready");
    }

    private string? GetModdedPath()
    {
        if (!string.IsNullOrEmpty(_moddedPath) && Directory.Exists(_moddedPath))
            return _moddedPath;

        _moddedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmongLauncher", "ModdedAmongUs");

        return Directory.Exists(_moddedPath) ? _moddedPath : null;
    }

    private async Task DownloadModAsync(string modId, string url, string destPath)
    {
        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fileStream);
    }

    private async void CheckRestartAfterInstall()
    {
        bool pending;
        bool restart;
        lock (_pendingLock)
        {
            pending = _pendingInstalls.Count > 0;
            restart = _restartRequested;
        }

        if (!pending && restart)
        {
            _restartRequested = false;
            await Dispatcher.InvokeAsync(() =>
            {
                if (ContentArea.Content is MainView mv)
                    mv.LaunchGame();
            });
        }
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;

        var view = tag switch
        {
            "MainView" => _mainView,
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
            var active = view == _mainView ? HomeButton : SettingsButton;
            SetActiveNav(active);
        }
    }

    private void SetActiveNav(Button active)
    {
        var activeBg = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));
        foreach (var btn in new[] { HomeButton, SettingsButton, LogoutButton })
        {
            var isActive = btn == active;
            btn.Foreground = new SolidColorBrush(isActive ? Colors.White : (Color)FindResource("NavIconColor"));
            btn.Background = isActive ? activeBg : Brushes.Transparent;
        }
    }

    private void OnLoginCompleted(object? sender, DiscordUserProfile profile)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(profile.AvatarUrl, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            SidebarAvatar.Source = bitmap;
            SidebarAvatar.Visibility = Visibility.Visible;
        }
        catch
        {
            // Keep default logo if avatar fails to load
        }

        ShowView(_mainView, showSidebar: true);
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

    private static void LogDebug(string message)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "AmongLauncher_ipc.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
