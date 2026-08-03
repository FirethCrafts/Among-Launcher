using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AmongLauncher.Ipc;

public class PipeServer : IDisposable
{
    private const string PipeName = "AmongLauncher.IPC";
    private const int HeaderSize = 4;

    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _handlers = new();
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private NamedPipeServerStream? _server;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsClientConnected => _server?.IsConnected == true;
    public event EventHandler? ClientConnected;
    public event EventHandler? ClientDisconnected;
    public event EventHandler<string>? RawMessageReceived;

    public PipeServer()
    {
        RegisterHandler("heartbeat", _ => Task.FromResult<object?>(new { type = "heartbeat_ack" }));
        RegisterHandler("mod_status", HandleModStatusAsync);
        RegisterHandler("download_progress", HandleDownloadProgressAsync);
        RegisterHandler("mod_installed", HandleModInstalledAsync);
        RegisterHandler("mod_uninstalled", HandleModUninstalledAsync);
        RegisterHandler("game_ready", HandleGameReadyAsync);
        RegisterHandler("error", HandleErrorAsync);
    }

    public void RegisterHandler(string messageType, Func<JsonElement, Task<object?>> handler)
    {
        lock (_lock)
        {
            _handlers[messageType] = handler;
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenerTask = ListenAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _server?.Dispose();
        }
        catch { }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await _server.WaitForConnectionAsync(ct);
                ClientConnected?.Invoke(this, EventArgs.Empty);

                await HandleConnectionAsync(_server, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                await Task.Delay(1000, ct);
            }
            finally
            {
                ClientDisconnected?.Invoke(this, EventArgs.Empty);
                try { _server?.Dispose(); } catch { }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        while (server.IsConnected && !ct.IsCancellationRequested)
        {
            try
            {
                var message = await ReadMessageAsync(server, ct);
                if (string.IsNullOrEmpty(message)) break;

                RawMessageReceived?.Invoke(this, message);

                // Log received message
                LogDebug($"[Pipe] Received: {message}");

                var doc = JsonDocument.Parse(message);
                if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                    continue;

                var msgType = typeProp.GetString() ?? "";
                var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                LogDebug($"[Pipe] Message type: {msgType}, id: {id}");

                Func<JsonElement, Task<object?>>? handler;
                lock (_lock)
                {
                    _handlers.TryGetValue(msgType, out handler);
                }

                if (handler != null)
                {
                    LogDebug($"[Pipe] Handling: {msgType}");
                    var response = await handler(doc.RootElement);
                    if (response != null)
                    {
                        var responseJson = JsonSerializer.Serialize(response);
                        await SendMessageAsync(server, responseJson, ct);
                        LogDebug($"[Pipe] Sent response: {responseJson}");
                    }
                }
                else
                {
                    LogDebug($"[Pipe] No handler for: {msgType}");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogDebug($"[Pipe] Error: {ex.Message}");
                break;
            }
        }
    }

    private static void LogDebug(string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmongLauncher");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "AmongLauncher_ipc.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    public async Task BroadcastMessageAsync(string messageType, object? payload = null)
    {
        if (_server == null || !_server.IsConnected) return;

        var message = new Dictionary<string, object>
        {
            ["type"] = messageType,
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        if (payload != null)
            message["payload"] = payload;

        var json = JsonSerializer.Serialize(message);
        try
        {
            await SendMessageAsync(_server, json, CancellationToken.None);
        }
        catch { }
    }

    private static async Task SendMessageAsync(NamedPipeServerStream server, string json, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(data.Length);
        await server.WriteAsync(header, ct);
        await server.WriteAsync(data, ct);
        await server.FlushAsync(ct);
    }

    private static async Task<string> ReadMessageAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var headerRead = await server.ReadAsync(header, ct);
        if (headerRead < HeaderSize) return string.Empty;

        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > 1024 * 1024) return string.Empty;

        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = await server.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, totalRead);
    }

    private static Task<object?> HandleModStatusAsync(JsonElement element)
    {
        return Task.FromResult<object?>(new { type = "mod_status_ack", status = "ok" });
    }

    private Task<object?> HandleDownloadProgressAsync(JsonElement element)
    {
        return Task.FromResult<object?>(null);
    }

    private Task<object?> HandleModInstalledAsync(JsonElement element)
    {
        return Task.FromResult<object?>(new { type = "mod_installed_ack", status = "ok" });
    }

    private Task<object?> HandleModUninstalledAsync(JsonElement element)
    {
        return Task.FromResult<object?>(new { type = "mod_uninstalled_ack", status = "ok" });
    }

    private Task<object?> HandleGameReadyAsync(JsonElement element)
    {
        return Task.FromResult<object?>(new { type = "game_ready_ack", status = "ok" });
    }

    private Task<object?> HandleErrorAsync(JsonElement element)
    {
        var message = element.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
        Console.WriteLine($"[AmongAPI Error] {message}");
        return Task.FromResult<object?>(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
