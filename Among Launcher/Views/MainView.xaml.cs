using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
        RefreshProfiles();
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

    private void PlayButton_Click(object sender, RoutedEventArgs e)
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

        _gameManager.LaunchGame(exePath);
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

    // Profile switcher
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
        var sync = new ModSetSync(pluginsDir, (_, url, dest) => DownloadModWithRetryAsync(url, dest));

        try
        {
            ShowProgress($"Syncing profile '{profile.Name}'...");
            var missing = await sync.DiffAsync(profile.Mods, CancellationToken.None);
            if (missing.Count > 0)
            {
                ProgressText.Text = $"Installing {missing.Count} missing mod(s)...";
                await sync.InstallAsync(missing, null, CancellationToken.None);
                RefreshModsList();
            }

            ModStatusText.Text = $"Profile '{profile.Name}' applied.";
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

    private async Task DownloadModWithRetryAsync(string url, string destPath)
    {
        var delays = new[] { 250, 500, 1000, 2000, 4000 };
        for (var i = 0; i < delays.Length; i++)
        {
            try
            {
                await DownloadModToFileAsync(url, destPath);
                return;
            }
            catch (IOException) when (i < delays.Length - 1)
            {
                await Task.Delay(delays[i]);
            }
        }
    }

    private async Task DownloadModToFileAsync(string url, string destPath)
    {
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0) return;

        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fileStream);
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
