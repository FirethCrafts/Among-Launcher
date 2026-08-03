namespace AmongApi.Services;

public class LocalFileManifestSource : IManifestSource
{
    private readonly string _manifestPath;
    private readonly ManualLogSource _log;

    public LocalFileManifestSource(string manifestPath, ManualLogSource log)
    {
        _manifestPath = manifestPath;
        _log = log;
    }

    public async Task<ModManifest> GetManifestAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_manifestPath))
        {
            _log.LogWarning($"[Manifest] File not found: {_manifestPath}. Returning empty manifest.");
            return new ModManifest();
        }

        var json = await File.ReadAllTextAsync(_manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<ModManifest>(json);

        if (manifest == null)
        {
            _log.LogError("[Manifest] Failed to deserialize manifest JSON.");
            return new ModManifest();
        }

        _log.LogInfo($"[Manifest] Loaded {manifest.Mods.Count} mods from {_manifestPath}");
        return manifest;
    }
}
