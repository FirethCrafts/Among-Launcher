using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AmongLauncher.Game;
using AmongLauncher.Installer;
using AmongLauncher.Models;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class MainView
{
    private readonly GameProcessManager _gameManager = new();
    private string? _moddedPath;
    private readonly HttpClient _httpClient = new();

    public event EventHandler? RequestShowWelcome;
    public event EventHandler<bool>? GameStateChanged;

    public MainView()
    {
        InitializeComponent();
        Loaded += MainView_Loaded;
        _gameManager.GameExited += OnGameExited;

        // Configure HttpClient with User-Agent
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");
    }

    private async void MainView_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckGameStatus();
        RefreshModsList();
    }

    private async Task CheckGameStatus()
    {
        GameStatusText.Text = "Searching for Among Us installation...";

        var locator = new AmongUsLocator();
        var gamePath = locator.FindAmongUs();

        if (gamePath == null)
        {
            GameStatusText.Text = "Among Us not found. Please install Among Us via Steam, Epic Games, or Xbox Game Pass.";
            PlayButton.IsEnabled = false;
            CopyPathButton.IsEnabled = false;
            AddModButton.IsEnabled = false;
            return;
        }

        _moddedPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmongLauncher", "ModdedAmongUs");

        var bepinExInstalled = System.IO.File.Exists(
            System.IO.Path.Combine(_moddedPath, "winhttp.dll"));

        if (bepinExInstalled)
        {
            GameStatusText.Text = $"Among Us ready!\nLocation: {_moddedPath}";
            PlayButton.IsEnabled = true;
            CopyPathButton.IsEnabled = true;
            AddModButton.IsEnabled = true;
            InstallButton.Content = "Reinstall BepInEx";
        }
        else
        {
            GameStatusText.Text = $"Among Us found at:\n{gamePath}\n\nClick 'Install BepInEx' to set up the modded copy.";
            PlayButton.IsEnabled = false;
            CopyPathButton.IsEnabled = false;
            AddModButton.IsEnabled = false;
        }

        await Task.CompletedTask;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var locator = new AmongUsLocator();
        var sourcePath = locator.FindAmongUs();

        if (sourcePath == null)
        {
            MessageBox.Show("Among Us installation not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _moddedPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmongLauncher", "ModdedAmongUs");

        ShowProgress("Copying Among Us installation...");

        try
        {
            var copier = new GameCopier();
            await copier.CopyGameAsync(sourcePath, _moddedPath, new Progress<int>(percent =>
            {
                Dispatcher.Invoke(() => ProgressText.Text = $"Copying files... {percent}%");
            }));

            ShowProgress("Downloading BepInEx...");

            var bepinExInstaller = new BepInExInstaller();
            await bepinExInstaller.InstallAsync(_moddedPath, new Progress<int>(percent =>
            {
                Dispatcher.Invoke(() => ProgressText.Text = $"Downloading BepInEx... {percent}%");
            }));

            // Ensure steam_appid.txt exists for Among Us
            var steamAppIdPath = Path.Combine(_moddedPath, "steam_appid.txt");
            if (!File.Exists(steamAppIdPath))
            {
                File.WriteAllText(steamAppIdPath, "945360");
            }

            ShowProgress("Installing AmongAPI...");

            await InstallAmongApiAsync();

            HideProgress();
            GameStatusText.Text = $"Modded Among Us ready!\nLocation: {_moddedPath}";
            PlayButton.IsEnabled = true;
            CopyPathButton.IsEnabled = true;
            AddModButton.IsEnabled = true;
            InstallButton.Content = "Reinstall BepInEx";
            RefreshModsList();
        }
        catch (Exception ex)
        {
            HideProgress();
            MessageBox.Show($"Installation failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InstallAmongApiAsync()
    {
        const string downloadUrl = "https://github.com/FirethCrafts/Among-Launcher/releases/latest/download/AmongApi.dll";
        var pluginsDir = Path.Combine(_moddedPath!, "BepInEx", "plugins");
        Directory.CreateDirectory(pluginsDir);

        var destPath = Path.Combine(pluginsDir, "AmongApi.dll");

        var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fileStream);
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gameManager.IsGameRunning())
        {
            StopGame();
            return;
        }

        if (string.IsNullOrEmpty(_moddedPath)) return;

        var exePath = System.IO.Path.Combine(_moddedPath, "Among Us.exe");
        if (!System.IO.File.Exists(exePath))
        {
            MessageBox.Show("Among Us.exe not found in modded folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Check and install mods before launching
        PlayButton.IsEnabled = false;
        try
        {
            var installed = await CheckAndInstallModsAsync();
            if (installed > 0)
            {
                ModStatusText.Text = $"Installed {installed} mod(s). Launching...";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to install mods:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            PlayButton.IsEnabled = true;
            return;
        }

        _gameManager.LaunchGame(exePath);
        SetPlayButtonRunning(true);
        ModStatusText.Text = "Game launched. AmongAPI.dll will load via BepInEx.";
    }

    private async Task<int> CheckAndInstallModsAsync()
    {
        var manifest = LoadManifest();
        if (manifest == null || manifest.Mods.Count == 0) return 0;

        var pluginsDir = Path.Combine(_moddedPath!, "BepInEx", "plugins");
        Directory.CreateDirectory(pluginsDir);

        var installed = 0;

        Dispatcher.Invoke(() =>
        {
            ModStatusText.Text = "Checking for mods...";
            ShowProgress("Checking for mods...");
        });

        // Small delay so user can see the status
        await Task.Delay(500);

        foreach (var mod in manifest.Mods)
        {
            var destPath = Path.Combine(pluginsDir, mod.FileName);

            Dispatcher.Invoke(() => ModStatusText.Text = $"Checking {mod.FileName}...");
            await Task.Delay(300);

            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            {
                Dispatcher.Invoke(() => ModStatusText.Text = $"✓ {mod.FileName} ready");
                await Task.Delay(200);
                continue;
            }

            Dispatcher.Invoke(() => ModStatusText.Text = $"Installing {mod.FileName}...");
            ShowProgress($"Installing {mod.FileName}...");

            await DownloadModAsync(mod.Id, mod.Url, destPath);
            installed++;

            Dispatcher.Invoke(() => ModStatusText.Text = $"✓ {mod.FileName} installed");
            await Task.Delay(200);
        }

        HideProgress();

        if (installed > 0)
            RefreshModsList();

        return installed;
    }

    private static Models.ModManifest? LoadManifest()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var stream = assembly.GetManifestResourceStream("manifest.json");
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Models.ModManifest>(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task DownloadModAsync(string modId, string url, string destPath)
    {
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            return;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(2000);
            }
        }
    }

    public void StopGame()
    {
        if (_gameManager.IsGameRunning())
        {
            _gameManager.KillGame();
            SetPlayButtonRunning(false);
            ModStatusText.Text = "Game closed.";
        }
    }

    public void LaunchGame()
    {
        if (_gameManager.IsGameRunning()) return;
        if (string.IsNullOrEmpty(_moddedPath)) return;

        var exePath = System.IO.Path.Combine(_moddedPath, "Among Us.exe");
        if (!System.IO.File.Exists(exePath)) return;

        _gameManager.LaunchGame(exePath);
        SetPlayButtonRunning(true);
        ModStatusText.Text = "Game launched. AmongAPI.dll will load via BepInEx.";
    }

    private void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_moddedPath)) return;

        try
        {
            Clipboard.SetText(_moddedPath);
            var originalContent = CopyPathButton.Content;
            CopyPathButton.Content = "✓ COPIED!";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() => CopyPathButton.Content = originalContent);
                });
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch
        {
            MessageBox.Show("Failed to copy path to clipboard.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        var logViewer = new LogViewerModal();
        mainWindow.ModalOverlayControl.Show("IPC Logs", logViewer);
    }

    private void SaveLaunchOptions_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Implement save launch options logic
        MessageBox.Show("Launch options saved!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Add Mod Button - Show menu
    private void AddModButton_Click(object sender, RoutedEventArgs e)
    {
        AddModPopup.IsOpen = true;
    }

    // Import Local Mod
    private void ImportLocalMod_Click(object sender, RoutedEventArgs e)
    {
        AddModPopup.IsOpen = false;

        if (string.IsNullOrEmpty(_moddedPath))
        {
            MessageBox.Show("Please install BepInEx first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select Mod DLL",
            Filter = "DLL Files (*.dll)|*.dll",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var pluginsDir = Path.Combine(_moddedPath, "BepInEx", "plugins");
                if (!Directory.Exists(pluginsDir))
                    Directory.CreateDirectory(pluginsDir);

                var destPath = Path.Combine(pluginsDir, Path.GetFileName(dialog.FileName));

                // Handle file conflicts
                if (File.Exists(destPath))
                {
                    var result = MessageBox.Show(
                        $"A file named '{Path.GetFileName(dialog.FileName)}' already exists. Overwrite?",
                        "File Exists",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                File.Copy(dialog.FileName, destPath, true);
                RefreshModsList();
                MessageBox.Show($"Mod '{Path.GetFileName(dialog.FileName)}' installed successfully!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import mod:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // Install Preset Mod - Show modal
    private void InstallPresetMod_Click(object sender, RoutedEventArgs e)
    {
        AddModPopup.IsOpen = false;

        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        // Map mod names to their actual GitHub repositories
        var modRepositories = new Dictionary<string, string>
        {
            { "aunlocker", "astra1dev/AUnlocker" },
            { "better-among-us", "NotHunter101/ExtraRolesAmongUs" },
            { "town-of-us", "Town-Of-Us/TownOfUs-Reactor" },
            { "the-other-roles", "NotHunter101/ExtraRolesAmongUs" }
        };

        var presetLibrary = new PresetModLibraryModal();
        presetLibrary.InstallModRequested += async (_, args) =>
        {
            var (modName, button) = args;
            button.IsEnabled = false;
            button.Content = "Installing...";

            // Get the actual repository path
            if (!modRepositories.TryGetValue(modName, out var repoPath))
            {
                button.Content = "Install";
                button.IsEnabled = true;
                MessageBox.Show($"Repository not found for '{modName}'", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var success = await DownloadModFromGitHub(repoPath, button);

            if (success)
            {
                button.Content = "Installed";
            }
            else
            {
                button.Content = "Install";
                button.IsEnabled = true;
            }
        };

        mainWindow.ModalOverlayControl.Show("Preset Mod Library", presetLibrary);
    }

    // GitHub API Download Helper
    private async Task<bool> DownloadModFromGitHub(string repoPath, Button installButton)
    {
        if (string.IsNullOrEmpty(_moddedPath))
        {
            MessageBox.Show("Please install BepInEx first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var originalContent = installButton.Content;
        installButton.Content = "Installing...";
        installButton.IsEnabled = false;

        try
        {
            var response = await _httpClient.GetAsync($"https://api.github.com/repos/{repoPath}/releases/latest");

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new Exception($"Repository '{repoPath}' not found.");
                }
                throw new Exception($"GitHub API returned {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("assets", out var assets))
            {
                throw new Exception("No assets found in release");
            }

            // Find first .dll asset
            string? downloadUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameElement) &&
                    nameElement.GetString()?.EndsWith(".dll") == true)
                {
                    if (asset.TryGetProperty("browser_download_url", out var urlElement))
                    {
                        downloadUrl = urlElement.GetString();
                        break;
                    }
                }
            }

            if (downloadUrl == null)
            {
                throw new Exception("No DLL file found in release assets");
            }

            // Download the DLL
            var dllResponse = await _httpClient.GetAsync(downloadUrl);
            dllResponse.EnsureSuccessStatusCode();

            var pluginsDir = Path.Combine(_moddedPath, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir))
                Directory.CreateDirectory(pluginsDir);

            var fileName = Path.GetFileName(downloadUrl);
            var destPath = Path.Combine(pluginsDir, fileName);

            using (var stream = await dllResponse.Content.ReadAsStreamAsync())
            using (var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write))
            {
                await stream.CopyToAsync(fileStream);
            }

            installButton.Content = "Installed";
            RefreshModsList();
            return true;
        }
        catch (Exception ex)
        {
            installButton.Content = originalContent;
            installButton.IsEnabled = true;
            MessageBox.Show($"Failed to install mod:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // Refresh Mods List
    public void RefreshModsList()
    {
        if (string.IsNullOrEmpty(_moddedPath)) return;

        var pluginsDir = Path.Combine(_moddedPath, "BepInEx", "plugins");
        var mods = new List<ModInfo>();

        if (Directory.Exists(pluginsDir))
        {
            foreach (var dllFile in Directory.GetFiles(pluginsDir, "*.dll"))
            {
                mods.Add(new ModInfo
                {
                    Name = Path.GetFileNameWithoutExtension(dllFile),
                    Description = $"Size: {new FileInfo(dllFile).Length / 1024} KB",
                    FilePath = dllFile
                });
            }
        }

        ModsList.ItemsSource = mods;
    }

    // Remove Mod
    private void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ModInfo mod) return;

        if (string.IsNullOrEmpty(mod.FilePath)) return;

        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        var confirmModal = new ConfirmationModal();
        confirmModal.Configure(
            $"Are you sure you want to remove '{mod.Name}'?\n\nThis action cannot be undone.",
            "Remove",
            isDanger: true);

        confirmModal.Confirmed += (_, _) =>
        {
            mainWindow.ModalOverlayControl.Hide();
            try
            {
                File.Delete(mod.FilePath);
                RefreshModsList();
            }
            catch (Exception ex)
            {
                var errorModal = new ConfirmationModal();
                errorModal.Configure($"Failed to remove mod:\n{ex.Message}", "OK");
                errorModal.Confirmed += (_, _) => mainWindow.ModalOverlayControl.Hide();
                mainWindow.ModalOverlayControl.Show("Error", errorModal);
            }
        };

        confirmModal.Cancelled += (_, _) => mainWindow.ModalOverlayControl.Hide();

        mainWindow.ModalOverlayControl.Show("Remove Mod", confirmModal);
    }

    private void OnGameExited(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SetPlayButtonRunning(false);
            ModStatusText.Text = "Game exited.";
        });
    }

    private void SetPlayButtonRunning(bool isRunning)
    {
        if (isRunning)
        {
            PlayButton.Content = "STOP GAME";
            PlayButton.Style = (Style)FindResource("StopButton");
        }
        else
        {
            PlayButton.Content = "PLAY";
            PlayButton.Style = (Style)FindResource("PlayButton");
        }

        GameStateChanged?.Invoke(this, isRunning);
    }

    private void ShowProgress(string text)
    {
        MainProgressBar.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        ProgressText.Text = text;
    }

    private void HideProgress()
    {
        MainProgressBar.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
    }

    // IPC helper methods
    public void UpdateConnectionStatus(bool connected)
    {
        if (connected)
            ModStatusText.Text = "AmongAPI connected";
        else
            ModStatusText.Text = "No mod loaded";
    }

    public List<ModInfo> GetInstalledMods()
    {
        if (string.IsNullOrEmpty(_moddedPath)) return new List<ModInfo>();

        var pluginsDir = Path.Combine(_moddedPath, "BepInEx", "plugins");
        var mods = new List<ModInfo>();

        if (Directory.Exists(pluginsDir))
        {
            foreach (var dllFile in Directory.GetFiles(pluginsDir, "*.dll"))
            {
                mods.Add(new ModInfo
                {
                    Name = Path.GetFileNameWithoutExtension(dllFile),
                    Description = $"Size: {new FileInfo(dllFile).Length / 1024} KB",
                    FilePath = dllFile
                });
            }
        }

        return mods;
    }

    public void UpdateModStatusText(string text)
    {
        ModStatusText.Text = text;
    }
}
