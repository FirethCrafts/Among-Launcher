using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

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

        // Entrance: fade + slide up
        ModalCard.Opacity = 0;
        if (ModalCard.RenderTransform is TranslateTransform t)
            t.Y = 8;

        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)));
        ModalCard.BeginAnimation(OpacityProperty, fade);

        if (ModalCard.RenderTransform is TranslateTransform tt && !App.ReduceMotion)
        {
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(220))));
        }
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
