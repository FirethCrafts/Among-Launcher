using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public class ModSetSync
{
    private readonly string _pluginsDir;
    private readonly HttpClient? _http;
    private readonly LobbyBackendClient? _backend;

    public ModSetSync(string pluginsDir, HttpClient? http = null, LobbyBackendClient? backend = null)
    {
        _pluginsDir = pluginsDir;
        _http = http;
        _backend = backend;
    }

    public async Task<List<ModSetEntry>> DiffAsync(List<ModSetEntry> target, CancellationToken ct)
    {
        Directory.CreateDirectory(_pluginsDir);
        var missing = new List<ModSetEntry>();

        foreach (var entry in target)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(_pluginsDir, entry.FileName);

            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                missing.Add(entry);
                continue;
            }

            if (!string.IsNullOrEmpty(entry.Sha256))
            {
                var localHash = await Sha256Helper.HashFileAsync(path);
                if (!string.Equals(localHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    missing.Add(entry);
            }
        }

        return missing;
    }

    public async Task InstallAsync(List<ModSetEntry> missing, CancellationToken ct)
    {
        foreach (var entry in missing)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(_pluginsDir, entry.FileName);

            if (string.IsNullOrEmpty(entry.DownloadUrl))
                continue;

            if (_http == null || _backend == null)
                continue;

            var url = entry.DownloadUrl.StartsWith("http")
                ? entry.DownloadUrl
                : _backend.GetModDownloadUrl(entry.DownloadUrl);

            await ModDownloader.DownloadToFileAsync(_http, url, dest);

            if (!string.IsNullOrEmpty(entry.Sha256))
            {
                var hash = await Sha256Helper.HashFileAsync(dest);
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(dest);
                    throw new InvalidOperationException(
                        $"SHA-256 mismatch for {entry.FileName}: expected {entry.Sha256}, got {hash}");
                }
            }
        }
    }
}
