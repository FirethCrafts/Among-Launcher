using System.Text.Json;
using AmongLauncher.Config;
using AmongLauncher.Services.Lobby;
using Xunit;

namespace AmongLauncher.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AmongLauncherConfigTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new LauncherConfig();

        Assert.Equal("ws://127.0.0.1:8080", config.BotWsEndpoint);
        Assert.Equal("https://yourserver.com/api", config.ServerUrl);
        Assert.Equal("wss://yourserver.com/ws", config.BackendWssUrl);
        Assert.Equal(string.Empty, config.DiscordAccessToken);
        Assert.False(config.DebugMode);
        Assert.False(config.AutoPostLobby);
        Assert.Null(config.WindowLeft);
        Assert.Null(config.WindowTop);
        Assert.Null(config.WindowWidth);
        Assert.Null(config.WindowHeight);
        Assert.False(config.IsMaximized);
    }

    [Fact]
    public void LoadFromFile_ParsesJson()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = new LauncherConfig
        {
            ServerUrl = "https://custom-server.com/api",
            DiscordAccessToken = "test_token",
            DebugMode = true,
            AutoPostLobby = true
        };

        File.WriteAllText(configPath, JsonSerializer.Serialize(config));

        var loaded = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(configPath));

        Assert.NotNull(loaded);
        Assert.Equal("https://custom-server.com/api", loaded!.ServerUrl);
        Assert.Equal("test_token", loaded.DiscordAccessToken);
        Assert.True(loaded.DebugMode);
        Assert.True(loaded.AutoPostLobby);
    }

    [Fact]
    public void SaveToJson_ProducesValidJson()
    {
        var config = new LauncherConfig
        {
            ServerUrl = "https://save-test.com/api",
            BotWsEndpoint = "ws://save-test:9090"
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonSerializer.Deserialize<LauncherConfig>(json);

        Assert.NotNull(parsed);
        Assert.Equal("https://save-test.com/api", parsed!.ServerUrl);
        Assert.Equal("ws://save-test:9090", parsed.BotWsEndpoint);
    }

    [Fact]
    public void HandleCorruptedConfig_ThrowsJsonException()
    {
        var json = "this is not valid json {{{";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LauncherConfig>(json));
    }

    [Fact]
    public void HandleEmptyConfig_ReturnsDefaults()
    {
        var config = new LauncherConfig();

        Assert.Equal("https://yourserver.com/api", config.ServerUrl);
        Assert.Empty(config.Profiles);
        Assert.Empty(config.Library);
    }

    [Fact]
    public void ConfigWithProfiles_PersistsCorrectly()
    {
        var config = new LauncherConfig();
        config.Profiles.Add(new AmongLauncher.Models.ModProfile
        {
            Name = "My Profile",
            Mods = new List<AmongLauncher.Models.ModSetEntry>
            {
                new() { FileName = "mod1.dll", Version = "1.0.0" },
                new() { FileName = "mod2.dll", Version = "2.0.0" }
            }
        });

        var json = JsonSerializer.Serialize(config);
        var loaded = JsonSerializer.Deserialize<LauncherConfig>(json);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Profiles);
        Assert.Equal("My Profile", loaded.Profiles[0].Name);
        Assert.Equal(2, loaded.Profiles[0].Mods.Count);
        Assert.Equal("mod1.dll", loaded.Profiles[0].Mods[0].FileName);
    }

    [Fact]
    public void ConfigWithWindowPosition_PersistsCorrectly()
    {
        var config = new LauncherConfig
        {
            WindowLeft = 100.5,
            WindowTop = 200.5,
            WindowWidth = 800.0,
            WindowHeight = 600.0,
            IsMaximized = true
        };

        var json = JsonSerializer.Serialize(config);
        var loaded = JsonSerializer.Deserialize<LauncherConfig>(json);

        Assert.NotNull(loaded);
        Assert.Equal(100.5, loaded!.WindowLeft);
        Assert.Equal(200.5, loaded.WindowTop);
        Assert.Equal(800.0, loaded.WindowWidth);
        Assert.Equal(600.0, loaded.WindowHeight);
        Assert.True(loaded.IsMaximized);
    }

    [Fact]
    public void HandlePartialJson_ReturnsPartialConfig()
    {
        var json = "{\"ServerUrl\":\"https://partial.com/api\"}";
        var loaded = JsonSerializer.Deserialize<LauncherConfig>(json);

        Assert.NotNull(loaded);
        Assert.Equal("https://partial.com/api", loaded!.ServerUrl);
        Assert.Equal("ws://127.0.0.1:8080", loaded.BotWsEndpoint);
    }

    [Fact]
    public void ConfigWithLibrary_PersistsCorrectly()
    {
        var config = new LauncherConfig();
        config.Library.Add(new AmongLauncher.Models.LibraryEntry
        {
            FileName = "library_mod.dll",
            DownloadUrl = "https://example.com/library_mod.dll"
        });

        var json = JsonSerializer.Serialize(config);
        var loaded = JsonSerializer.Deserialize<LauncherConfig>(json);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Library);
        Assert.Equal("library_mod.dll", loaded.Library[0].FileName);
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_ForDefaultServerUrl()
    {
        var config = new LauncherConfig();
        Assert.False(LobbyBackendClient.IsConfigured(config));
    }

    [Fact]
    public void IsConfigured_ReturnsTrue_ForCustomServerUrl()
    {
        var config = new LauncherConfig { ServerUrl = "https://among-us2.mel-homes.com/api" };
        Assert.True(LobbyBackendClient.IsConfigured(config));
    }
}
