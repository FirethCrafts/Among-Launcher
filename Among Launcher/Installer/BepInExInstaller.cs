using AmongLauncher.GameDetection;
using System.IO.Compression;

namespace AmongLauncher.Installer;

public class BepInExInstaller
{
    private static readonly string BepInExSourceDir = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "BepInEx"));

    private static readonly string BepInExMsEpicSourceDir = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "bepinex-ms-epic"));

    private static readonly string ReleaseAssetBaseUrl =
        "https://github.com/FirethCrafts/Among-Launcher/releases/latest/download/";

    public async Task InstallAsync(string gamePath, Storefront? storefront, IProgress<int>? progress = null)
    {
        // MS Store / Epic use the shared "bepinex-ms-epic" build; Steam uses the default one.
        var sourceDir = storefront is Storefront.Epic or Storefront.MicrosoftStore
            ? BepInExMsEpicSourceDir
            : BepInExSourceDir;

        var assetName = storefront is Storefront.Epic or Storefront.MicrosoftStore
            ? "bepinex-ms-epic.zip"
            : "BepInEx.zip";

        if (Directory.Exists(sourceDir))
        {
            CopyFromDirectory(sourceDir, gamePath, progress);
        }
        else
        {
            await DownloadAndExtractAsync(assetName, gamePath, progress);
        }
    }

    private static void CopyFromDirectory(string sourceDir, string gamePath, IProgress<int>? progress)
    {
        progress?.Report(0);

        var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
        var totalFiles = files.Length;
        var copiedFiles = 0;

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(gamePath, relativePath);
            var destDir = Path.GetDirectoryName(destPath);

            if (destDir != null && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(file, destPath, true);
            copiedFiles++;

            if (totalFiles > 0)
            {
                var percent = (int)((double)copiedFiles / totalFiles * 100);
                progress?.Report(percent);
            }
        }

        progress?.Report(100);
    }

    private static async Task DownloadAndExtractAsync(string assetName, string gamePath, IProgress<int>? progress)
    {
        progress?.Report(0);

        var url = ReleaseAssetBaseUrl + assetName;
        var downloadDir = Path.Combine(Path.GetTempPath(), "AmongLauncher", "Downloads");
        Directory.CreateDirectory(downloadDir);
        var zipPath = Path.Combine(downloadDir, assetName);

        using (var httpClient = new HttpClient())
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var dest = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(dest);
        }

        ZipFile.ExtractToDirectory(zipPath, gamePath, overwriteFiles: true);

        progress?.Report(100);
    }
}
