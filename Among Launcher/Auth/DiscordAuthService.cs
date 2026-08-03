using System.Net;
using System.Text;
using System.Text.Json;
using AmongLauncher.Models;

namespace AmongLauncher.Auth;

public class DiscordAuthService
{
    private const string ClientId = "1533706803748147240";
    private static readonly string ClientSecret = "Um7wPIDVkCS9ro-0ZltYrs1NUI2q2LLh";
    private const string RedirectUri = "http://localhost:5000/callback/";
    private const string CallbackUrl = "http://localhost:5000/callback/";

    private const string AuthorizeUrl =
        "https://discord.com/oauth2/authorize" +
        "?client_id=1533706803748147240" +
        "&response_type=code" +
        "&redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fcallback%2F" +
        "&scope=identify";

    public async Task<DiscordUserProfile?> LoginAsync(CancellationToken ct = default)
    {
        if (ClientSecret == "REPLACE_WITH_CLIENT_SECRET")
            throw new InvalidOperationException(
                "Discord ClientSecret has not been set. Edit DiscordAuthService.ClientSecret with the secret from your Discord application.");

        using var listener = new HttpListener();
        listener.Prefixes.Add(CallbackUrl);
        listener.Start();

        // Open browser for the user to authorize
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AuthorizeUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            listener.Stop();
            throw new InvalidOperationException($"Could not open the browser: {ex.Message}", ex);
        }

        // Wait for the redirect callback
        var context = await WaitForCallbackAsync(listener, ct);
        if (context == null)
        {
            listener.Stop();
            return null; // cancelled / timed out
        }

        var code = ExtractCode(context.Request.Url);
        await SendSuccessPageAsync(context);

        listener.Stop();

        if (string.IsNullOrEmpty(code))
            return null; // no code in callback (user denied, etc.)

        var token = await ExchangeCodeForTokenAsync(code, ct);
        if (token == null)
            return null;

        return await FetchUserProfileAsync(token, ct);
    }

    private async Task<HttpListenerContext?> WaitForCallbackAsync(HttpListener listener, CancellationToken ct)
    {
        try
        {
            var getTask = listener.GetContextAsync();
            var tcs = new TaskCompletionSource<bool>();
            using (ct.Register(() => tcs.TrySetResult(true)))
            {
                var completed = await Task.WhenAny(getTask, tcs.Task);
                if (completed != getTask)
                    return null;
            }

            return await getTask;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (HttpListenerException)
        {
            return null;
        }
    }

    private static string? ExtractCode(Uri? uri)
    {
        if (uri == null) return null;
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "code")
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
        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.OutputStream.Close();
    }

    private async Task<string?> ExchangeCodeForTokenAsync(string code, CancellationToken ct)
    {
        using var client = new HttpClient();

        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri
        };

        var content = new FormUrlEncodedContent(values);

        var response = await client.PostAsync("https://discord.com/api/v10/oauth2/token", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("access_token", out var tokenEl))
            return tokenEl.GetString();

        throw new InvalidOperationException("Token exchange succeeded but no access_token was returned.");
    }

    private async Task<DiscordUserProfile?> FetchUserProfileAsync(string accessToken, CancellationToken ct)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("https://discord.com/api/v10/users/@me", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException($"Profile fetch failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.GetProperty("id").GetString() ?? string.Empty;
        var username = root.GetProperty("username").GetString() ?? string.Empty;
        var globalName = root.TryGetProperty("global_name", out var gn) ? gn.GetString() : null;

        string avatarUrl;
        if (root.TryGetProperty("avatar", out var avatarEl) && !avatarEl.ValueKind.Equals(JsonValueKind.Null))
        {
            var avatar = avatarEl.GetString();
            avatarUrl = string.IsNullOrEmpty(avatar)
                ? "https://cdn.discordapp.com/embed/avatars/0.png"
                : $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png";
        }
        else
        {
            avatarUrl = "https://cdn.discordapp.com/embed/avatars/0.png";
        }

        return new DiscordUserProfile(id, username, globalName, avatarUrl);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "<no response body>";
        }
    }
}
