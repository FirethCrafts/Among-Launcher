namespace AmongApi;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;
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

            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Found {manifest.Mods.Count} mods. Connecting to launcher...");

            using var pipe = new PipeClient(Log);
            var connected = await pipe.ConnectAsync();

            if (!connected)
            {
                Log.LogWarning($"[{MyPluginInfo.PLUGIN_NAME}] Launcher not running. Mods will not be installed.");
                return;
            }

            foreach (var mod in manifest.Mods)
            {
                try
                {
                    Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Requesting install: {mod.FileName} v{mod.Version}");

                    await pipe.SendMessageAsync("install_mod", new
                    {
                        modId = mod.Id,
                        downloadUrl = mod.Url,
                        fileName = mod.FileName
                    });
                }
                catch (Exception ex)
                {
                    Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Failed to request install for {mod.FileName}: {ex.Message}");
                }
            }

            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] All mods queued. Requesting restart...");
            await pipe.SendMessageAsync("restart_after_install");
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
}
