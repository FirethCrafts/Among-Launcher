namespace AmongApi.Services;

public class LocalFileResourceProvider : IResourceProvider
{
    private readonly ManualLogSource _log;

    public LocalFileResourceProvider(ManualLogSource log)
    {
        _log = log;
    }

    public Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        var path = url;

        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            path = path["file://".Length..];

        if (!File.Exists(path))
        {
            _log.LogError($"[Resource] File not found: {path}");
            return Task.FromException<byte[]>(new FileNotFoundException($"Mod file not found: {path}"));
        }

        _log.LogInfo($"[Resource] Reading file: {path}");
        var bytes = File.ReadAllBytes(path);
        return Task.FromResult(bytes);
    }
}
