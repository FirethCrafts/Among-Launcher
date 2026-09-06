using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class InGameView : UserControl
{
    public event EventHandler<string>? JoinLobbyRequested;

    private bool _busy;

    public InGameView()
    {
        InitializeComponent();
    }

    public void SetPlayers(List<string> names, List<int>? levels = null, List<int>? pings = null)
    {
        var items = new List<PlayerRow>();
        for (var i = 0; i < names.Count; i++)
        {
            var parts = new List<string>();
            if (levels != null && i < levels.Count)
                parts.Add($"Level {levels[i]}");
            if (pings != null && i < pings.Count)
                parts.Add($"{pings[i]}ms");
            items.Add(new PlayerRow { Name = names[i], Detail = string.Join(" • ", parts) });
        }
        PlayersList.ItemsSource = items;
        PlayersHeaderText.Text = $"PLAYERS ({items.Count})";
        PlayersEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetMods(List<string> names, List<string>? versions = null)
    {
        var items = new List<PlayerRow>();
        for (var i = 0; i < names.Count; i++)
        {
            var detail = versions != null && i < versions.Count ? versions[i] ?? "" : "";
            items.Add(new PlayerRow { Name = names[i], Detail = detail });
        }
        ModsList.ItemsSource = items;
        ModsHeaderText.Text = $"MODS ({items.Count})";
        ModsEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetLobbyCode(string? code)
    {
        // Never blank a non-empty box: ignore empty updates when the user
        // (or a previous prefill) already entered a code.
        if (string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(LobbyCodeTextBox.Text))
            return;
        LobbyCodeTextBox.Text = code ?? "";
    }

    public string GetLobbyCode() => LobbyCodeTextBox.Text?.Trim() ?? "";

    public void SetStatus(string? message)
    {
        JoinStatusText.Text = message ?? "";
        JoinStatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        JoinButton.IsEnabled = !busy;
        LobbyCodeTextBox.IsEnabled = !busy;
        JoinButton.Content = busy ? "WORKING..." : "JOIN";
    }

    private void JoinButton_Click(object sender, RoutedEventArgs e) => TryJoin();

    private void LobbyCodeTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            TryJoin();
    }

    private void TryJoin()
    {
        if (_busy) return;
        var code = (LobbyCodeTextBox.Text ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[A-Za-z0-9]{4,8}$"))
        {
            JoinErrorText.Text = "Enter a valid 4–8 character lobby code.";
            JoinErrorText.Visibility = Visibility.Visible;
            LobbyCodeTextBox.Focus();
            return;
        }
        JoinErrorText.Visibility = Visibility.Collapsed;
        LobbyCodeTextBox.Text = code;
        JoinLobbyRequested?.Invoke(this, code);
    }

    private sealed class PlayerRow
    {
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
    }
}
