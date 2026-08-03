using System.Windows;
using AmongLauncher.Services;

namespace AmongLauncher;

public partial class App
{
    public static bool ReduceMotion { get; private set; }

    /// <summary>Raised when a deep link arrives (used for single-instance protocol re-dispatch).</summary>
    public static event Action<string?>? DeepLinkReceived;

    protected override void OnStartup(StartupEventArgs e)
    {
        ReduceMotion = !SystemParameters.ClientAreaAnimation;

        var deepLink = DeepLinkHandler.FindDeepLinkArgument();

        // Single-instance: only the primary process hosts the UI and the IPC pipe.
        if (!SingleInstance.TryBecomePrimary(out _))
        {
            if (deepLink != null)
                SingleInstance.ForwardDeepLink(deepLink);
            Shutdown();
            return;
        }

        // As primary, accept deep links forwarded by later instances.
        SingleInstance.StartRedirectServer(link => DeepLinkReceived?.Invoke(link));

        base.OnStartup(e);

        var window = new MainWindow(deepLink);
        MainWindow = window;
        window.Show();
    }
}
