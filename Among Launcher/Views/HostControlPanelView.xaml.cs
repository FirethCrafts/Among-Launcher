using System.Collections.ObjectModel;
using System.Windows;
using AmongLauncher.Models;

namespace AmongLauncher.Views;

public partial class HostControlPanelView : System.Windows.Controls.UserControl
{
    public ObservableCollection<LobbyPlayer> Players { get; } = new();

    public event EventHandler? RePostRequested;
    public event EventHandler? DisbandRequested;
    public event EventHandler<string>? KickRequested;

    public HostControlPanelView(LobbyInfo lobby)
    {
        InitializeComponent();
        CodeText.Text = lobby.Code;
        RegionText.Text = $"{lobby.Region}  {lobby.RegionIp}:{lobby.RegionPort}";
        DataContext = this;
    }

    public void UpdatePlayers(List<LobbyPlayer> players)
    {
        Players.Clear();
        foreach (var p in players) Players.Add(p);
        PlayersCountText.Text = $"{players.Count} players";
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CodeText.Text);
    }

    private void RePostButton_Click(object sender, RoutedEventArgs e)
    {
        RePostRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DisbandButton_Click(object sender, RoutedEventArgs e)
    {
        DisbandRequested?.Invoke(this, EventArgs.Empty);
    }

    private void KickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LobbyPlayer player })
            KickRequested?.Invoke(this, player.DiscordUserId);
    }
}
