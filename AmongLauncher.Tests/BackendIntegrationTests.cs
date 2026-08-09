using System.Net.Http;
using Xunit;
using AmongLauncher.Models;
using AmongLauncher.Services.Lobby;
using AmongLauncher.Config;

namespace AmongLauncher.Tests;

/// <summary>
/// Integration tests that hit the live backend at https://among-us2.mel-homes.com/api.
/// Each test generates a unique transient lobby code (TST + 4 random hex chars) and
/// cleans up after itself in a best-effort finally block that never masks primary failures.
/// </summary>
public class BackendIntegrationTests : IDisposable
{
    // LobbyBackendClient uses v1/... relative paths, so ServerUrl must end at /api
    private const string ServerUrl = "https://among-us2.mel-homes.com/api";

    private readonly HttpClient _httpClient = new();
    private readonly LauncherConfig _config;
    private readonly LobbyBackendClient _backendClient;

    public BackendIntegrationTests()
    {
        _config = new LauncherConfig
        {
            ServerUrl = ServerUrl
        };
        _backendClient = new LobbyBackendClient(_httpClient, _config);
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Generates a unique TST-prefixed lobby code safe for parallel test runs.
    /// Produces exactly 6 uppercase letters (A-Z only) as required by the backend.
    /// Format: "TS" + 4 random uppercase letters.
    /// </summary>
    private static string GenerateLobbyCode()
    {
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var rng = Random.Shared;
        return "TS" + new string(Enumerable.Range(0, 4).Select(_ => letters[rng.Next(letters.Length)]).ToArray());
    }

    private static CreateLobbyRequest MakeRequest(string code) =>
        new(code, "NA", "TestHost", "vanilla", new List<ModInfoEntry>(), 15);

    // ─────────────────────────────────────────────────────────────────────────
    // Full Lobby Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateLobby_ReturnsSuccess()
    {
        var code = GenerateLobbyCode();
        try
        {
            var result = await _backendClient.CreateLobbyAsync(MakeRequest(code), CancellationToken.None);
            Assert.True(result, $"CreateLobbyAsync returned false for code {code}");
        }
        finally
        {
            try { await _backendClient.DisbandAsync(code, CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task GetLobby_ReturnsCorrectData()
    {
        var code = GenerateLobbyCode();
        await _backendClient.CreateLobbyAsync(MakeRequest(code), CancellationToken.None);

        try
        {
            var lobby = await _backendClient.GetLobbyAsync(code, CancellationToken.None);
            Assert.NotNull(lobby);
            Assert.Equal(code, lobby.Code);
            Assert.Equal("NA", lobby.Region);
            Assert.Equal("TestHost", lobby.Host);
            Assert.Equal(15, lobby.MaxPlayers);
        }
        finally
        {
            try { await _backendClient.DisbandAsync(code, CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Heartbeat_ReturnsSuccess()
    {
        var code = GenerateLobbyCode();
        await _backendClient.CreateLobbyAsync(MakeRequest(code), CancellationToken.None);

        try
        {
            var result = await _backendClient.HeartbeatAsync(code, "test_user", CancellationToken.None);
            Assert.True(result, $"HeartbeatAsync returned false for code {code}");
        }
        finally
        {
            try { await _backendClient.DisbandAsync(code, CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Repost_ReturnsSuccess()
    {
        var code = GenerateLobbyCode();
        await _backendClient.CreateLobbyAsync(MakeRequest(code), CancellationToken.None);

        try
        {
            var result = await _backendClient.RepostAsync(code, CancellationToken.None);
            Assert.True(result, $"RepostAsync returned false for code {code}");
        }
        finally
        {
            try { await _backendClient.DisbandAsync(code, CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Disband_ReturnsSuccess()
    {
        var code = GenerateLobbyCode();
        await _backendClient.CreateLobbyAsync(MakeRequest(code), CancellationToken.None);

        // Disband is the Act step here — no nested try/finally needed.
        var result = await _backendClient.DisbandAsync(code, CancellationToken.None);
        Assert.True(result, $"DisbandAsync returned false for code {code}");
    }

    [Fact]
    public async Task GetLobby_NonExistentCode_ReturnsNull()
    {
        // Use a valid-format but non-existent code — backend should return 404 → null.
        var code = "ZZZZZZ";
        var lobby = await _backendClient.GetLobbyAsync(code, CancellationToken.None);
        Assert.Null(lobby);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Full Lifecycle (sequential happy path)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullLobbyLifecycle_HappyPath()
    {
        var code = GenerateLobbyCode();

        // 1. Create
        var created = await _backendClient.CreateLobbyAsync(MakeRequest(code), CancellationToken.None);
        Assert.True(created, "Create failed");

        try
        {
            // 2. Read back
            var lobby = await _backendClient.GetLobbyAsync(code, CancellationToken.None);
            Assert.NotNull(lobby);
            Assert.Equal(code, lobby.Code);
            Assert.Equal(15, lobby.MaxPlayers);

            // 3. Heartbeat
            var hb = await _backendClient.HeartbeatAsync(code, "test_user", CancellationToken.None);
            Assert.True(hb, "Heartbeat failed");

            // 4. Repost
            var repost = await _backendClient.RepostAsync(code, CancellationToken.None);
            Assert.True(repost, "Repost failed");

            // 5. Disband (as part of Act — also serves as cleanup)
            var disbanded = await _backendClient.DisbandAsync(code, CancellationToken.None);
            Assert.True(disbanded, "Disband failed");
        }
        finally
        {
            // Best-effort safety net in case an assertion above threw before Disband
            try { await _backendClient.DisbandAsync(code, CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WebSocket Connection
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WebSocket_ConnectsToExistingLobby()
    {
        var code = GenerateLobbyCode();
        await _backendClient.CreateLobbyAsync(MakeRequest(code), CancellationToken.None);

        try
        {
            // Spec: WS /api/v1/ws/{code}?client_id={id}
            var wsUrl = $"wss://among-us2.mel-homes.com/api/v1/ws/{code}?client_id=test";
            var botClient = new LobbyBotClient();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await botClient.ConnectAsync(wsUrl);

            // Allow handshake to settle, then cleanly disconnect
            await Task.Delay(1500, cts.Token).ContinueWith(_ => { });
            botClient.Disconnect();
        }
        finally
        {
            try { await _backendClient.DisbandAsync(code, CancellationToken.None); } catch { /* best-effort */ }
        }
    }
}
