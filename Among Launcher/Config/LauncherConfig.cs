using System.Text.Json;
using AmongLauncher.GameDetection;
using AmongLauncher.Models;

namespace AmongLauncher.Config;

public class LauncherConfig
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmongLauncher");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public Storefront? Storefront { get; set; }
    public string ServerUrl { get; set; } = "https://yourserver.com/api";
    public string ModdedInstallPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmongLauncher", "ModdedAmongUs");
    public string AvatarUrl { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string BackendWssUrl { get; set; } = "wss://yourserver.com/ws";
    public string DiscordAccessToken { get; set; } = string.Empty;
    public List<ModProfile> Profiles { get; set; } = new();
    public List<LibraryEntry> Library { get; set; } = new();

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<LauncherConfig>(json) ?? new LauncherConfig();
            }
        }
        catch
        {
            // Config corrupted, return defaults
        }

        return new LauncherConfig();
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
            {
                Directory.CreateDirectory(ConfigDir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Failed to save config, ignore
        }
    }
}
