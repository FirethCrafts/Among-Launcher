namespace AmongApi.Services;

public static class FileLogger
{
    private static readonly object _lock = new();
    private static string _logPath = null!;

    public static void Init()
    {
        var bepInExRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        // BepInEx/plugins/AmongApi.dll -> go up to BepInEx/
        bepInExRoot = Path.GetDirectoryName(bepInExRoot)!;
        _logPath = Path.Combine(bepInExRoot, "AmongApi.log");

        lock (_lock)
        {
            File.WriteAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AmongApi log started\n");
        }
    }

    public static void Log(string level, string message)
    {
        if (string.IsNullOrEmpty(_logPath)) return;

        lock (_lock)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}\n";
                File.AppendAllText(_logPath, line);
            }
            catch { }
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
}
