using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class ChangelogModal : UserControl
{
    public event EventHandler? Closed;
    public event EventHandler? UpdateRequested;

    public ChangelogModal()
    {
        InitializeComponent();
    }

    public void Configure(string currentVersion, string changelogText)
    {
        VersionText.Text = $"Version {currentVersion} \u2014 What's new:";
        ChangelogContent.Text = changelogText;
    }

    public void ShowUpdateButtons()
    {
        UpdateButton.Visibility = Visibility.Visible;
        CloseButton.Content = "Later";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateRequested?.Invoke(this, EventArgs.Empty);
    }
}
