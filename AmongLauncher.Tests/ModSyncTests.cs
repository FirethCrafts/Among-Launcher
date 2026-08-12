using System.Security.Cryptography;
using System.Text;
using AmongLauncher.Models;
using AmongLauncher.Services;
using AmongLauncher.Services.Lobby;
using Moq;
using Xunit;

namespace AmongLauncher.Tests;

public class ModSyncTests : IDisposable
{
    private readonly string _tempDir;

    public ModSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AmongLauncherTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> CreateFileWithContent(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(path, bytes);
        return ComputeSha256(bytes);
    }

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private static HttpClient CreateHttpClientReturning(byte[] content, string mediaType = "application/octet-stream")
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content) { Headers = { { "Content-Type", mediaType } } }
            }));
        return new HttpClient(handler);
    }

    private static LobbyBackendClient CreateBackendClient(HttpClient http)
    {
        return new LobbyBackendClient(http, new Config.LauncherConfig { ServerUrl = "https://test-server.example.com/api" });
    }

    [Fact]
    public async Task DiffAsync_MissingFile_ReturnsAsMissing()
    {
        var sync = new ModSetSync(_tempDir);
        var target = new List<ModSetEntry>
        {
            new() { FileName = "missing.dll", Sha256 = "abc123" }
        };

        var missing = await sync.DiffAsync(target, CancellationToken.None);

        Assert.Single(missing);
        Assert.Equal("missing.dll", missing[0].FileName);
    }

    [Fact]
    public async Task DiffAsync_ExistingFile_NoHash_NoMissing()
    {
        var sync = new ModSetSync(_tempDir);
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "existing.dll"), new byte[] { 1, 2, 3 });

        var target = new List<ModSetEntry>
        {
            new() { FileName = "existing.dll", Sha256 = null }
        };

        var missing = await sync.DiffAsync(target, CancellationToken.None);

        Assert.Empty(missing);
    }

    [Fact]
    public async Task DiffAsync_CorrectHash_NoMissing()
    {
        var sync = new ModSetSync(_tempDir);
        var hash = await CreateFileWithContent(Path.Combine(_tempDir, "mod.dll"), "test content");

        var target = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll", Sha256 = hash }
        };

        var missing = await sync.DiffAsync(target, CancellationToken.None);

        Assert.Empty(missing);
    }

    [Fact]
    public async Task DiffAsync_WrongHash_ReturnsAsMissing()
    {
        var sync = new ModSetSync(_tempDir);
        await CreateFileWithContent(Path.Combine(_tempDir, "mod.dll"), "test content");

        var target = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll", Sha256 = "wronghash123" }
        };

        var missing = await sync.DiffAsync(target, CancellationToken.None);

        Assert.Single(missing);
        Assert.Equal("mod.dll", missing[0].FileName);
    }

    [Fact]
    public async Task DiffAsync_EmptyFile_ReturnsAsMissing()
    {
        var sync = new ModSetSync(_tempDir);
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "empty.dll"), Array.Empty<byte>());

        var target = new List<ModSetEntry>
        {
            new() { FileName = "empty.dll", Sha256 = "anything" }
        };

        var missing = await sync.DiffAsync(target, CancellationToken.None);

        Assert.Single(missing);
    }

    [Fact]
    public async Task DiffAsync_MultipleFiles_ReportsAllMissing()
    {
        var sync = new ModSetSync(_tempDir);
        var target = new List<ModSetEntry>
        {
            new() { FileName = "mod1.dll" },
            new() { FileName = "mod2.dll" },
            new() { FileName = "mod3.dll" }
        };

        var missing = await sync.DiffAsync(target, CancellationToken.None);

        Assert.Equal(3, missing.Count);
    }

    [Fact]
    public async Task DiffAsync_CaseInsensitiveFileName_OnWindows()
    {
        var sync = new ModSetSync(_tempDir);
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "Mod.dll"), new byte[] { 1, 2, 3 });

        var target = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll", Sha256 = null }
        };

        var missing = await sync.DiffAsync(target, CancellationToken.None);

        Assert.Empty(missing);
    }

    [Fact]
    public async Task DiffAsync_Cancellation_ThrowsOperationCanceled()
    {
        var sync = new ModSetSync(_tempDir);
        var target = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll" }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => sync.DiffAsync(target, cts.Token));
    }

    [Fact]
    public async Task InstallAsync_NullHttpClient_SkipsDownload()
    {
        var sync = new ModSetSync(_tempDir, http: null, backend: null);
        var missing = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll", DownloadUrl = "https://example.com/mod.dll" }
        };

        await sync.InstallAsync(missing, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_tempDir, "mod.dll")));
    }

    [Fact]
    public async Task InstallAsync_EmptyDownloadUrl_SkipsEntry()
    {
        var http = new HttpClient();
        var backend = CreateBackendClient(http);

        var sync = new ModSetSync(_tempDir, http, backend);
        var missing = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll", DownloadUrl = "" }
        };

        await sync.InstallAsync(missing, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_tempDir, "mod.dll")));
    }

    [Fact]
    public async Task InstallAsync_Cancellation_ThrowsOperationCanceled()
    {
        var sync = new ModSetSync(_tempDir);
        var missing = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll", DownloadUrl = "https://example.com/mod.dll" }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => sync.InstallAsync(missing, cts.Token));
    }

    [Fact]
    public async Task InstallAsync_VerifiesSha256_AfterDownload()
    {
        var correctContent = "correct mod content";
        var correctContentBytes = Encoding.UTF8.GetBytes(correctContent);
        var correctHash = ComputeSha256(correctContentBytes);

        var http = CreateHttpClientReturning(correctContentBytes);
        var backend = CreateBackendClient(http);

        var sync = new ModSetSync(_tempDir, http, backend);
        var missing = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll", DownloadUrl = "https://test-server.example.com/api/v1/mods/mod.dll/download", Sha256 = correctHash }
        };

        await sync.InstallAsync(missing, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_tempDir, "mod.dll")));
    }

    [Fact]
    public async Task InstallAsync_Sha256Mismatch_ThrowsAndDeletesFile()
    {
        var actualContentBytes = Encoding.UTF8.GetBytes("actual content");

        var http = CreateHttpClientReturning(actualContentBytes);
        var backend = CreateBackendClient(http);

        var sync = new ModSetSync(_tempDir, http, backend);
        var missing = new List<ModSetEntry>
        {
            new() { FileName = "mod.dll", DownloadUrl = "https://test-server.example.com/api/v1/mods/mod.dll/download", Sha256 = "expectedhash" }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.InstallAsync(missing, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_tempDir, "mod.dll")));
    }

    [Fact]
    public async Task DiffAsync_EmptyTarget_ReturnsEmpty()
    {
        var sync = new ModSetSync(_tempDir);
        var target = new List<ModSetEntry>();

        var missing = await sync.DiffAsync(target, CancellationToken.None);

        Assert.Empty(missing);
    }
}
