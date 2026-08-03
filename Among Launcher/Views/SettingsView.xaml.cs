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

        var locator = new Steam.AmongUsLocator();
        var path = locator.FindAmongUs();
        GamePathText.Text = path ?? "Not found";
    }

    private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Among Us.exe",
            Filter = "Executable|Among Us.exe|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            var path = System.IO.Path.GetDirectoryName(dialog.FileName);
            GamePathText.Text = path ?? dialog.FileName;

            var config = Config.LauncherConfig.Load();
            config.GamePath = path ?? dialog.FileName;
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
