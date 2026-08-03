using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class ConfirmationModal : UserControl
{
    public event EventHandler? Confirmed;
    public event EventHandler? Cancelled;

    public ConfirmationModal()
    {
        InitializeComponent();
    }

    public void Configure(string message, string confirmText, bool isDanger = false)
    {
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (isDanger)
        {
            ConfirmButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
        }
        else
        {
            ConfirmButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed?.Invoke(this, EventArgs.Empty);
    }
}
