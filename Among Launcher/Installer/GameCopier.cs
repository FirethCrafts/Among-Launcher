namespace AmongLauncher.Installer;

public class GameCopier
{
    private static readonly string[] SkipExtensions = [".pdb"];

    public async Task CopyGameAsync(string sourcePath, string destinationPath, IProgress<int>? progress = null)
    {
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Source directory not found: {sourcePath}");

        if (Directory.Exists(destinationPath))
        {
            Directory.Delete(destinationPath, true);
        }

        var files = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories);
        var totalFiles = files.Length;
        var copiedFiles = 0;

        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (SkipExtensions.Contains(extension))
                {
                    copiedFiles++;
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourcePath, file);
                var destFile = Path.Combine(destinationPath, relativePath);

                var destDir = Path.GetDirectoryName(destFile);
                if (destDir != null && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(file, destFile, true);
                copiedFiles++;

                if (totalFiles > 0)
                {
                    var percent = (int)((double)copiedFiles / totalFiles * 100);
                    progress?.Report(percent);
                }
            }
        });
    }
}
