namespace AmongApi.Services;

public class ModSyncService
{
    private readonly PluginConfig _config;
    private readonly ManualLogSource _log;
    private readonly ModLoader _loader;
    private readonly string _pluginsPath;

    public ModSyncService(PluginConfig config, ManualLogSource log)
    {
        _config = config;
        _log = log;
        _loader = new ModLoader(log);
        _pluginsPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = new SyncResult();

        try
        {
            var manifestSource = new LocalFileManifestSource(_config.GetManifestPath(), _log);
            var manifest = await manifestSource.GetManifestAsync(ct);

            if (manifest.Mods.Count == 0)
            {
                _log.LogInfo("[Sync] No mods in manifest. Nothing to sync.");
                return result;
            }

            var resourceProvider = new LocalFileResourceProvider(_log);

            foreach (var mod in manifest.Mods)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    _log.LogInfo($"[Sync] Processing {mod.Id} v{mod.Version} (kind={mod.Kind})...");

                    var bytes = await resourceProvider.GetBytesAsync(mod.Url, ct);

                    if (!FileManager.VerifyHash(bytes, mod.Sha256))
                    {
                        _log.LogWarning($"[Sync] {mod.Id}: hash mismatch. Skipping.");
                        result.Failed.Add(mod);
                        continue;
                    }

                    if (mod.Kind == "hostmod")
                    {
                        _loader.LoadAndActivate(bytes, mod.Id);
                        _log.LogInfo($"[Sync] {mod.Id}: loaded into memory.");
                    }
                    else
                    {
                        var destPath = Path.Combine(_pluginsPath, mod.FileName);
                        var fileExists = File.Exists(destPath);
                        var existingHash = fileExists ? FileManager.ComputeSha256File(destPath) : "";
                        var isSame = string.Equals(existingHash, mod.Sha256, StringComparison.OrdinalIgnoreCase);

                        if (isSame)
                        {
                            _log.LogInfo($"[Sync] {mod.Id}: already up to date on disk.");
                            result.UpToDate.Add(mod);
                            continue;
                        }

                        await File.WriteAllBytesAsync(destPath, bytes, ct);
                        _log.LogInfo($"[Sync] {mod.Id}: written to {destPath}");
                        result.Downloaded.Add(mod);

                        if (fileExists)
                        {
                            _log.LogWarning($"[Sync] {mod.Id}: updated. Restart required for changes to take effect.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError($"[Sync] {mod.Id}: failed - {ex.Message}");
                    result.Failed.Add(mod);
                }
            }

            _log.LogInfo($"[Sync] Complete. Loaded: {result.Downloaded.Count}, Up-to-date: {result.UpToDate.Count}, Failed: {result.Failed.Count}");
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[Sync] Sync cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogError($"[Sync] Fatal error: {ex.Message}");
        }

        return result;
    }
}
