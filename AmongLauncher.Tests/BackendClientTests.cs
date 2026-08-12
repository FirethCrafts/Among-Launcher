using System.Net;
using System.Text;
using System.Text.Json;
using AmongLauncher.Config;
using AmongLauncher.Models;
using AmongLauncher.Services.Lobby;
using Xunit;

namespace AmongLauncher.Tests;

public class BackendClientTests
{
    private const string ServerUrl = "https://test-server.example.com/api";

    private static LauncherConfig CreateConfig(string? token = null) =>
        new()
        {
            ServerUrl = ServerUrl,
            DiscordAccessToken = token ?? string.Empty
        };

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestContent { get; private set; }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
                LastRequestContent = await request.Content.ReadAsStringAsync(cancellationToken);
            return await _handler(request, cancellationToken);
        }
    }

    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string responseBody = "")
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            }));

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(ServerUrl + "/")
        };
    }

    private static (HttpClient http, FakeHttpMessageHandler handler) CreateMockHttpClientWithCapture()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ServerUrl + "/")
        };
        return (http, handler);
    }

    [Fact]
    public async Task CreateLobbyAsync_SendsCorrectJson()
    {
        var (http, handler) = CreateMockHttpClientWithCapture();
        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var request = new CreateLobbyRequest("ABCDEF", "NA", "TestHost", "modded", new List<ModInfoEntry>(), 15);
        var result = await client.CreateLobbyAsync(request, CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("api/v1/lobbies", handler.LastRequest.RequestUri!.ToString());

        Assert.NotNull(handler.LastRequestContent);
        Assert.Contains("ABCDEF", handler.LastRequestContent!);
        Assert.Contains("NA", handler.LastRequestContent);
        Assert.Contains("TestHost", handler.LastRequestContent);
        Assert.Contains("mod_type", handler.LastRequestContent);
    }

    [Fact]
    public async Task GetLobbyAsync_ParsesResponseCorrectly()
    {
        var lobbyResponse = new LobbyResponse(
            "ABCDEF", "NA", "HostUser", "modded",
            new List<ModInfoEntry>
            {
                new("TestMod", "1.0.0", "abc123hash", "https://example.com/mod.dll")
            },
            new List<PlayerInfoEntry>
            {
                new("123", "Player1", true, 5, 50)
            },
            15);

        var json = JsonSerializer.Serialize(lobbyResponse);
        using var http = CreateMockHttpClient(HttpStatusCode.OK, json);

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.GetLobbyAsync("ABCDEF", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("ABCDEF", result!.Code);
        Assert.Equal("NA", result.Region);
        Assert.Equal("HostUser", result.Host);
        Assert.Single(result.ModSet);
        Assert.Equal("TestMod", result.ModSet[0].FileName);
        Assert.Equal("1.0.0", result.ModSet[0].Version);
        Assert.Equal("abc123hash", result.ModSet[0].Sha256);
        Assert.Equal(1, result.PlayerCount);
        Assert.Equal(15, result.MaxPlayers);
    }

    [Fact]
    public async Task HeartbeatAsync_CallsCorrectEndpoint()
    {
        var (http, handler) = CreateMockHttpClientWithCapture();
        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.HeartbeatAsync("ABCDEF", "test_user", CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("api/v1/lobbies/ABCDEF/heartbeat", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task UploadModAsync_SendsMultipartFormData()
    {
        var modInfo = new ModInfoEntry("TestMod", "1.0.0", "hash123");
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(modInfo), Encoding.UTF8, "application/json")
            }));

        using var http = new HttpClient(handler) { BaseAddress = new Uri(ServerUrl + "/") };

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var fileContent = Encoding.UTF8.GetBytes("fake mod content");
        using var stream = new MemoryStream(fileContent);

        var result = await client.UploadModAsync(stream, "TestMod.dll", "1.0.0", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("TestMod", result!.Name);
        Assert.Equal("1.0.0", result.Version);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("api/v1/mods", handler.LastRequest.RequestUri!.ToString());
        Assert.IsType<MultipartFormDataContent>(handler.LastRequest.Content);
    }

    [Fact]
    public async Task GetLobbyDetailsAsync_ReturnsDetailedResponse()
    {
        var detailedResponse = new LobbyDetailedResponse(
            "ABCDEF", "NA", "HostUser", "modded", "active",
            new List<ModInfoEntry> { new("Mod1", "1.0", "hash1") },
            new List<PlayerInfoEntry> { new("123", "Player1", true, 5, 50) },
            DateTime.UtcNow,
            15,
            DateTime.UtcNow.AddMinutes(-5),
            "2024.1.1",
            "TheSkeld",
            "English",
            "FreeChat");

        var json = JsonSerializer.Serialize(detailedResponse);
        using var http = CreateMockHttpClient(HttpStatusCode.OK, json);

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.GetLobbyDetailsAsync("ABCDEF", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("ABCDEF", result!.Code);
        Assert.Equal("active", result.Status);
        Assert.Equal("2024.1.1", result.GameVersion);
        Assert.Equal("TheSkeld", result.MapName);
        Assert.Equal("English", result.Language);
        Assert.Equal("FreeChat", result.ChatType);
    }

    [Fact]
    public async Task AuthHeader_IsAdded_WhenTokenExists()
    {
        var lobbyResponse = new LobbyResponse("ABCDEF", "NA", "Host", "vanilla", new List<ModInfoEntry>(), new List<PlayerInfoEntry>(), 15);
        var json = JsonSerializer.Serialize(lobbyResponse);
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));

        using var http = new HttpClient(handler) { BaseAddress = new Uri(ServerUrl + "/") };
        var config = CreateConfig("discord_token_123");
        var client = new LobbyBackendClient(http, config);

        await client.GetLobbyAsync("ABCDEF", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("Authorization"));
        Assert.Equal("Bearer discord_token_123", handler.LastRequest.Headers.GetValues("Authorization").First());
    }

    [Fact]
    public async Task AuthHeader_IsNotAdded_WhenTokenIsEmpty()
    {
        var lobbyResponse = new LobbyResponse("ABCDEF", "NA", "Host", "vanilla", new List<ModInfoEntry>(), new List<PlayerInfoEntry>(), 15);
        var json = JsonSerializer.Serialize(lobbyResponse);
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));

        using var http = new HttpClient(handler) { BaseAddress = new Uri(ServerUrl + "/") };
        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        await client.GetLobbyAsync("ABCDEF", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task TimeoutHandling_ReturnsNull_OnTimeout()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new TaskCanceledException("timeout"));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ServerUrl + "/"),
            Timeout = TimeSpan.FromMilliseconds(100)
        };

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.GetLobbyAsync("ABCDEF", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ErrorResponse_404_ReturnsNull()
    {
        using var http = CreateMockHttpClient(HttpStatusCode.NotFound);

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.GetLobbyAsync("NONEXISTENT", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ErrorResponse_500_ReturnsNull()
    {
        using var http = CreateMockHttpClient(HttpStatusCode.InternalServerError);

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.GetLobbyAsync("ABCDEF", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateLobbyAsync_False_OnServerError()
    {
        using var http = CreateMockHttpClient(HttpStatusCode.InternalServerError);

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var request = new CreateLobbyRequest("ABCDEF", "NA", "Host", "modded", new List<ModInfoEntry>());
        var result = await client.CreateLobbyAsync(request, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task DisbandAsync_CallsDeleteEndpoint()
    {
        var (http, handler) = CreateMockHttpClientWithCapture();
        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.DisbandAsync("ABCDEF", CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains("api/v1/lobbies/ABCDEF", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RepostAsync_CallsPostEndpoint()
    {
        var (http, handler) = CreateMockHttpClientWithCapture();
        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.RepostAsync("ABCDEF", CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("api/v1/lobbies/ABCDEF/repost", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task KickAsync_SendsCorrectPayload()
    {
        var (http, handler) = CreateMockHttpClientWithCapture();
        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.KickAsync("ABCDEF", "target_user_id", CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("api/v1/lobbies/ABCDEF/kick", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetLobbyAsync_EmptyBody_ThrowsJsonException()
    {
        using var http = CreateMockHttpClient(HttpStatusCode.OK, "");

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        await Assert.ThrowsAsync<JsonException>(() => client.GetLobbyAsync("ABCDEF", CancellationToken.None));
    }

    [Fact]
    public async Task GetLobbyAsync_MissingMods_ReturnsEmptyModSet()
    {
        var lobbyResponse = new LobbyResponse(
            "ABCDEF", "NA", "HostUser", "vanilla",
            new List<ModInfoEntry>(),
            new List<PlayerInfoEntry>(),
            15);

        var json = JsonSerializer.Serialize(lobbyResponse);
        using var http = CreateMockHttpClient(HttpStatusCode.OK, json);

        var config = CreateConfig();
        var client = new LobbyBackendClient(http, config);

        var result = await client.GetLobbyAsync("ABCDEF", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.ModSet);
        Assert.Equal(0, result.PlayerCount);
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_ForDefaultUrl()
    {
        var config = new LauncherConfig { ServerUrl = "https://yourserver.com/api" };
        Assert.False(LobbyBackendClient.IsConfigured(config));
    }

    [Fact]
    public void IsConfigured_ReturnsTrue_ForCustomUrl()
    {
        var config = new LauncherConfig { ServerUrl = "https://among-us2.mel-homes.com/api" };
        Assert.True(LobbyBackendClient.IsConfigured(config));
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_ForBlankUrl()
    {
        var config = new LauncherConfig { ServerUrl = "" };
        Assert.False(LobbyBackendClient.IsConfigured(config));
    }
}
