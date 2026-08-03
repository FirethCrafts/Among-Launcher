namespace AmongApi;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;
        FileLogger.Init();
        FileLogger.Info($"Plugin v{MyPluginInfo.PLUGIN_VERSION} loading...");
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
                FileLogger.Info("No mods in manifest.");
                Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] No mods in manifest.");
                return;
            }

            FileLogger.Info($"Found {manifest.Mods.Count} mods in manifest.");
            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Found {manifest.Mods.Count} mods. Connecting to launcher...");

            using var pipe = new PipeClient(Log);
            var connected = await pipe.ConnectAsync();

            if (!connected)
            {
                FileLogger.Warn("Launcher not running. Mods will not be installed.");
                Log.LogWarning($"[{MyPluginInfo.PLUGIN_NAME}] Launcher not running. Mods will not be installed.");
                return;
            }

            FileLogger.Info("Connected to launcher.");

            foreach (var mod in manifest.Mods)
            {
                try
                {
                    FileLogger.Info($"Requesting install: {mod.FileName} v{mod.Version}");
                    Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Requesting install: {mod.FileName} v{mod.Version}");

                    await pipe.SendMessageAsync("install_mod", new
                    {
                        modId = mod.Id,
                        downloadUrl = mod.Url,
                        fileName = mod.FileName
                    });

                    FileLogger.Info($"Install request sent for {mod.FileName}.");
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"Failed to request install for {mod.FileName}: {ex.Message}");
                    Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Failed to request install for {mod.FileName}: {ex.Message}");
                }
            }

            FileLogger.Info("All mods queued. Requesting restart...");
            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] All mods queued. Requesting restart...");
            await pipe.SendMessageAsync("restart_after_install");
            FileLogger.Info("Restart request sent.");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Error: {ex.Message}");
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Error: {ex.Message}");
        }
    }

    private static ModManifest? LoadEmbeddedManifest()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("manifest.json");
        if (stream == null)
        {
            FileLogger.Error("Embedded manifest not found!");
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Embedded manifest not found!");
            return null;
        }

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        FileLogger.Info("Manifest loaded.");
        return JsonSerializer.Deserialize<ModManifest>(json);
    }
}
