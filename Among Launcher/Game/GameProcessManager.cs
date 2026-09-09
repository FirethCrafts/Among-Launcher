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

    public bool KillGame()
    {
        var killedAnything = false;

        if (_gameProcess != null)
        {
            try
            {
                if (!_gameProcess.HasExited)
                {
                    try
                    {
                        if (_gameProcess.CloseMainWindow())
                            _gameProcess.WaitForExit(15000);
                    }
                    catch { }

                    try
                    {
                        if (!_gameProcess.HasExited)
                        {
                            _gameProcess.Kill();
                            _gameProcess.WaitForExit(15000);
                        }
                    }
                    catch { }

                    try
                    {
                        if (_gameProcess.HasExited)
                            killedAnything = true;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Fallback: tracked handle may be null (game launched externally,
        // launcher restarted) or already exited. Kill by process name best-effort.
        try
        {
            foreach (var proc in Process.GetProcessesByName("Among Us"))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        proc.WaitForExit(15000);
                    }
                    killedAnything = true;
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }

        return killedAnything;
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
