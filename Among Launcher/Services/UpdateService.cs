using System.Diagnostics;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace AmongLauncher.Services;

public static class UpdateService
{
    private const string GitHubRepo = "FirethCrafts/Among-Launcher";
    
    public static UpdateManager? UpdateManager { get; private set; }
    private static UpdateInfo? _pendingUpdate;
    private static string? _pendingLauncherChangelog;
    private static string? _pendingModChangelog;
    
    public static string? PendingLauncherChangelog => _pendingLauncherChangelog;
    public static string? PendingModChangelog => _pendingModChangelog;
    
    public static void Initialize()
    {
        UpdateManager = new UpdateManager(
            new GithubSource($"https://github.com/{GitHubRepo}", null, false));
    }
    
    public static async Task<bool> CheckForUpdateAsync()
    {
        if (UpdateManager == null) { _pendingLauncherChangelog = null; _pendingModChangelog = null; return false; }
        
        try
        {
            _pendingUpdate = await UpdateManager.CheckForUpdatesAsync();
            if (_pendingUpdate != null)
            {
                // Velopack found an update with matching assets — fetch changelog and return true.
                await FetchReleaseChangelogAsync();
                return true;
            }
            
            // Velopack couldn't find a matching asset. Fall back to GitHub API version comparison.
            return await CheckGitHubReleaseFallbackAsync();
        }
        catch
        {
            _pendingLauncherChangelog = null;
            _pendingModChangelog = null;
            return false;
        }
    }
    
    private static async Task<bool> CheckGitHubReleaseFallbackAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // Get tag name (e.g. "v1.2.3" or "1.2.3")
            var tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                _pendingLauncherChangelog = null;
                _pendingModChangelog = null;
                return false;
            }
            
            // Strip leading 'v' or 'V' for version parsing
            var versionStr = tagName!.TrimStart('v', 'V');
            if (!Version.TryParse(versionStr, out var remoteVersion))
            {
                _pendingLauncherChangelog = null;
                _pendingModChangelog = null;
                return false;
            }
            
            // Compare against current launcher version
            var currentVersionStr = GetCurrentLauncherVersion();
            if (!Version.TryParse(currentVersionStr, out var currentVersion))
            {
                _pendingLauncherChangelog = null;
                _pendingModChangelog = null;
                return false;
            }
            
            if (remoteVersion <= currentVersion)
            {
                // No newer release — up to date.
                _pendingLauncherChangelog = null;
                _pendingModChangelog = null;
                return false;
            }
            
            // GitHub has a newer release. Fetch changelog even though Velopack can't auto-install.
            var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            var (launcherChangelog, modChangelog) = ReleaseChangelogParser.Parse(body);
            _pendingLauncherChangelog = launcherChangelog;
            _pendingModChangelog = modChangelog;
            return true;
        }
        catch
        {
            _pendingLauncherChangelog = null;
            _pendingModChangelog = null;
            return false;
        }
    }
    
    private static async Task FetchReleaseChangelogAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var body = doc.RootElement.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            var (launcherChangelog, modChangelog) = ReleaseChangelogParser.Parse(body);
            _pendingLauncherChangelog = launcherChangelog;
            _pendingModChangelog = modChangelog;
        }
        catch
        {
            _pendingLauncherChangelog = null;
            _pendingModChangelog = null;
        }
    }
    
    private static string GetCurrentLauncherVersion()
    {
        var version = FileVersionInfo.GetVersionInfo(
            System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "").ProductVersion;
        return version ?? "1.0.0";
    }
    
    public static async Task ApplyUpdateAsync(Action<int>? progress = null, Action<string>? status = null)
    {
        if (UpdateManager == null || _pendingUpdate == null) return;
        
        await UpdateManager.DownloadUpdatesAsync(_pendingUpdate, progress);
        status?.Invoke("Installing...");
        UpdateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
    
    public static async Task<(bool UpdateAvailable, string? DownloadUrl, string? Changelog)> CheckAmongApiUpdateAsync(string moddedPath)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");
        var (updateAvailable, _, downloadUrl, changelog) = await VersionChecker.CheckForUpdateAsync(http, moddedPath);
        return (updateAvailable, downloadUrl, changelog);
    }
}
