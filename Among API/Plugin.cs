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
            FileLogger.Info("Connecting to launcher...");
            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Connecting to launcher...");

            using var pipe = new PipeClient(Log);
            var connected = await pipe.ConnectAsync();

            if (!connected)
            {
                FileLogger.Warn("Launcher not running. Mods will load from BepInEx/plugins/.");
                Log.LogWarning($"[{MyPluginInfo.PLUGIN_NAME}] Launcher not running.");
                return;
            }

            FileLogger.Info("Connected to launcher.");
            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Connected to launcher.");

            // Tell launcher the game is ready
            await pipe.SendMessageAsync("game_ready");
            FileLogger.Info("Game ready signal sent to launcher.");

            // Keep connection alive - launcher may send commands later
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Error: {ex.Message}");
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Error: {ex.Message}");
        }
    }
}
