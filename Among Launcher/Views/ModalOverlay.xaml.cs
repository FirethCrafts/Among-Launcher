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

        // Backdrop fade in
        Backdrop.Opacity = 0;
        Backdrop.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.7, new Duration(TimeSpan.FromMilliseconds(220))));

        // Card entrance: fade + slide up + slight grow
        ModalCard.Opacity = 0;
        ModalCardScale.ScaleX = 0.96;
        ModalCardScale.ScaleY = 0.96;
        ModalCardTranslate.Y = 8;

        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)));
        ModalCard.BeginAnimation(OpacityProperty, fade);

        if (App.ReduceMotion) return;

        ModalCardTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ModalCardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.96, 1, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = ease });
        ModalCardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96, 1, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = ease });
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
