namespace AmongApi.Contracts;

public interface IManifestSource
{
    Task<ModManifest> GetManifestAsync(CancellationToken ct = default);
}
