using Velopack;
using Velopack.Sources;

namespace AmongLauncher.Services;

public static class UpdateService
{
    private const string GitHubRepo = "FirethCrafts/Among-Launcher";
    
    public static UpdateManager? UpdateManager { get; private set; }
    private static UpdateInfo? _pendingUpdate;
    private static string? _pendingLauncherChangelog;
    
    public static string? PendingLauncherChangelog => _pendingLauncherChangelog;
    
    public static void Initialize()
    {
        UpdateManager = new UpdateManager(
            new GithubSource($"https://github.com/{GitHubRepo}", null, false));
    }
    
    public static async Task<bool> CheckForUpdateAsync()
    {
        if (UpdateManager == null) { _pendingLauncherChangelog = null; return false; }
        
        try
        {
            _pendingUpdate = await UpdateManager.CheckForUpdatesAsync();
            if (_pendingUpdate == null)
            {
                _pendingLauncherChangelog = null;
                return false;
            }
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");
                var json = await http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var body = doc.RootElement.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
                var (launcherChangelog, _) = ReleaseChangelogParser.Parse(body);
                _pendingLauncherChangelog = launcherChangelog;
            }
            catch
            {
                _pendingLauncherChangelog = null;
            }
            return _pendingUpdate != null;
        }
        catch
        {
            _pendingLauncherChangelog = null;
            return false;
        }
    }
    
    public static async Task ApplyUpdateAsync(Action<int>? progress = null, Action<string>? status = null)
    {
        if (UpdateManager == null || _pendingUpdate == null) return;
        
        await UpdateManager.DownloadUpdatesAsync(_pendingUpdate, progress);
        status?.Invoke("Installing...");
        UpdateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
