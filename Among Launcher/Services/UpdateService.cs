using Velopack;
using Velopack.Sources;

namespace AmongLauncher.Services;

public static class UpdateService
{
    private const string GitHubRepo = "FirethCrafts/Among-Launcher";
    
    public static UpdateManager? UpdateManager { get; private set; }
    private static UpdateInfo? _pendingUpdate;
    
    public static void Initialize()
    {
        UpdateManager = new UpdateManager(
            new GithubSource($"https://github.com/{GitHubRepo}", null, false));
    }
    
    public static async Task<bool> CheckForUpdateAsync()
    {
        if (UpdateManager == null) return false;
        
        try
        {
            _pendingUpdate = await UpdateManager.CheckForUpdatesAsync();
            return _pendingUpdate != null;
        }
        catch
        {
            return false;
        }
    }
    
    public static async Task ApplyUpdateAsync()
    {
        if (UpdateManager == null || _pendingUpdate == null) return;
        
        await UpdateManager.DownloadUpdatesAsync(_pendingUpdate);
        UpdateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
