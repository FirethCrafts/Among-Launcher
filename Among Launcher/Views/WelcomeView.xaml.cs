using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AmongLauncher.Auth;
using AmongLauncher.Models;

namespace AmongLauncher.Views;

public partial class WelcomeView : UserControl
{
    private readonly DiscordAuthService _authService = new();

    public WelcomeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event EventHandler<DiscordUserProfile>? LoginCompleted;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var reduce = App.ReduceMotion;

        // Bloom breathe loop (skip when reduce-motion is on)
        if (!reduce)
        {
            var breathe = new DoubleAnimation(0.35, 0.26, new Duration(TimeSpan.FromSeconds(2)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Bloom.BeginAnimation(OpacityProperty, breathe);
        }

        // Title pop
        if (!reduce)
        {
            var pop = new DoubleAnimation(0.94, 1, new Duration(TimeSpan.FromMilliseconds(400)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            TitleScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            TitleScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        // Subtitle + button fade-up with delays
        var subAnim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            BeginTime = TimeSpan.FromMilliseconds(reduce ? 0 : 120)
        };
        SubtitleText.BeginAnimation(OpacityProperty, subAnim);
        if (!reduce)
            SubtitleTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(220)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(120)
                });

        var loginAnim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            BeginTime = TimeSpan.FromMilliseconds(reduce ? 0 : 180)
        };
        LoginButton.BeginAnimation(OpacityProperty, loginAnim);
        if (!reduce)
            LoginTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(220)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(180)
                });
    }

    private async void DiscordLogin_Click(object sender, RoutedEventArgs e)
    {
        DiscordUserProfile? profile = null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            profile = await _authService.LoginAsync(cts.Token);
        }
        catch (Exception ex)
        {
            ShowError($"Login failed:\n{ex.Message}");
            return;
        }

        if (profile is null)
        {
            ShowError("Login was cancelled (you closed the browser or denied access).");
            return;
        }

        LoginCompleted?.Invoke(this, profile);
    }

    private void ShowError(string message)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        var errorModal = new ConfirmationModal();
        errorModal.Configure(message, "OK");
        errorModal.Confirmed += (_, _) => mainWindow.ModalOverlayControl.Hide();
        mainWindow.ModalOverlayControl.Show("Error", errorModal);
    }
}
