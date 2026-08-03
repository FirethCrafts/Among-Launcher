using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class PresetModLibraryModal : UserControl
{
    public event EventHandler<(string modName, Button installButton)>? InstallModRequested;

    public PresetModLibraryModal()
    {
        InitializeComponent();
    }

    private void InstallAUnlocker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            InstallModRequested?.Invoke(this, ("aunlocker", button));
    }

    private void InstallBetterAU_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            InstallModRequested?.Invoke(this, ("better-among-us", button));
    }

    private void InstallTownOfUs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            InstallModRequested?.Invoke(this, ("town-of-us", button));
    }

    private void InstallOtherRoles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            InstallModRequested?.Invoke(this, ("the-other-roles", button));
    }
}
