using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class SettingsView
{
    private bool _isInitializing;
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
        ServerUrlTextBox.Text = config.ServerUrl;
        BotWsEndpointTextBox.Text = config.BotWsEndpoint;
        ModdedRoleIdTextBox.Text = config.ModdedRoleId;
        VanillaRoleIdTextBox.Text = config.VanillaRoleId;

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
    }

    private void BotWsEndpointTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var config = Config.LauncherConfig.Load();
        config.BotWsEndpoint = BotWsEndpointTextBox.Text;
        config.Save();
    }

    private void ModdedRoleIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var config = Config.LauncherConfig.Load();
        config.ModdedRoleId = ModdedRoleIdTextBox.Text;
        config.Save();
    }

    private void VanillaRoleIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var config = Config.LauncherConfig.Load();
        config.VanillaRoleId = VanillaRoleIdTextBox.Text;
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
}
