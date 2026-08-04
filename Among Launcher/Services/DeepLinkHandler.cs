using System.Text.RegularExpressions;

namespace AmongLauncher.Services;

public static class DeepLinkHandler
{
    public const string Scheme = "amongus-launcher";
    public const string JoinScheme = "amonglauncher";

    /// <summary>Among Us lobby codes are exactly 6 uppercase letters (A-Z).</summary>
    private static readonly Regex RoomCodeRegex = new("^[A-Z]{6}$", RegexOptions.Compiled);

    public record JoinRequest(string Code);

    /// <summary>
    /// Finds the custom-protocol URI in the process arguments (the OS passes the
    /// full URI as a single argument when a registered scheme is clicked). Returns
    /// null when the app was started normally.
    /// </summary>
    public static string? FindDeepLinkArgument()
    {
        var args = Environment.GetCommandLineArgs();
        return args.FirstOrDefault(a =>
            a.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith($"{JoinScheme}://", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses a join deep link. Supports both query and path forms:
    ///   amonglauncher://join?code=ABCDEF
    ///   amonglauncher://join/ABCDEF
    /// The code is trimmed, upper-cased, and must be exactly 6 alphabetical
    /// characters. Returns null for any other URI shape or invalid code.
    /// </summary>
    public static JoinRequest? TryParseJoin(string deepLink)
    {
        if (!Uri.TryCreate(deepLink, UriKind.Absolute, out var uri))
            return null;
        if (!string.Equals(uri.Scheme, JoinScheme, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(uri.Host, "join", StringComparison.OrdinalIgnoreCase))
            return null;

        // Query form: join?code=ABCDEF
        var code = ExtractParam(uri.Query.TrimStart('?'), "code");

        // Path form: join/ABCDEF (Host = "join", AbsolutePath = "/ABCDEF")
        if (string.IsNullOrWhiteSpace(code))
        {
            var segments = uri.AbsolutePath.Split('/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 1)
                code = segments[0];
        }

        return NormalizeRoomCode(code) is { } valid ? new JoinRequest(valid) : null;
    }

    /// <summary>
    /// Validates and normalizes a room code: trims, upper-cases, and requires
    /// exactly 6 alphabetical characters. Returns null when invalid.
    /// </summary>
    public static string? NormalizeRoomCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var normalized = code.Trim().ToUpperInvariant();
        return RoomCodeRegex.IsMatch(normalized) ? normalized : null;
    }

    public static List<ModDownloadRequest> Parse(string deepLink)
    {
        var requests = new List<ModDownloadRequest>();

        if (!Uri.TryCreate(deepLink, UriKind.Absolute, out var uri))
            return requests;

        // amongus-launcher://install?mods=<url1>,<url2>,<url3>
        if (!string.Equals(uri.Host, "install", StringComparison.OrdinalIgnoreCase))
            return requests;

        var modsParam = ExtractParam(uri.Query.TrimStart('?'), "mods");
        if (string.IsNullOrEmpty(modsParam)) return requests;

        foreach (var part in modsParam.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var url = Uri.UnescapeDataString(part.Trim());
            if (!Uri.TryCreate(url, UriKind.Absolute, out _)) continue;

            var fileName = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrEmpty(fileName))
                fileName = $"mod_{requests.Count}.dll";

            requests.Add(new ModDownloadRequest(url, fileName));
        }

        return requests;
    }

    public static void RegisterProtocol()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            foreach (var scheme in new[] { Scheme, JoinScheme })
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
                key.SetValue("", $"URL:{scheme} Protocol");
                key.SetValue("URL Protocol", "");
                using var shell = key.CreateSubKey(@"shell\open\command");
                shell.SetValue("", $"\"{exePath}\" \"%1\"");
            }
        }
        catch
        {
            // Best effort - protocol registration failure shouldn't break startup
        }
    }

    private static string? ExtractParam(string query, string name)
    {
        foreach (var pair in query.Split('&'))
        {
            var idx = pair.IndexOf('=');
            if (idx > 0 && pair[..idx].Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair[(idx + 1)..];
        }
        return null;
    }
}

public class ModDownloadRequest
{
    public string Url { get; }
    public string FileName { get; }

    public ModDownloadRequest(string url, string fileName)
    {
        Url = url;
        FileName = fileName;
    }
}
