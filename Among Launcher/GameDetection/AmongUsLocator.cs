namespace AmongLauncher.GameDetection;

public class AmongUsLocator
{
    public GameSearchResult FindAmongUsWithStorefront()
    {
        return GameFinder.FindAmongUsWithStorefront();
    }

    public GameSearchResult FindAmongUsForStorefront(Storefront? storefront)
    {
        return GameFinder.FindAmongUsForStorefront(storefront);
    }
}
