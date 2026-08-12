using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class ChangelogModal : UserControl
{
    public event EventHandler? Closed;
    public event EventHandler? UpdateRequested;

    private bool _isShowingUpdateDialog;

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
        _isShowingUpdateDialog = true;
        UpdateButton.Visibility = Visibility.Visible;
        ViewChangelogButton.Visibility = Visibility.Visible;
        CloseButton.Content = "Later";
        CloseButton.Visibility = Visibility.Visible;
        ChangelogBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowChangelogView()
    {
        _isShowingUpdateDialog = false;
        UpdateButton.Visibility = Visibility.Collapsed;
        ViewChangelogButton.Visibility = Visibility.Collapsed;
        CloseButton.Content = "Got it";
        CloseButton.Visibility = Visibility.Visible;
        ChangelogBorder.Visibility = Visibility.Visible;
    }

    private void ShowUpdateDialogView()
    {
        _isShowingUpdateDialog = true;
        UpdateButton.Visibility = Visibility.Visible;
        ViewChangelogButton.Visibility = Visibility.Visible;
        CloseButton.Content = "Later";
        CloseButton.Visibility = Visibility.Visible;
        ChangelogBorder.Visibility = Visibility.Collapsed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isShowingUpdateDialog)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ShowUpdateDialogView();
        }
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ViewChangelogButton_Click(object sender, RoutedEventArgs e)
    {
        ShowChangelogView();
    }
}
