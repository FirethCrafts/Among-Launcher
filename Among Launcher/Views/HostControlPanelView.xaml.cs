using System.Collections.ObjectModel;
using System.Windows;
using AmongLauncher.Models;

namespace AmongLauncher.Views;

/// <summary>
/// Row view-model for the players list. <see cref="LobbyPlayer"/> is kept
/// untouched; level/ping detail text is built here in the view layer.
/// </summary>
public sealed class HostPlayerRow
{
    public string DisplayName { get; init; } = "";
    public bool IsHost { get; init; }
    public string DetailText { get; init; } = "";
    public bool HasDetail => !string.IsNullOrEmpty(DetailText);
}

public partial class HostControlPanelView : System.Windows.Controls.UserControl
{
    public ObservableCollection<HostPlayerRow> Players { get; } = new();

    public event EventHandler? PostRequested;
    public event EventHandler? DisbandRequested;
    public event EventHandler<string>? KickRequested;

    private int _maxPlayers = 15;

    private static readonly System.Windows.Media.SolidColorBrush PostedBrush =
        new(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));

    public HostControlPanelView(LobbyInfo lobby)
    {
        InitializeComponent();
        CodeText.Text = lobby.Code;
        RegionText.Text = lobby.Region;
        HostText.Text = lobby.Host;
        _maxPlayers = lobby.MaxPlayers > 0 ? lobby.MaxPlayers : 15;
        SetSubtitle(lobby.Host, lobby.Region, 0, _maxPlayers);
        SetPosted(false);
        SetStatus("");
        UpdatePlayers(new List<LobbyPlayer>());
        DataContext = this;
    }

    public void UpdatePlayers(List<LobbyPlayer> players)
    {
        players ??= new List<LobbyPlayer>();
        Players.Clear();
        foreach (var p in players)
        {
            Players.Add(new HostPlayerRow
            {
                DisplayName = p.PlayerName ?? "",
                IsHost = p.IsHost,
                DetailText = BuildDetail(p.Level, p.Ping),
            });
        }

        var n = players.Count;
        PlayersHeaderText.Text = $"PLAYERS ({n}/{_maxPlayers})";
        PlayersMetaText.Text = n == 1
            ? $"{n}/{_maxPlayers} · 1 player"
            : $"{n}/{_maxPlayers} · {n} players";

        var empty = n == 0;
        PlayersEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        PlayersList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetSubtitle(string host, string region, int n, int max)
    {
        _maxPlayers = max > 0 ? max : 15;
        SubtitleText.Text = $"Hosting as {host} • {region} • {n}/{_maxPlayers} players";
    }

    public void SetPosted(bool posted)
    {
        PostButton.Visibility = posted ? Visibility.Collapsed : Visibility.Visible;
        MetaStatusText.Text = posted ? "POSTED" : "LOCAL";
        MetaStatusText.Foreground = posted
            ? PostedBrush
            : (System.Windows.Media.Brush)FindResource("TextMuted");
    }

    public void SetStatus(string message)
    {
        StatusLine.Text = message ?? "";
        StatusLine.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string BuildDetail(int? level, int? ping)
    {
        var parts = new List<string>(2);
        if (level.HasValue) parts.Add($"Lv {level.Value}");
        if (ping.HasValue) parts.Add($"{ping.Value}ms");
        return string.Join(" • ", parts);
    }

    private async void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CodeText.Text);
            CopiedLabel.Visibility = Visibility.Visible;
            await Task.Delay(1500);
            CopiedLabel.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CopiedLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void PostButton_Click(object sender, RoutedEventArgs e)
    {
        PostRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DisbandButton_Click(object sender, RoutedEventArgs e)
    {
        DisbandRequested?.Invoke(this, EventArgs.Empty);
    }

    private void KickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HostPlayerRow row })
            KickRequested?.Invoke(this, row.DisplayName);
    }
}
