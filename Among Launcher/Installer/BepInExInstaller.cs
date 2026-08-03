using System.IO.Compression;

namespace AmongLauncher.Installer;

public class BepInExInstaller
{
    private const string BepInExDownloadUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip";

    public async Task InstallAsync(string gamePath, IProgress<int>? progress = null)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), "BepInEx.zip");

        try
        {
            // Download BepInEx
            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromMinutes(5);

                var response = await httpClient.GetAsync(BepInExDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead);

                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var canReportProgress = totalBytes != -1;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[8192];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        totalRead += bytesRead;

                        if (canReportProgress)
                        {
                            var percent = (int)((double)totalRead / totalBytes * 100);
                            progress?.Report(percent);
                        }
                    }
                }
            }

            progress?.Report(100);

            // Extract each entry individually for reliability
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(tempZip);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue; // Skip directories

                    var destPath = Path.Combine(gamePath, entry.FullName);
                    var destDir = Path.GetDirectoryName(destPath);

                    if (destDir != null && !Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    if (File.Exists(destPath))
                        File.Delete(destPath);

                    entry.ExtractToFile(destPath);
                }
            });
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }
}
