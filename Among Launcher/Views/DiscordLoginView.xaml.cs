using System.Diagnostics;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace AmongLauncher.Views;

public partial class DiscordLoginView : UserControl
{
    private const string AuthorizeUrl = "https://discord.com/oauth2/authorize?client_id=1533706803748147240&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fcallback%2F&scope=identify";
    private const string CallbackPrefix = "http://localhost:5000/callback/";
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _completed;

    public event EventHandler<string>? LoginCodeReceived;
    public event EventHandler? LoginCancelled;

    public DiscordLoginView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Let Discord detect WebView2 (no User-Agent override) so it shows
        // the "Continue to Discord" app-redirect screen.

        _cts = new CancellationTokenSource(CallbackTimeout);

        StartCallbackListener();
        _ = WaitForCallbackLoopAsync(_cts.Token);

        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.Source = new Uri(AuthorizeUrl);
        }
        catch (Exception ex)
        {
            ShowError($"WebView2 initialization failed: {ex.Message}\n\nPlease install the WebView2 Runtime from https://developer.microsoft.com/en-us/microsoft-edge/webview2/");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }

    private void StartCallbackListener()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(CallbackPrefix);
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener = null;
            ShowError("Port 5000 is already in use. Please close any other application using that port (such as another instance of this launcher) and try again.");
        }
        catch (Exception ex)
        {
            _listener = null;
            ShowError($"Could not start login callback listener: {ex.Message}");
        }
    }

    private async Task WaitForCallbackLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;

        try
        {
            while (!ct.IsCancellationRequested && !_completed && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                var code = ExtractCode(context.Request.Url);
                var error = ExtractQueryParam(context.Request.Url, "error");
                await SendSuccessPageAsync(context);

                if (_completed) return;

                if (!string.IsNullOrEmpty(code))
                {
                    _completed = true;
                    var captured = code;
                    _ = Dispatcher.BeginInvoke(() => LoginCodeReceived?.Invoke(this, captured));
                    Cleanup();
                    return;
                }

                if (!string.IsNullOrEmpty(error))
                {
                    _ = Dispatcher.BeginInvoke(() => ShowError("Login was denied or cancelled."));
                }
            }

            // If we get here via cancellation/timeout without completing, report it.
            if (!_completed && ct.IsCancellationRequested)
            {
                _ = Dispatcher.BeginInvoke(() => ShowError("Login timed out. Please try again."));
            }
        }
        catch (OperationCanceledException)
        {
            if (!_completed)
                _ = Dispatcher.BeginInvoke(() => ShowError("Login timed out. Please try again."));
        }
        catch (Exception ex)
        {
            if (!_completed)
                _ = Dispatcher.BeginInvoke(() => ShowError($"Login callback failed: {ex.Message}"));
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Let discord:// links open the Discord app; the OAuth callback
        // arrives later on localhost:5000/callback/ via the HttpListener.
        if (e.Uri.StartsWith("discord://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowError($"Could not open the Discord app: {ex.Message}");
                return;
            }

            ShowStatus("Opening Discord... Complete authorization in the app.");
            return;
        }

        // Web flow may still complete directly inside WebView2.
        if (e.Uri.StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            var uri = new Uri(e.Uri);
            var code = ExtractCode(uri);
            var error = ExtractQueryParam(uri, "error");

            if (!string.IsNullOrEmpty(code))
            {
                _completed = true;
                Cleanup();
                _ = Dispatcher.BeginInvoke(() => LoginCodeReceived?.Invoke(this, code));
            }
            else if (!string.IsNullOrEmpty(error))
            {
                _ = Dispatcher.BeginInvoke(() => ShowError("Login was denied or cancelled."));
            }
        }
    }

    private static string? ExtractCode(Uri? uri)
    {
        return ExtractQueryParam(uri, "code");
    }

    private static string? ExtractQueryParam(Uri? uri, string name)
    {
        if (uri == null) return null;
        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
                return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }

    private static async Task SendSuccessPageAsync(HttpListenerContext context)
    {
        const string html = """
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>Among Launcher</title></head>
            <body style="font-family:Segoe UI,sans-serif;background:#0c0c12;color:#e6e6ee;text-align:center;padding-top:80px;">
                <h1>Login successful!</h1>
                <p>You can close this tab and return to the app.</p>
            </body></html>
            """;
        try
        {
            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
        }
        catch
        {
            // Best-effort response; the code has already been captured.
        }
        finally
        {
            try { context.Response.OutputStream.Close(); } catch { }
            try { context.Response.Close(); } catch { }
        }
    }

    private void ShowStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ShowStatus(message));
            return;
        }
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ShowError(message));
            return;
        }
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cleanup()
    {
        try { _cts?.Cancel(); } catch { }
        if (_listener != null)
        {
            try
            {
                if (_listener.IsListening) _listener.Stop();
            }
            catch { }
            try { _listener.Close(); } catch { }
            _listener = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _completed = true;
        Cleanup();
        LoginCancelled?.Invoke(this, EventArgs.Empty);
    }
}
