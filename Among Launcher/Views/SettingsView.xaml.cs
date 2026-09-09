using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class SettingsView
{
    public event EventHandler? SignOutRequested;

    private bool _isInitializing;
    private string _currentSection = "Account";
    private string _discordUserId = string.Empty;
    private readonly AmongUsLocator _locator = new();

    private static string StorefrontLabel(Storefront? storefront) => storefront switch
    {
        Storefront.Steam => "Steam",
        Storefront.Epic => "Epic",
        Storefront.MicrosoftStore => "Microsoft Store",
        _ => "Auto"
    };

    private void SyncComboToStorefront(Storefront? storefront)
    {
        _isInitializing = true;
        StorefrontCombo.SelectedItem = StorefrontLabel(storefront);
        _isInitializing = false;
    }

    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        var config = Config.LauncherConfig.Load();
        DebugModeToggle.IsChecked = config.DebugMode;
        AutoPostToggle.IsChecked = config.AutoPostLobby;

        _isInitializing = true;
        if (StorefrontCombo.Items.Count == 0)
        {
            StorefrontCombo.Items.Add("Steam");
            StorefrontCombo.Items.Add("Epic");
            StorefrontCombo.Items.Add("Microsoft Store");
            StorefrontCombo.Items.Add("Auto");
        }
        SyncComboToStorefront(config.Storefront);
        _isInitializing = false;

        RefreshStorefrontSearch(config.Storefront);
        RefreshAccount();
        RefreshAbout(config);
        ShowSection(_currentSection);
    }

    private void DebugModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || !IsLoaded) return;
        var config = Config.LauncherConfig.Load();
        config.DebugMode = DebugModeToggle.IsChecked == true;
        config.Save();
    }

    private void AutoPostToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || !IsLoaded) return;
        var config = Config.LauncherConfig.Load();
        config.AutoPostLobby = AutoPostToggle.IsChecked == true;
        config.Save();
    }

    private void StorefrontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || !IsLoaded) return;

        var storefront = SelectedStorefront();
        RefreshStorefrontSearch(storefront);
        if (storefront.HasValue)
        {
            SaveStorefront(storefront.Value);
        }
    }

    private Storefront? SelectedStorefront()
    {
        return (StorefrontCombo.SelectedItem as string) switch
        {
            "Steam" => Storefront.Steam,
            "Epic" => Storefront.Epic,
            "Microsoft Store" => Storefront.MicrosoftStore,
            _ => null
        };
    }

    private void RefreshStorefrontSearch(Storefront? storefront)
    {
        if (storefront == null)
        {
            RunAutoScan();
            return;
        }

        var result = _locator.FindAmongUsForStorefront(storefront);

        if (result.Path != null)
        {
            GamePathText.Text = result.Path;
            StorefrontStatusText.Text = "";
            return;
        }

        GamePathText.Text = "Not found";
        StorefrontStatusText.Text = "";
    }

    private void RunAutoScan()
    {
        var all = _locator.FindAmongUsWithStorefront();

        if (all.DetectedButUnavailable)
        {
            GamePathText.Text = "Not found";
            StorefrontStatusText.Text =
                "An install was detected but is inaccessible. See the guide in the main install flow.";
            return;
        }

        var found = new List<GameSearchResult>();
        foreach (var sf in new[] { Storefront.Steam, Storefront.Epic, Storefront.MicrosoftStore })
        {
            var candidate = _locator.FindAmongUsForStorefront(sf);
            if (candidate.Path != null) found.Add(candidate);
        }

        if (found.Count == 0)
        {
            GamePathText.Text = "Not found";
            StorefrontStatusText.Text = "No Among Us installation found.";
            return;
        }

        if (found.Count == 1)
        {
            var only = found[0];
            GamePathText.Text = only.Path;
            if (only.Storefront.HasValue)
            {
                SaveStorefront(only.Storefront.Value);
                SyncComboToStorefront(only.Storefront.Value);
            }
            StorefrontStatusText.Text = $"Auto-detected: {only.Storefront}";
            return;
        }

        OpenPicker(found);
    }

    private void OpenPicker(List<GameSearchResult> found)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        var picker = new StorefrontPickerModal();
        picker.SetResults(found);
        picker.Selected += (_, chosen) =>
        {
            mainWindow.ModalOverlayControl.Hide();
            if (chosen.Storefront.HasValue)
            {
                SaveStorefront(chosen.Storefront.Value);
                SyncComboToStorefront(chosen.Storefront.Value);
            }
            GamePathText.Text = chosen.Path;
            StorefrontStatusText.Text = "";
        };

        mainWindow.ModalOverlayControl.Show("Choose Among Us Installation", picker);
    }

    private void SaveStorefront(Storefront? storefront)
    {
        var config = Config.LauncherConfig.Load();
        config.Storefront = storefront;
        config.Save();
    }

    private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Among Us Folder"
        };

        if (dialog.ShowDialog() != true) return;

        var selectedFolder = dialog.FolderName;
        GamePathText.Text = selectedFolder;

        var config = Config.LauncherConfig.Load();

        var matched = MatchStorefrontToFolder(selectedFolder);
        if (matched.HasValue)
        {
            config.Storefront = matched.Value;
            SyncComboToStorefront(matched.Value);
        }

        config.Save();
    }

    private Storefront? MatchStorefrontToFolder(string folder)
    {
        foreach (var storefront in new[] { Storefront.Steam, Storefront.Epic, Storefront.MicrosoftStore })
        {
            var result = _locator.FindAmongUsForStorefront(storefront);
            if (result.Path != null &&
                string.Equals(result.Path.TrimEnd('\\'), folder.TrimEnd('\\'), System.StringComparison.OrdinalIgnoreCase))
            {
                return storefront;
            }
        }

        return null;
    }

    private void ResetInstall_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will delete the modded Among Us installation and copy it again from your game library.\n\nContinue?",
            "Reset Installation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var moddedPath = Config.LauncherConfig.DefaultModdedPath();

        if (System.IO.Directory.Exists(moddedPath))
        {
            System.IO.Directory.Delete(moddedPath, true);
        }

        MessageBox.Show("Modded installation deleted. Click 'Install BepInEx' on the main page to set it up again.",
            "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void ShowSection(string name)
    {
        _currentSection = name;
        BreadcrumbSectionText.Text = name;
        AccountSection.Visibility = name == "Account" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsSection.Visibility = name == "Connections" ? Visibility.Visible : Visibility.Collapsed;
        StorageSection.Visibility = name == "Storage" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = name == "About" ? Visibility.Visible : Visibility.Collapsed;
        HighlightNavButton(AccountNavButton, name == "Account");
        HighlightNavButton(ConnectionsNavButton, name == "Connections");
        HighlightNavButton(StorageNavButton, name == "Storage");
        HighlightNavButton(AboutNavButton, name == "About");
    }

    private void HighlightNavButton(Button button, bool isActive)
    {
        if (isActive)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));
            button.Foreground = new SolidColorBrush(Colors.White);
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.Foreground = (Brush)FindResource("NavIconBrush");
        }
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            ShowSection(name);
        }
    }

    public void RefreshAccount()
    {
        var config = Config.LauncherConfig.Load();
        AccountUsernameText.Text = string.IsNullOrWhiteSpace(config.UserName)
            ? "Not signed in"
            : config.UserName;
        ConnectionUsernameText.Text = string.IsNullOrWhiteSpace(config.UserName)
            ? "@unknown"
            : "@" + config.UserName;
        ConnectionIdText.Text = _discordUserId;
        LoadAvatarImage(AccountAvatarImage, config.AvatarUrl);
        LoadAvatarImage(ConnectionAvatarImage, config.AvatarUrl);
    }

    public void SetDiscordUserId(string userId)
    {
        _discordUserId = userId ?? string.Empty;
        ConnectionIdText.Text = _discordUserId;
    }

    private static void LoadAvatarImage(System.Windows.Controls.Image image, string avatarUrl)
    {
        if (string.IsNullOrEmpty(avatarUrl))
        {
            image.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(avatarUrl, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            image.Source = bitmap;
        }
        catch
        {
            image.Source = null;
        }
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e) => SignOut();

    private void DisconnectButton_Click(object sender, RoutedEventArgs e) => SignOut();

    private void SignOut()
    {
        var config = Config.LauncherConfig.Load();
        config.AvatarUrl = string.Empty;
        config.UserName = string.Empty;
        config.DiscordAccessToken = string.Empty;
        config.Save();
        _discordUserId = string.Empty;
        RefreshAccount();
        SignOutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshAbout(Config.LauncherConfig config)
    {
        AppVersionText.Text = $"Among Launcher v{GetLauncherVersion()}";
        var changelog = LoadChangelogSince(config.LastSeenVersion);
        ChangelogText.Text = string.IsNullOrEmpty(changelog)
            ? "No new changes."
            : changelog;
    }

    private static string GetLauncherVersion()
    {
        var version = FileVersionInfo.GetVersionInfo(
            Assembly.GetEntryAssembly()?.Location ?? "").ProductVersion;
        return version ?? "1.0.0";
    }

    private static string LoadChangelogSince(string lastSeenVersion)
    {
        try
        {
            var changelogPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "CHANGELOG.md");
            if (!File.Exists(changelogPath))
                return string.Empty;

            if (string.IsNullOrEmpty(lastSeenVersion))
                return File.ReadAllText(changelogPath).Trim();

            var lines = File.ReadAllLines(changelogPath);
            var result = new List<string>();

            foreach (var line in lines)
            {
                if (line.StartsWith("## "))
                {
                    var version = line.Substring(3).Trim();
                    if (version == lastSeenVersion)
                        break;
                }
                result.Add(line);
            }

            return string.Join("\n", result).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (Services.UpdateService.UpdateManager == null)
        {
            UpdateStatusText.Text = "Update check runs on launch.";
            return;
        }

        try
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text = "Checking for updates...";
            var available = await Services.UpdateService.CheckForUpdateAsync();
            UpdateStatusText.Text = available
                ? "Update available — restart the launcher to install it."
                : "You're up to date.";
        }
        catch
        {
            UpdateStatusText.Text = "Update check failed. Try again later.";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = new List<(string Path, string EntryName)>();

            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmongLauncher");
            if (Directory.Exists(logDir))
            {
                foreach (var file in Directory.GetFiles(logDir, "*.log"))
                    files.Add((file, Path.GetFileName(file)));
            }

            var bepinexDir = Path.Combine(
                Config.LauncherConfig.DefaultModdedPath(), "BepInEx");
            if (Directory.Exists(bepinexDir))
            {
                foreach (var file in Directory.GetFiles(bepinexDir, "*.log", SearchOption.AllDirectories))
                    files.Add((file, "BepInEx-" + Path.GetFileName(file)));
            }

            if (files.Count == 0)
            {
                MessageBox.Show("No log files found.", "Export Logs",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export logs",
                Filter = "Zip archive (*.zip)|*.zip",
                FileName = "AmongLauncher-logs.zip"
            };

            if (dialog.ShowDialog() != true) return;

            using (var zip = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create))
            {
                foreach (var (path, entryName) in files)
                    zip.CreateEntryFromFile(path, entryName);
            }

            MessageBox.Show($"Exported {files.Count} log file(s).", "Export Logs",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export logs: {ex.Message}", "Export Logs",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
