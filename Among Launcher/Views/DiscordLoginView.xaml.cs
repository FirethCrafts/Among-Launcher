using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;

namespace AmongLauncher.Views;

public partial class DiscordLoginView : UserControl
{
    private const string AuthorizeUrl = "https://discord.com/oauth2/authorize?client_id=1533706803748147240&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fcallback%2F&scope=identify";
    private const string CallbackPrefix = "http://localhost:5000/callback/";

    public event EventHandler<string>? LoginCodeReceived;
    public event EventHandler? LoginCancelled;

    public DiscordLoginView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            // Set desktop Edge User-Agent so Discord serves the web login form
            // instead of redirecting to the Discord app
            Browser.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.Source = new Uri(AuthorizeUrl);
        }
        catch (Exception ex)
        {
            ShowError($"WebView2 initialization failed: {ex.Message}\n\nPlease install the WebView2 Runtime from https://developer.microsoft.com/en-us/microsoft-edge/webview2/");
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Ignore discord:// app-protocol redirects; the web flow handles auth without the app
        if (e.Uri.StartsWith("discord://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            return;
        }

        if (e.Uri.StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            var uri = new Uri(e.Uri);
            var query = uri.Query;

            // Extract code parameter
            if (query.Contains("code="))
            {
                var codeStart = query.IndexOf("code=") + 5;
                var codeEnd = query.IndexOf('&', codeStart);
                var code = codeEnd == -1
                    ? Uri.UnescapeDataString(query.Substring(codeStart))
                    : Uri.UnescapeDataString(query.Substring(codeStart, codeEnd - codeStart));

                Dispatcher.BeginInvoke(() => LoginCodeReceived?.Invoke(this, code));
            }
            // Handle error (access denied)
            else if (query.Contains("error="))
            {
                Dispatcher.BeginInvoke(() => ShowError("Login was denied or cancelled."));
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        LoginCancelled?.Invoke(this, EventArgs.Empty);
    }
}
