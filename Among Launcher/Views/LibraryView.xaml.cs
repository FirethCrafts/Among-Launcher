using System.Windows;
using System.Windows.Controls;
using AmongLauncher.Config;
using AmongLauncher.Models;
using AmongLauncher.Services.Lobby;

namespace AmongLauncher.Views;

public partial class LibraryView : UserControl
{
    private LibraryManager? _library;
    private string? _moddedPath;

    public LibraryView()
    {
        InitializeComponent();
        Loaded += LibraryView_Loaded;
    }

    private void LibraryView_Loaded(object sender, RoutedEventArgs e)
    {
        _library = new LibraryManager(LauncherConfig.Load());
        RefreshLibrary();
    }

    public void RefreshLibrary()
    {
        if (_library == null) return;

        var entries = _library.LoadLibrary();
        LibraryList.ItemsSource = entries;
        LibraryInfoText.Text = entries.Count == 0
            ? "Your library is empty. Copy mods here from the Home screen to reuse them across profiles."
            : $"{entries.Count} mod(s) stored in your library.";
    }

    private string? GetPluginsDir()
    {
        _moddedPath ??= LauncherConfig.Load().ModdedInstallPath;
        var pluginsDir = System.IO.Path.Combine(_moddedPath, "BepInEx", "plugins");
        return System.IO.Directory.Exists(pluginsDir) ? pluginsDir : null;
    }

    private void InstallLibraryMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not LibraryEntry entry) return;
        if (string.IsNullOrEmpty(entry.FileName)) return;

        var pluginsDir = GetPluginsDir();
        if (pluginsDir == null)
        {
            MessageBox.Show("Please install BepInEx first.", "Library", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_library.InstallToPlugins(entry.FileName, pluginsDir))
        {
            LibraryInfoText.Text = $"Installed '{entry.FileName}' to the game.";
            if (Window.GetWindow(this) is MainWindow mw && mw.MainView != null)
                mw.MainView.RefreshModsList();
        }
        else
        {
            MessageBox.Show($"Library file '{entry.FileName}' is missing.", "Library", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLibrary();
        }
    }

    private void RemoveLibraryMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not LibraryEntry entry) return;
        if (string.IsNullOrEmpty(entry.FileName)) return;

        var result = MessageBox.Show(
            $"Remove '{entry.FileName}' from the library?\n\nThe file will be deleted.",
            "Remove from Library",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _library.RemoveFromLibrary(entry.FileName);
        RefreshLibrary();
    }
}
