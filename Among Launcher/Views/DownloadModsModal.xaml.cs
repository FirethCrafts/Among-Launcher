using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using AmongLauncher.Models;
using AmongLauncher.Services;

namespace AmongLauncher.Views;

public partial class DownloadModsModal : UserControl
{
    private readonly HttpClient _httpClient = new();
    private readonly string _pluginsDir;
    private readonly List<ModDownloadItem> _items = new();

    public event EventHandler<bool>? AllComplete;

    public IReadOnlyList<ModDownloadItem> Items => _items;

    public DownloadModsModal(string? moddedPath, IEnumerable<ModDownloadRequest> requests)
    {
        InitializeComponent();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AmongUsLauncher");

        _pluginsDir = Path.Combine(moddedPath ?? "", "BepInEx", "plugins");
        foreach (var req in requests)
            _items.Add(new ModDownloadItem(req.Url, req.FileName));

        DataContext = this;
    }

    public async Task StartAsync()
    {
        var success = true;

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            item.IsActive = true;
            item.Status = "Downloading...";
            StatusText.Text = $"Downloading {item.FileName} ({i + 1}/{_items.Count})...";

            try
            {
                Directory.CreateDirectory(_pluginsDir);
                var destPath = Path.Combine(_pluginsDir, item.FileName);
                await DownloadModAsync(item, destPath);
                item.Status = "Completed ✓";
                item.Progress = 100;
            }
            catch (Exception)
            {
                item.Status = "Failed ✗";
                success = false;
            }
            finally
            {
                item.IsActive = false;
            }
        }

        StatusText.Text = success ? "All mods ready!" : "Some mods failed to download.";
        AllComplete?.Invoke(this, success);
    }

    private async Task DownloadModAsync(ModDownloadItem item, string destPath)
    {
        var response = await _httpClient.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[8192];
        long read = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            read += bytesRead;

            if (total > 0)
            {
                var pct = (int)(read * 100 / total);
                item.Progress = Math.Clamp(pct, 0, 100);
            }
        }
    }
}
