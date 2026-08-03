using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AmongLauncher.Auth;
using AmongLauncher.Models;

namespace AmongLauncher.Views;

public partial class WelcomeView : UserControl
{
    private readonly DiscordAuthService _authService = new();

    public WelcomeView()
    {
        InitializeComponent();
    }

    public event EventHandler<DiscordUserProfile>? LoginCompleted;

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
            MessageBox.Show($"Login failed:\n{ex.Message}", "Discord Login", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (profile is null)
        {
            MessageBox.Show("Login was cancelled (you closed the browser or denied access).", "Discord Login", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoginCompleted?.Invoke(this, profile);
    }
}
