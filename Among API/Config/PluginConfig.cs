using System.Text.Json;

namespace AmongApi.Config;

public class PluginConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _configPath;
    private readonly string _manifestPath;

    public string ServerUrl { get; set; } = string.Empty;
    public bool AutoUpdate { get; set; } = true;
    public bool OfflineCache { get; set; } = false;

    public PluginConfig()
    {
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        _configPath = Path.Combine(pluginDir, "config.json");
        _manifestPath = Path.Combine(pluginDir, "Data", "manifest.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded != null)
                {
                    ServerUrl = loaded.ServerUrl;
                    AutoUpdate = loaded.AutoUpdate;
                    OfflineCache = loaded.OfflineCache;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Config] Failed to load config: {ex.Message}. Using defaults.");
        }
    }

    public string GetManifestPath() => _manifestPath;

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Config] Failed to save config: {ex.Message}");
        }
    }
}
