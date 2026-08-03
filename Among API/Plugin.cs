using System.Diagnostics;

namespace AmongApi;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;
    private static string _pluginDir = null!;

    public override void Load()
    {
        Log = base.Log;
        _pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Loading...");

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var manifest = LoadEmbeddedManifest();
            if (manifest == null || manifest.Mods.Count == 0)
            {
                Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] No mods in manifest.");
                return;
            }

            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Found {manifest.Mods.Count} mods.");

            var anyWritten = false;

            foreach (var mod in manifest.Mods)
            {
                try
                {
                    var destPath = Path.Combine(_pluginDir, mod.FileName);

                    if (File.Exists(destPath))
                    {
                        var existingHash = ComputeSha256(File.ReadAllBytes(destPath));
                        if (string.Equals(existingHash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] {mod.FileName} up to date.");
                            continue;
                        }
                    }

                    var srcPath = mod.Url;
                    if (srcPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                        srcPath = srcPath["file://".Length..];

                    if (!File.Exists(srcPath))
                    {
                        Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Source not found: {srcPath}");
                        continue;
                    }

                    var bytes = await File.ReadAllBytesAsync(srcPath);
                    var hash = ComputeSha256(bytes);

                    if (!string.Equals(hash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] {mod.FileName} hash mismatch!");
                        continue;
                    }

                    await File.WriteAllBytesAsync(destPath, bytes);
                    Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] {mod.FileName} installed.");
                    anyWritten = true;
                }
                catch (Exception ex)
                {
                    Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] {mod.FileName}: {ex.Message}");
                }
            }

            if (anyWritten)
            {
                Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Restarting...");
                RestartGame();
            }
            else
            {
                Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] All mods installed.");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Error: {ex.Message}");
        }
    }

    private static ModManifest? LoadEmbeddedManifest()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("manifest.json");
        if (stream == null)
        {
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Embedded manifest not found!");
            return null;
        }

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<ModManifest>(json);
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void RestartGame()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true
            });

            Thread.Sleep(1000);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Restart failed: {ex.Message}");
        }
    }
}
