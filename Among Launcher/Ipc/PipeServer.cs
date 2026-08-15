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
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public event EventHandler? ClientConnected;
    public event EventHandler? ClientDisconnected;

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

                LogDebug($"[PipeServer] Listening on pipe '{PipeName}'...");
                await _server.WaitForConnectionAsync(ct);
                LogDebug("[PipeServer] Client connected!");
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

    private static void LogDebug(string message) => Services.LauncherLog.Write(message);

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

    private async Task SendMessageAsync(NamedPipeServerStream server, string json, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var data = Encoding.UTF8.GetBytes(json);
            var header = BitConverter.GetBytes(data.Length);
            await server.WriteAsync(header, ct);
            await server.WriteAsync(data, ct);
            await server.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
