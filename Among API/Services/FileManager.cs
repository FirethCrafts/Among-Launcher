namespace AmongApi.Services;

public static class FileManager
{
    public static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256File(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return ComputeSha256(bytes);
    }

    public static bool VerifyHash(byte[] data, string expectedHash)
    {
        if (string.IsNullOrEmpty(expectedHash))
            return true;

        var actual = ComputeSha256(data);
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
