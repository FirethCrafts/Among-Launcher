using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AmongLauncher.Config;
using AmongLauncher.Game;
using AmongLauncher.Installer;
using AmongLauncher.Models;
using AmongLauncher.GameDetection;
using AmongLauncher.Services.Lobby;

namespace AmongLauncher.Views;

public partial class MainView
{
    private readonly GameProcessManager _gameManager = new();
    private string? _moddedPath;
    private readonly HttpClient _httpClient = new();
    private Storefront? _storefront;

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
        RefreshProfiles();
    }

    private async Task CheckGameStatus()
    {
        GameStatusText.Text = "Searching for Among Us installation...";

        var locator = new AmongUsLocator();
        var storefront = Config.LauncherConfig.Load().Storefront;
        var gamePath = locator.FindAmongUsForStorefront(storefront).Path;

        if (gamePath == null)
        {
            GameStatusText.Text = "Among Us not found. Please install Among Us via Steam, Epic Games, or Xbox Game Pass.";
            PlayButton.IsEnabled = false;
            BrowseFilesButton.IsEnabled = false;
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
            BrowseFilesButton.IsEnabled = true;
            AddModButton.IsEnabled = true;
            InstallButton.Content = "Reinstall BepInEx";
        }
        else
        {
            GameStatusText.Text = $"Among Us found at:\n{gamePath}\n\nClick 'Install BepInEx' to set up the modded copy.";
            PlayButton.IsEnabled = false;
            BrowseFilesButton.IsEnabled = false;
            AddModButton.IsEnabled = false;
        }

        await Task.CompletedTask;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var locator = new AmongUsLocator();
        var savedStorefront = Config.LauncherConfig.Load().Storefront;
        var result = locator.FindAmongUsForStorefront(savedStorefront);

        if (result.DetectedButUnavailable)
        {
            ShowMsStoreAccessModal(result.Storefront);
            return;
        }

        if (result.Path == null)
        {
            MessageBox.Show("Among Us installation not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var sourcePath = result.Path;
        var storefront = result.Storefront;

        _storefront = storefront ?? Storefront.Steam;

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
            await bepinExInstaller.InstallAsync(_moddedPath, _storefront, new Progress<int>(percent =>
            {
                Dispatcher.Invoke(() => ProgressText.Text = $"Downloading BepInEx... {percent}%");
            }));

            // Only Steam needs steam_appid.txt; Epic/MS Store builds don't.
            if (_storefront == Storefront.Steam)
            {
                var steamAppIdPath = Path.Combine(_moddedPath, "steam_appid.txt");
                if (!File.Exists(steamAppIdPath))
                {
                    File.WriteAllText(steamAppIdPath, "945360");
                }
            }

            ShowProgress("Installing AmongAPI...");

            await InstallAmongApiAsync();

            HideProgress();
            GameStatusText.Text = $"Modded Among Us ready!\nLocation: {_moddedPath}";
            PlayButton.IsEnabled = true;
            BrowseFilesButton.IsEnabled = true;
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

    private void ShowMsStoreAccessModal(Storefront? storefront)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;
        var modal = new MsStoreAccessModal();
        modal.Configure(storefront);
        mainWindow.ModalOverlayControl.Show(
            storefront == Storefront.Epic ? "Epic install not found" : "Microsoft Store copy blocked",
            modal);
    }

    private bool TryShowUnavailableInstall()
    {
        if (!string.IsNullOrEmpty(_moddedPath)) return false;

        var result = new AmongUsLocator().FindAmongUsWithStorefront();
        if (!result.DetectedButUnavailable) return false;

        ShowMsStoreAccessModal(result.Storefront);
        return true;
    }

    private async Task InstallAmongApiAsync()
    {
        const string downloadUrl = "https://github.com/FirethCrafts/Among-Launcher/releases/latest/download/AmongApi.dll";
        var pluginsDir = Path.Combine(_moddedPath!, "BepInEx", "plugins");        Directory.CreateDirectory(pluginsDir);

        var destPath = Path.Combine(pluginsDir, "AmongApi.dll");

        var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fileStream);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gameManager.IsGameRunning())
        {
            StopGame();
            return;
        }

        if (string.IsNullOrEmpty(_moddedPath))
        {
            if (TryShowUnavailableInstall()) return;
            return;
        }

        var exePath = System.IO.Path.Combine(_moddedPath, "Among Us.exe");
        if (!System.IO.File.Exists(exePath))
        {
            MessageBox.Show("Among Us.exe not found in modded folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _gameManager.LaunchGame(exePath, GetLaunchArguments());
        SetPlayButtonRunning(true);
        ModStatusText.Text = "Game launched. AmongAPI.dll will load via BepInEx.";
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
        if (string.IsNullOrEmpty(_moddedPath))
        {
            if (TryShowUnavailableInstall()) return;
            return;
        }

        var exePath = System.IO.Path.Combine(_moddedPath, "Among Us.exe");
        if (!System.IO.File.Exists(exePath)) return;

        _gameManager.LaunchGame(exePath, GetLaunchArguments());
        SetPlayButtonRunning(true);
        ModStatusText.Text = "Game launched. AmongAPI.dll will load via BepInEx.";
    }

    private string? GetLaunchArguments()
    {
        return _storefront switch
        {
            Storefront.Epic => "-EpicPortal",
            _ => null
        };
    }

    private void BrowseFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_moddedPath)) return;

        try
        {
            if (!Directory.Exists(_moddedPath))
                Directory.CreateDirectory(_moddedPath);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _moddedPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        var logViewer = new LogViewerModal();
        mainWindow.ModalOverlayControl.Show("IPC Logs", logViewer);
    }

    // Add Mod Button - Show menu
    private void AddModButton_Click(object sender, RoutedEventArgs e)
    {
        AddModPopup.IsOpen = true;

        // Grow + fade entrance (skipped under reduce-motion)
        if (!App.ReduceMotion)
        {
            AddModPopupContent.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))));
            AddModPopupScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.95, 1, new Duration(TimeSpan.FromMilliseconds(160)))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                });
            AddModPopupScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.95, 1, new Duration(TimeSpan.FromMilliseconds(160)))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                });
        }
        else
        {
            AddModPopupContent.Opacity = 1;
        }
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

    // From Library - install a library mod into the game
    private void InstallLibraryMod_Click(object sender, RoutedEventArgs e)
    {
        AddModPopup.IsOpen = false;

        if (string.IsNullOrEmpty(_moddedPath))
        {
            MessageBox.Show("Please install BepInEx first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var library = new LibraryManager(LauncherConfig.Load());
        var entries = library.LoadLibrary();

        if (entries.Count == 0)
        {
            MessageBox.Show("Your library is empty. Copy mods to the library from the Home screen first.",
                "From Library", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        // Show a picker listing library mods
        var picker = new LibraryPickerModal(entries);
        picker.PickRequested += (_, entry) =>
        {
            mainWindow.ModalOverlayControl.Hide();
            var pluginsDir = Path.Combine(_moddedPath, "BepInEx", "plugins");
            if (library.InstallToPlugins(entry.FileName, pluginsDir))
            {
                RefreshModsList();
                ModStatusText.Text = $"Installed '{entry.FileName}' from library.";
            }
            else
            {
                MessageBox.Show($"Library file '{entry.FileName}' is missing.", "From Library",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        mainWindow.ModalOverlayControl.Show("From Library", picker);
    }

    // To Library - copy a local mod into the library
    private void ToLibraryMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ModInfo mod) return;
        if (string.IsNullOrEmpty(mod.FilePath)) return;

        var library = new LibraryManager(LauncherConfig.Load());
        if (library.AddToLibrary(mod.FilePath))
        {
            ModStatusText.Text = $"Copied '{mod.Name}' to library.";
        }
        else
        {
            MessageBox.Show("Failed to copy mod to library.", "To Library", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Install Preset Mod - Show preset library modal
    private void InstallPresetMod_Click(object sender, RoutedEventArgs e)
    {
        AddModPopup.IsOpen = false;

        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        var presetLibrary = new PresetModLibraryModal();
        presetLibrary.InstallRequested += async (_, args) =>
        {
            var (preset, button) = args;
            button.IsEnabled = false;
            button.Content = "Installing...";

            var success = await DownloadModFromGitHub(preset);
            button.Content = success ? "Installed" : "Install";
            button.IsEnabled = true;
        };

        mainWindow.ModalOverlayControl.Show("Preset Mod Library", presetLibrary);
    }

    // Download latest release DLL from a GitHub repo into BepInEx/plugins/
    private async Task<bool> DownloadModFromGitHub(PresetMod preset)
    {
        if (string.IsNullOrEmpty(_moddedPath))
        {
            MessageBox.Show("Please install BepInEx first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            var response = await _httpClient.GetAsync($"https://api.github.com/repos/{preset.Repo}/releases/latest");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                // First pass: prefer the preset's preferred asset name
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : "";
                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        (preset.PreferredAsset == null ||
                         string.Equals(name, preset.PreferredAsset, StringComparison.OrdinalIgnoreCase)))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
                }

                // Fallback: first .dll asset
                if (downloadUrl == null)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var n) ? n.GetString() : "";
                        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                            break;
                        }
                    }
                }
            }

            if (downloadUrl == null)
                throw new Exception("No DLL file found in release assets.");

            var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            if (string.IsNullOrEmpty(fileName))
                fileName = $"{preset.Name.Replace(" ", "")}.dll";

            var pluginsDir = Path.Combine(_moddedPath, "BepInEx", "plugins");
            Directory.CreateDirectory(pluginsDir);
            var destPath = Path.Combine(pluginsDir, fileName);

            var dllResponse = await _httpClient.GetAsync(downloadUrl);
            dllResponse.EnsureSuccessStatusCode();

            await using var stream = await dllResponse.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);

            RefreshModsList();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to install {preset.Name}:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // Refresh Mods List
    public void RefreshModsList()
    {
        ModsList.ItemsSource = GetInstalledMods();
    }

    // Profile switcher (WIP: saved profiles store no download URLs, so applying
    // a profile only verifies present mods and relocates missing ones to the library)
    private void RefreshProfiles()
    {
        var profiles = new ModProfileManager(LauncherConfig.Load()).LoadProfiles();
        ProfileCombo.DisplayMemberPath = nameof(ModProfile.Name);
        ProfileCombo.ItemsSource = profiles;
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var mods = GetInstalledMods();
        if (mods.Count == 0)
        {
            MessageBox.Show("No mods are currently installed to save.", "Save Profile",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        var saveButton = new Button
        {
            Content = "Save",
            Style = (Style)FindResource("StopButton"),
            Height = 36,
            Padding = new Thickness(20, 8, 20, 8)
        };
        var nameInput = new TextBox { Style = (Style)FindResource("GlassInput"), Margin = new Thickness(0, 0, 0, 16) };
        nameInput.KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter)
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Style = (Style)FindResource("SecondaryButton"),
            Height = 36,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(0, 0, 12, 0)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(saveButton);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Enter a name for this mod profile:",
            Foreground = (System.Windows.Media.Brush)FindResource("TextBody"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(nameInput);
        panel.Children.Add(buttons);

        saveButton.Click += (_, _) =>
        {
            var name = nameInput.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Profile name cannot be empty.", "Save Profile",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            mainWindow.ModalOverlayControl.Hide();
            var entries = mods
                .Select(m => new ModSetEntry { FileName = Path.GetFileName(m.FilePath) })
                .ToList();
            new ModProfileManager(LauncherConfig.Load()).SaveProfile(name, entries);
            RefreshProfiles();
            ModStatusText.Text = $"Profile '{name}' saved with {entries.Count} mod(s).";
        };

        cancelButton.Click += (_, _) => mainWindow.ModalOverlayControl.Hide();

        mainWindow.ModalOverlayControl.Show("Save Profile", panel);
        nameInput.Focus();
    }

    private async void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not ModProfile profile)
        {
            MessageBox.Show("Select a profile to apply first.", "Apply Profile",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrEmpty(_moddedPath))
        {
            MessageBox.Show("Please install BepInEx first.", "Apply Profile",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StopGame();

        var pluginsDir = Path.Combine(_moddedPath, "BepInEx", "plugins");
        var sync = new ModSetSync(pluginsDir, (_, url, dest) => DownloadModToFileAsync(url, dest));

        try
        {
            ShowProgress($"Syncing profile '{profile.Name}'...");
            var missing = await sync.DiffAsync(profile.Mods, CancellationToken.None);
            if (missing.Count > 0)
            {
                ProgressText.Text = $"Installing {missing.Count} missing mod(s)...";
                await sync.InstallAsync(missing, CancellationToken.None);
                RefreshModsList();
            }

            // Move any installed mods that don't belong to this profile into the library.
            var keepNames = profile.Mods.Select(m => m.FileName).ToList();
            var library = new LibraryManager(LauncherConfig.Load());
            var moved = library.MoveNonListedToLibrary(pluginsDir, keepNames);
            if (moved.Count > 0)
            {
                RefreshModsList();
                ModStatusText.Text = $"Profile '{profile.Name}' applied. Moved {moved.Count} mod(s) to library.";
            }
            else
            {
                ModStatusText.Text = $"Profile '{profile.Name}' applied.";
            }

            LaunchGame();
        }
        catch (Exception ex)
        {
            ModStatusText.Text = $"Failed to apply profile: {ex.Message}";
        }
        finally
        {
            HideProgress();
        }
    }

    private async Task DownloadModToFileAsync(string url, string destPath)
    {
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0) return;

        // Retry up to 5 times with backoff for file lock issues
        var delays = new[] { 250, 500, 1000, 2000, 4000 };
        for (var attempt = 0; attempt < delays.Length; attempt++)
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
            catch (IOException) when (attempt < delays.Length - 1)
            {
                await Task.Delay(delays[attempt]);
            }
        }
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
        var mods = new List<ModInfo>();
        if (string.IsNullOrEmpty(_moddedPath)) return mods;

        var pluginsDir = Path.Combine(_moddedPath, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsDir)) return mods;

        foreach (var dllFile in Directory.GetFiles(pluginsDir, "*.dll"))
        {
            mods.Add(new ModInfo
            {
                Name = Path.GetFileNameWithoutExtension(dllFile),
                Description = $"Size: {new FileInfo(dllFile).Length / 1024} KB",
                FilePath = dllFile
            });
        }

        return mods;
    }

    public void UpdateModStatusText(string text)
    {
        ModStatusText.Text = text;
    }
}
