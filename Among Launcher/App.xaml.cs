namespace AmongLauncher;

public partial class App
{
    public static bool ReduceMotion { get; set; }

    /// <summary>Raised when a deep link arrives (used for single-instance protocol re-dispatch).</summary>
    public static event Action<string?>? DeepLinkReceived;
}
