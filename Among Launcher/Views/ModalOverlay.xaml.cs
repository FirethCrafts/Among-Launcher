using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AmongLauncher.Views;

public partial class ModalOverlay : UserControl
{
    public ModalOverlay()
    {
        InitializeComponent();
    }

    public void Show(string title, UIElement content)
    {
        ModalTitle.Text = title;
        ModalContent.Content = content;
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        ModalContent.Content = null;
    }

    private void Backdrop_Click(object sender, MouseButtonEventArgs e)
    {
        Hide();
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        // Prevent clicks on the card from closing the modal
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
