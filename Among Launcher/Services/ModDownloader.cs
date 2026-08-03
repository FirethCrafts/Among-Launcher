namespace AmongLauncher.Services;

/// <summary>Shared mod-download logic with file-lock retry, used by MainWindow and MainView.</summary>
public static class ModDownloader
{
    private static readonly int[] RetryDelays = { 250, 500, 1000, 2000, 4000 };

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destPath"/>, skipping when the
    /// file already exists and is non-empty. Retries with backoff on file-lock IOExceptions.
    /// </summary>
    public static async Task DownloadToFileAsync(HttpClient http, string url, string destPath, Action<string>? log = null)
    {
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
        {
            log?.Invoke($"[Launcher] {Path.GetFileName(destPath)} already exists, skipping download");
            return;
        }

        for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
        {
            try
            {
                var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream);
                return;
            }
            catch (IOException) when (attempt < RetryDelays.Length - 1)
            {
                log?.Invoke($"[Launcher] File locked, retry {attempt + 1}/{RetryDelays.Length} for {destPath}");
                await Task.Delay(RetryDelays[attempt]);
            }
        }
    }
}
