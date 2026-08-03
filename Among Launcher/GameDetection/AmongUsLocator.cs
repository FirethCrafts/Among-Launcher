namespace AmongLauncher.GameDetection;

public class AmongUsLocator
{
    public string? FindAmongUs()
    {
        return GameFinder.FindAmongUs();
    }

    public (string? Path, Storefront? Storefront) FindAmongUsWithStorefront()
    {
        return GameFinder.FindAmongUsWithStorefront();
    }
}
