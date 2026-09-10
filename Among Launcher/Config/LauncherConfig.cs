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

    private static readonly string BackupPath = Path.Combine(ConfigDir, "config.json.bak");

    public static string DefaultModdedPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmongLauncher", "ModdedAmongUs");

    public const string BackendServerUrl = "https://among-us.mel-homes.com";
    public const string BackendWebSocketUrl = "wss://among-us.mel-homes.com/ws";

    public Storefront? Storefront { get; set; }
    public string ModdedInstallPath { get; set; } = DefaultModdedPath();
    public string AvatarUrl { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DiscordAccessToken { get; set; } = string.Empty;
    public List<ModProfile> Profiles { get; set; } = new();
    public List<LibraryEntry> Library { get; set; } = new();
    public bool DebugMode { get; set; }
    public bool AutoPostLobby { get; set; }
    public string LastSeenVersion { get; set; } = string.Empty;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool IsMaximized { get; set; }

    public static LauncherConfig Load()
    {
        // Primary config first, backup second, defaults last. A kill
        // mid-save (e.g. Velopack update restart) can truncate config.json;
        // the backup keeps a single bad write from wiping everything.
        foreach (var path in new[] { ConfigPath, BackupPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var parsed = JsonSerializer.Deserialize<LauncherConfig>(json);
                        if (parsed != null)
                            return parsed;
                    }
                }
            }
            catch
            {
                // Corrupted/unreadable — try the next source.
            }
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

            // Atomic write: temp file + move, so a kill mid-save can never
            // leave a truncated config.json behind.
            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);

            // Keep one known-good backup for recovery on corrupt load.
            try { File.Copy(ConfigPath, BackupPath, overwrite: true); }
            catch { /* backup is best-effort */ }
        }
        catch
        {
            // Failed to save config, ignore
        }
    }
}
