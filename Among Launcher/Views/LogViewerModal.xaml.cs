using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class LogViewerModal : UserControl
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmongLauncher", "AmongLauncher_ipc.log");

    public LogViewerModal()
    {
        InitializeComponent();
        LoadLogs();
    }

    public void LoadLogs()
    {
        try
        {
            if (File.Exists(LogPath))
            {
                var content = File.ReadAllText(LogPath);
                LogContent.Text = string.IsNullOrEmpty(content) ? "No logs yet..." : content;
            }
            else
            {
                LogContent.Text = "Log file not found.\nLogs will appear here after IPC communication starts.";
            }

            LogScrollViewer.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogContent.Text = $"Error reading logs: {ex.Message}";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadLogs();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(LogPath))
                File.WriteAllText(LogPath, string.Empty);
            LogContent.Text = "Logs cleared.";
        }
        catch
        {
            LogContent.Text = "Failed to clear logs.";
        }
    }
}
