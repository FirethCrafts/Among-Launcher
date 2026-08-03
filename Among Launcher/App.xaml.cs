using System.Windows;
using AmongLauncher.Services;

namespace AmongLauncher;

public partial class App : Application
{
    public static event Action<string>? DeepLinkReceived;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var deepLink = DeepLinkHandler.FindDeepLinkArgument();

        if (!SingleInstance.TryBecomePrimary(out _))
        {
            if (deepLink != null)
                SingleInstance.ForwardDeepLink(deepLink);
            Shutdown();
            return;
        }

        SingleInstance.StartRedirectServer(link => DeepLinkReceived?.Invoke(link));
    }
}
