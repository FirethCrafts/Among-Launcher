using System.Windows;
using System.Windows.Controls;
using AmongLauncher.Models;

namespace AmongLauncher.Views;

public partial class PresetModLibraryModal : UserControl
{
    public static readonly List<PresetMod> Presets =
    [
        new("EHR (Endless Host Roles)", "Host-only mod with 450+ roles", "Gurge44/EndlessHostRoles", "EHR.dll"),
        new("AUnlocker", "Unlock cosmetics, account, chat and more", "astra1dev/AUnlocker"),
        new("Town of Us Mira", "Town of Us rebuilt on MiraAPI", "AU-Avengers/TOU-Mira", "TownOfUsMira.dll"),
        new("Town of Us Reactivated", "TOU-R, cleaner using MiraAPI", "badzyn/TOU-Mira", "TownOfUsMira.dll"),
        new("Lotus", "Quality host-only mod with custom cosmetics", "Lotus-AU/LotusContinued", "Lotus.dll")
    ];

    public IReadOnlyList<PresetMod> AvailablePresets => Presets;

    public event EventHandler<(PresetMod preset, Button button)>? InstallRequested;

    public PresetModLibraryModal()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PresetMod preset)
            InstallRequested?.Invoke(this, (preset, btn));
    }
}
