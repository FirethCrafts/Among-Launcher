namespace AmongApi.Services;

/// <summary>
/// Polls the in-game free-chat input for host chat commands (/repost, /disband).
/// Reads via reflection (GameAssembly): HudManager.Instance.Chat.freeChatField.Text.
/// When a command is detected the input is cleared (best-effort, so the raw text
/// is not sent as a normal chat message) and the matching Action is invoked.
/// All reads are try/catch'd and never crash the poll loop.
/// </summary>
public class ChatCommandHandler : IDisposable
{
    private const int PollIntervalMs = 500;

    private readonly ManualLogSource _log;
    private CancellationTokenSource? _cts;
    private string _lastHandledText = "";

    public Action? OnRepost { get; set; }
    public Action? OnDisband { get; set; }
    public Action? OnPostLobby { get; set; }

    public ChatCommandHandler(ManualLogSource log) => _log = log;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(LoopAsync);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task LoopAsync()
    {
        var cts = _cts;
        if (cts == null)
            return;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[ChatCommandHandler] Tick failed: {ex.Message}");
            }
            try
            {
                await Task.Delay(PollIntervalMs, cts.Token);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Tick()
    {
        var text = ReadChatInput();
        if (text.Length == 0)
        {
            _lastHandledText = "";
            return;
        }

        if (string.Equals(text, _lastHandledText, StringComparison.Ordinal))
            return;

        FileLogger.Info($"[ChatCommandHandler] Chat input: '{text}'");

        var handled = false;
        if (text.StartsWith("/repost", StringComparison.OrdinalIgnoreCase))
        {
            FileLogger.Info("[ChatCommandHandler] /repost detected.");
            _log.LogInfo("[ChatCommandHandler] /repost detected.");
            handled = true;
            OnRepost?.Invoke();
        }
        else if (text.StartsWith("/disband", StringComparison.OrdinalIgnoreCase))
        {
            FileLogger.Info("[ChatCommandHandler] /disband detected.");
            _log.LogInfo("[ChatCommandHandler] /disband detected.");
            handled = true;
            OnDisband?.Invoke();
        }
        else if (text.StartsWith("/postlobby", StringComparison.OrdinalIgnoreCase))
        {
            FileLogger.Info("[ChatCommandHandler] /postlobby detected.");
            _log.LogInfo("[ChatCommandHandler] /postlobby detected.");
            handled = true;
            OnPostLobby?.Invoke();
        }

        if (!handled)
            return;

        if (TryClearInput())
            _lastHandledText = "";
        else
            _lastHandledText = text;
    }

    /// <summary>
    /// Reads the current free-chat input text. Returns "" when not in a lobby /
    /// no chat controller / no free-chat field (e.g. quick-chat mode) is present.
    /// </summary>
    private static string ReadChatInput()
    {
        var field = GetFreeChatField();
        if (field == null)
            return "";
        return GameAssembly.ToStr(GameAssembly.GetInstanceProp(field, "Text"));
    }

    /// <summary>
    /// Best-effort clear of the free-chat input so a command is not sent as a
    /// normal chat message. Returns true when the field was found and cleared.
    /// </summary>
    private static bool TryClearInput()
    {
        try
        {
            var field = GetFreeChatField();
            if (field == null)
                return false;
            if (!GameAssembly.HasInstanceMethod(field, "Clear", 0))
            {
                GameAssembly.Log?.LogWarning("[ChatCommandHandler] FreeChatInputField.Clear not available; input left as-is.");
                return false;
            }
            GameAssembly.CallInstanceMethod(field, "Clear");
            return true;
        }
        catch (Exception ex)
        {
            GameAssembly.Log?.LogWarning($"[ChatCommandHandler] Clearing chat input failed: {ex.Message}");
            return false;
        }
    }

    private static object? GetFreeChatField()
    {
        // HudManager : DestroyableSingleton<HudManager>; the Instance static lives
        // on the closed generic base (same pattern as ServerManager in LobbyJoiner).
        var hudManagerType = GameAssembly.Type("HudManager");
        if (hudManagerType?.BaseType == null)
            return null;
        var hudManager = GameAssembly.GetStaticProp(hudManagerType.BaseType, "Instance");
        if (hudManager == null)
            return null;

        var chat = GameAssembly.GetInstanceProp(hudManager, "Chat");
        if (chat == null)
            return null;

        return GameAssembly.GetInstanceProp(chat, "freeChatField");
    }
}
