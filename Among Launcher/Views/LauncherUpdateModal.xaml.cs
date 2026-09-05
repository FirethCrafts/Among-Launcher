using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class LauncherUpdateModal : UserControl
{
    public event EventHandler? UpdateRequested;
    public event EventHandler? CloseRequested;

    public LauncherUpdateModal()
    {
        InitializeComponent();
    }

    public void Configure(string message)
    {
        MessageText.Text = message;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
