using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AmongLauncher.Views;

public partial class SettingsView
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        var config = Config.LauncherConfig.Load();
        ServerUrlTextBox.Text = config.ServerUrl;

        var locator = new GameDetection.AmongUsLocator();
        var path = locator.FindAmongUs();
        GamePathText.Text = path ?? "Not found";
    }

    private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Among Us Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            GamePathText.Text = dialog.FolderName;

            var config = Config.LauncherConfig.Load();
            config.GamePath = dialog.FolderName;
            config.Save();
        }
    }

    private void ResetInstall_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will delete the modded Among Us installation and copy it again from your Steam library.\n\nContinue?",
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
