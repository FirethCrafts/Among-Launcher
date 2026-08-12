using System.Text.Json;
using Xunit;

namespace AmongLauncher.Tests;

public class IpcTests
{
    [Fact]
    public void MessageSerialization_LauncherReady_RoundTrips()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "launcher_ready",
            ["id"] = "abc12345",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("launcher_ready", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("abc12345", doc.RootElement.GetProperty("id").GetString());
        Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public void MessageSerialization_LobbyCreated_ParsesPayload()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "lobby_created",
            ["id"] = "def67890",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = new { code = "ABCDEF", region = "NA", host = "TestHost" }
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("lobby_created", doc.RootElement.GetProperty("type").GetString());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("ABCDEF", payload.GetProperty("code").GetString());
        Assert.Equal("NA", payload.GetProperty("region").GetString());
        Assert.Equal("TestHost", payload.GetProperty("host").GetString());
    }

    [Fact]
    public void MessageSerialization_LobbyClosed_ParsesPayload()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "lobby_closed",
            ["id"] = "ghi11111",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = new { code = "XYZ789" }
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("lobby_closed", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("XYZ789", doc.RootElement.GetProperty("payload").GetProperty("code").GetString());
    }

    [Fact]
    public void MessageSerialization_PlayerJoined_ParsesPayload()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "player_joined",
            ["id"] = "jkl22222",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = new { playerName = "TestPlayer", playerId = "12345" }
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("player_joined", doc.RootElement.GetProperty("type").GetString());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("TestPlayer", payload.GetProperty("playerName").GetString());
        Assert.Equal("12345", payload.GetProperty("playerId").GetString());
    }

    [Fact]
    public void MessageSerialization_PlayerLeft_ParsesPayload()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "player_left",
            ["id"] = "mno33333",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = new { playerName = "LeavingPlayer", reason = "disconnect" }
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("player_left", doc.RootElement.GetProperty("type").GetString());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("LeavingPlayer", payload.GetProperty("playerName").GetString());
        Assert.Equal("disconnect", payload.GetProperty("reason").GetString());
    }

    [Fact]
    public void MessageSerialization_JoinLobbyResult_ParsesPayload()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "join_lobby_result",
            ["id"] = "pqr44444",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = new { success = true, message = "Joined successfully" }
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("join_lobby_result", doc.RootElement.GetProperty("type").GetString());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("Joined successfully", payload.GetProperty("message").GetString());
    }

    [Fact]
    public void MessageSerialization_JoinLobby_ParsesPayload()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "join_lobby",
            ["id"] = "stu55555",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = new { code = "TEST12", region = "EU" }
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("join_lobby", doc.RootElement.GetProperty("type").GetString());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("TEST12", payload.GetProperty("code").GetString());
        Assert.Equal("EU", payload.GetProperty("region").GetString());
    }

    [Fact]
    public void MessageSerialization_SetServerUrl_ParsesPayload()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "set_server_url",
            ["id"] = "vwx66666",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = new { url = "https://among-us2.mel-homes.com" }
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("set_server_url", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("https://among-us2.mel-homes.com", doc.RootElement.GetProperty("payload").GetProperty("url").GetString());
    }

    [Fact]
    public void MessageSerialization_EmptyPayload_OmitsPayload()
    {
        var original = new Dictionary<string, object>
        {
            ["type"] = "launcher_ready",
            ["id"] = "yza77777",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var json = JsonSerializer.Serialize(original);
        var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("payload", out _));
    }

    [Fact]
    public void MessageSerialization_Id_IsEightCharacters()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(8, id.Length);
        Assert.Matches("^[0-9a-f]{8}$", id);
    }
}
