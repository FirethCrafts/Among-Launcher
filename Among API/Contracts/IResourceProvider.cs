namespace AmongApi.Contracts;

public interface IResourceProvider
{
    Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default);
}
