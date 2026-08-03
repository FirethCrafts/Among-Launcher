namespace AmongLauncher.GameDetection;

public record GameSearchResult
{
    public string? Path { get; init; }
    public Storefront? Storefront { get; init; }
    public bool DetectedButUnavailable { get; init; }
}
