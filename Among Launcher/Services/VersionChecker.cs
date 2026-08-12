using System.Diagnostics;
using System.Text.Json;

namespace AmongLauncher.Services;

public static class VersionChecker
{
    private const string GitHubRepo = "FirethCrafts/Among-Launcher";
    private const string AssetName = "AmongApi.dll";

    public static Version? GetCurrentVersion(string moddedPath)
    {
        var dllPath = Path.Combine(moddedPath, "BepInEx", "plugins", AssetName);
        Services.LauncherLog.Write($"[VersionCheck] Looking for DLL: {dllPath}");
        Services.LauncherLog.Write($"[VersionCheck] DLL exists: {File.Exists(dllPath)}");
        
        if (!File.Exists(dllPath)) return null;

        var info = FileVersionInfo.GetVersionInfo(dllPath);
        Services.LauncherLog.Write($"[VersionCheck] File version: {info.FileVersion}");
        Services.LauncherLog.Write($"[VersionCheck] File parts: {info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}");
        
        var major = info.FileMajorPart;
        var minor = info.FileMinorPart;
        var build = info.FileBuildPart;
        var revision = info.FilePrivatePart;

        if (major == 0 && minor == 0 && build == 0 && revision == 0)
            return null;

        return new Version(major, minor, build, revision);
    }

    public static async Task<(bool UpdateAvailable, Version? LatestVersion, string? DownloadUrl)> CheckForUpdateAsync(
        HttpClient http, string moddedPath)
    {
        var current = GetCurrentVersion(moddedPath);
        Services.LauncherLog.Write($"[VersionCheck] Current version: {current?.ToString() ?? "null"}");
        Services.LauncherLog.Write($"[VersionCheck] Modded path: {moddedPath}");

        try
        {
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");

            var url = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            Services.LauncherLog.Write($"[VersionCheck] Checking: {url}");
            
            var response = await http.GetAsync(url);
            Services.LauncherLog.Write($"[VersionCheck] Response: {(int)response.StatusCode} {response.ReasonPhrase}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            Services.LauncherLog.Write($"[VersionCheck] Tag: {tag ?? "null"}");
            
            if (string.IsNullOrEmpty(tag)) return (false, null, null);

            var versionStr = tag.StartsWith('v') ? tag[1..] : tag;
            Services.LauncherLog.Write($"[VersionCheck] Version string: {versionStr}");
            
            Version? latest = null;
            if (!Version.TryParse(versionStr, out latest))
            {
                Services.LauncherLog.Write($"[VersionCheck] Tag is not a semver, checking for assets...");
            }
            
            Services.LauncherLog.Write($"[VersionCheck] Latest version: {latest}");

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : "";
                    if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
                }
            }

            Services.LauncherLog.Write($"[VersionCheck] Download URL: {downloadUrl ?? "null"}");

            if (latest == null)
            {
                if (downloadUrl != null)
                {
                    Services.LauncherLog.Write($"[VersionCheck] Tag unparsable, but asset found - update available");
                    return (true, null, downloadUrl);
                }
                Services.LauncherLog.Write($"[VersionCheck] No parseable version and no asset");
                return (false, null, null);
            }

            if (current == null)
            {
                Services.LauncherLog.Write($"[VersionCheck] No current version, update available: {downloadUrl != null}");
                return (downloadUrl != null, latest, downloadUrl);
            }

            var latestNorm = NormalizeVersion(latest);
            var currentNorm = NormalizeVersion(current);
            Services.LauncherLog.Write($"[VersionCheck] Comparing: latest={latestNorm}, current={currentNorm}, update={latestNorm > currentNorm}");
            if (latestNorm <= currentNorm) return (false, null, null);

            return (true, latest, downloadUrl);
        }
        catch (Exception ex)
        {
            Services.LauncherLog.Write($"[VersionCheck] Error: {ex.Message}");
            return (false, null, null);
        }
    }

    private static Version NormalizeVersion(Version v)
    {
        return v.Revision >= 0
            ? v
            : new Version(v.Major, v.Minor, v.Build >= 0 ? v.Build : 0, 0);
    }

    public static async Task<bool> DownloadAndUpdateAsync(HttpClient http, string downloadUrl, string moddedPath)
    {
        try
        {
            var pluginsDir = Path.Combine(moddedPath, "BepInEx", "plugins");
            Directory.CreateDirectory(pluginsDir);
            var destPath = Path.Combine(pluginsDir, AssetName);

            // Backup current version
            if (File.Exists(destPath))
            {
                var backupPath = destPath + ".bak";
                File.Copy(destPath, backupPath, overwrite: true);
            }

            var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
