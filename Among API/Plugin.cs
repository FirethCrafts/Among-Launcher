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
                FileLogger.Warn("Launcher not running. Waiting for reconnection...");
                Log.LogWarning($"[{MyPluginInfo.PLUGIN_NAME}] Launcher not running.");
                return;
            }

            FileLogger.Info("Connected to launcher. Sending game_ready...");
            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Connected to launcher.");

            // Tell launcher the game is ready - launcher handles mod installation
            var response = await pipe.SendMessageAsync("game_ready");
            if (response.HasValue)
            {
                var restart = response.Value.TryGetProperty("restart", out var r) && r.GetBoolean();
                FileLogger.Info($"Launcher response: restart={restart}");

                if (restart)
                {
                    FileLogger.Info("New mods installed. Waiting for restart command...");
                    Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] New mods installed. Waiting for restart...");
                    // The launcher will send a "restart" broadcast - just wait
                    await Task.Delay(Timeout.Infinite);
                }
                else
                {
                    FileLogger.Info("All mods installed. Plugin ready.");
                    Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] All mods installed. Plugin ready.");
                    // Keep connection alive
                    await Task.Delay(Timeout.Infinite);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Error: {ex.Message}");
            Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] Error: {ex.Message}");
        }
    }
}
