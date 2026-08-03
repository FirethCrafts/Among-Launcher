namespace AmongLauncher.GameDetection;

public class AmongUsLocator
{
    public string? FindAmongUs()
    {
        return GameFinder.FindAmongUs();
    }

    public GameSearchResult FindAmongUsWithStorefront()
    {
        return GameFinder.FindAmongUsWithStorefront();
    }
}
