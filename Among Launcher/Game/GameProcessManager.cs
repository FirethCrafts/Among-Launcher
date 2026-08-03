using System.Diagnostics;

namespace AmongLauncher.Game;

public class GameProcessManager
{
    private Process? _gameProcess;

    public event EventHandler? GameExited;

    public void LaunchGame(string exePath, string? arguments = null)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Among Us.exe not found", exePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath),
            UseShellExecute = true
        };

        if (!string.IsNullOrEmpty(arguments))
            startInfo.Arguments = arguments;

        _gameProcess = Process.Start(startInfo);

        if (_gameProcess != null)
        {
            _gameProcess.EnableRaisingEvents = true;
            _gameProcess.Exited += OnGameExited;
        }
    }

    public void KillGame()
    {
        if (_gameProcess == null || _gameProcess.HasExited)
            return;

        // Try graceful close first
        try
        {
            if (_gameProcess.CloseMainWindow())
            {
                _gameProcess.WaitForExit(3000);
            }
        }
        catch
        {
            // CloseMainWindow may throw if process already exited
        }

        // Force kill if still running
        if (!_gameProcess.HasExited)
        {
            try
            {
                _gameProcess.Kill();
                _gameProcess.WaitForExit(5000);
            }
            catch
            {
                // Process may have exited between checks
            }
        }
    }

    public void RestartGame(string exePath)
    {
        KillGame();
        LaunchGame(exePath);
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
