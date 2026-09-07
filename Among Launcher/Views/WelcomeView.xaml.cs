using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AmongLauncher.Auth;
using AmongLauncher.Models;

namespace AmongLauncher.Views;

public partial class WelcomeView : UserControl
{
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

    private void DiscordLogin_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        var loginView = new DiscordLoginView();

        loginView.LoginCodeReceived += async (_, code) =>
        {
            mainWindow.ModalOverlayControl.Hide();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var authService = new DiscordAuthService();
                var profile = await authService.ExchangeCodeAndFetchProfileAsync(code, cts.Token);
                if (profile != null)
                    LoginCompleted?.Invoke(this, profile);
                else
                    ShowError("Failed to get user profile.");
            }
            catch (Exception ex)
            {
                ShowError($"Login failed: {ex.Message}");
            }
        };

        loginView.LoginCancelled += (_, _) =>
        {
            mainWindow.ModalOverlayControl.Hide();
            ShowError("Login was cancelled.");
        };

        mainWindow.ModalOverlayControl.Show("Log in with Discord", loginView);
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
