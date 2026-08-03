using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class SettingsView
{
    private bool _isInitializing;
    private readonly AmongUsLocator _locator = new();

    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        var config = Config.LauncherConfig.Load();
        ServerUrlTextBox.Text = config.ServerUrl;

        _isInitializing = true;
        if (StorefrontCombo.Items.Count == 0)
        {
            StorefrontCombo.Items.Add("Steam");
            StorefrontCombo.Items.Add("Epic");
            StorefrontCombo.Items.Add("Microsoft Store");
            StorefrontCombo.Items.Add("Auto");
        }
        StorefrontCombo.SelectedItem = config.Storefront switch
        {
            Storefront.Steam => "Steam",
            Storefront.Epic => "Epic",
            Storefront.MicrosoftStore => "Microsoft Store",
            _ => "Auto"
        };
        _isInitializing = false;

        RefreshStorefrontSearch(config.Storefront);
    }

    private void StorefrontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || !IsLoaded) return;

        var storefront = SelectedStorefront();
        RefreshStorefrontSearch(storefront);
        SaveStorefront(storefront);
    }

    private Storefront? SelectedStorefront()
    {
        return StorefrontCombo.SelectedItem as string switch
        {
            "Steam" => Storefront.Steam,
            "Epic" => Storefront.Epic,
            "Microsoft Store" => Storefront.MicrosoftStore,
            _ => null
        };
    }

    private void RefreshStorefrontSearch(Storefront? storefront)
    {
        var result = _locator.FindAmongUsForStorefront(storefront);

        if (result.Path != null)
        {
            GamePathText.Text = result.Path;
            StorefrontStatusText.Text = "";
            return;
        }

        GamePathText.Text = "Not found";

        if (storefront == null)
        {
            var all = _locator.FindAmongUsWithStorefront();
            if (all.DetectedButUnavailable)
            {
                StorefrontStatusText.Text =
                    "An install was detected but is inaccessible. See the guide in the main install flow.";
            }
            else
            {
                StorefrontStatusText.Text = "No Among Us installation found.";
            }
        }
        else
        {
            StorefrontStatusText.Text = "";
        }
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
        config.GamePath = selectedFolder;

        var matched = MatchStorefrontToFolder(selectedFolder);
        if (matched.HasValue)
        {
            config.Storefront = matched.Value;
            _isInitializing = true;
            StorefrontCombo.SelectedItem = matched.Value switch
            {
                Storefront.Steam => "Steam",
                Storefront.Epic => "Epic",
                Storefront.MicrosoftStore => "Microsoft Store",
                _ => "Auto"
            };
            _isInitializing = false;
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

        var moddedPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmongLauncher", "ModdedAmongUs");

        if (System.IO.Directory.Exists(moddedPath))
        {
            System.IO.Directory.Delete(moddedPath, true);
        }

        MessageBox.Show("Modded installation deleted. Click 'Install BepInEx' on the main page to set it up again.",
            "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
