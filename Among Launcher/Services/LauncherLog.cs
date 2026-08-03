namespace AmongLauncher.Services;

/// <summary>Appends lines to %LocalAppData%\AmongLauncher\AmongLauncher_ipc.log.</summary>
public static class LauncherLog
{
    public static void Write(string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmongLauncher");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "AmongLauncher_ipc.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
