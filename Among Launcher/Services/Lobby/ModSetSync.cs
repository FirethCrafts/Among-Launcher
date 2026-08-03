using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public class ModSetSync
{
    private readonly string _pluginsDir;
    private readonly Func<string, string, string, Task> _downloadMod;

    public ModSetSync(string pluginsDir, Func<string, string, string, Task> downloadMod)
    {
        _pluginsDir = pluginsDir;
        _downloadMod = downloadMod;
    }

    public Task<List<ModSetEntry>> DiffAsync(List<ModSetEntry> target, CancellationToken ct)
    {
        Directory.CreateDirectory(_pluginsDir);
        var missing = new List<ModSetEntry>();
        foreach (var entry in target)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(_pluginsDir, entry.FileName);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                missing.Add(entry);
        }
        return Task.FromResult(missing);
    }

    public async Task InstallAsync(List<ModSetEntry> missing, CancellationToken ct)
    {
        foreach (var entry in missing)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(_pluginsDir, entry.FileName);

            // The host's mirrored mod set only carries file names (GetInstalledModSet
            // captures FileName), so a joiner sees an empty DownloadUrl. When no URL is
            // known, skip the download and treat the entry as "verify present" — the file
            // is often already installed locally. The backend should supply DownloadUrl for
            // a full mod sync; never throw on an empty URL.
            if (string.IsNullOrEmpty(entry.DownloadUrl))
                continue;

            await _downloadMod(entry.FileName, entry.DownloadUrl, dest);
        }
    }
}
