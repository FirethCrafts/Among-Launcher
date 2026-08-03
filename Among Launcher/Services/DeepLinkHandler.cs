namespace AmongLauncher.Services;

public static class DeepLinkHandler
{
    public const string Scheme = "amongus-launcher";

    public static string? FindDeepLinkArgument()
    {
        var args = Environment.GetCommandLineArgs();
        return args.FirstOrDefault(a =>
            a.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase));
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

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            key.SetValue("", $"URL:{Scheme} Protocol");
            key.SetValue("URL Protocol", "");

            using var shell = key.CreateSubKey(@"shell\open\command");
            shell.SetValue("", $"\"{exePath}\" \"%1\"");
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
