using System.Diagnostics;

namespace AmongLauncher.Game;

public class GameProcessManager
{
    private Process? _gameProcess;

    public event EventHandler? GameExited;

    public bool LaunchGame(string exePath, string? arguments = null)
    {
        if (!File.Exists(exePath))
        {
            Services.LauncherLog.Write($"[GameProcessManager] Launch failed: '{exePath}' not found.");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath),
            UseShellExecute = true
        };

        if (!string.IsNullOrEmpty(arguments))
            startInfo.Arguments = arguments;

        try
        {
            _gameProcess = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Services.LauncherLog.Write($"[GameProcessManager] Failed to launch '{exePath}': {ex}");
            return false;
        }

        if (_gameProcess == null)
        {
            Services.LauncherLog.Write($"[GameProcessManager] Process.Start returned null for '{exePath}'.");
            return false;
        }

        _gameProcess.EnableRaisingEvents = true;
        _gameProcess.Exited += OnGameExited;
        return true;
    }

    public void KillGame()
    {
        if (_gameProcess == null || _gameProcess.HasExited)
            return;

        try
        {
            if (_gameProcess.CloseMainWindow())
                _gameProcess.WaitForExit(15000);
        }
        catch { }

        if (!_gameProcess.HasExited)
        {
            try
            {
                _gameProcess.Kill();
                _gameProcess.WaitForExit(15000);
            }
            catch { }
        }
    }

    public bool IsGameRunning()
    {
        return _gameProcess != null && !_gameProcess.HasExited;
    }

    private void OnGameExited(object? sender, EventArgs e)
    {
        GameExited?.Invoke(this, EventArgs.Empty);
    }
}
