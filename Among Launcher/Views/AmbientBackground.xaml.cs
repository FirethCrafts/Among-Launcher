using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AmongLauncher.Views;

public partial class AmbientBackground : UserControl
{
    public AmbientBackground()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.ReduceMotion) return;

        var topAnim = new DoubleAnimation(0, 40, new Duration(TimeSpan.FromSeconds(80)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var bottomAnim = new DoubleAnimation(0, -36, new Duration(TimeSpan.FromSeconds(90)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        GlowTopTransform.BeginAnimation(TranslateTransform.YProperty, topAnim);
        GlowBottomTransform.BeginAnimation(TranslateTransform.YProperty, bottomAnim);
    }
}
