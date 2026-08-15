using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class InGameView : UserControl
{
    public event EventHandler<string>? JoinLobbyRequested;
    
    public InGameView()
    {
        InitializeComponent();
    }
    
    public void SetPlayers(List<string> players)
    {
        PlayersList.ItemsSource = players;
    }
    
    public void SetMods(List<string> mods)
    {
        ModsList.ItemsSource = mods;
    }
    
    public void SetLobbyCode(string? code)
    {
        LobbyCodeTextBox.Text = code ?? "";
    }
    
    private void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        var code = LobbyCodeTextBox.Text.Trim();
        if (string.IsNullOrEmpty(code)) return;
        JoinLobbyRequested?.Invoke(this, code);
    }
}
