using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AmongApi.Services;

public class PipeClient : IDisposable
{
    private const string PipeName = "AmongLauncher.IPC";
    private const int HeaderSize = 4;

    private readonly ManualLogSource _log;
    private NamedPipeClientStream? _client;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _connected;
    private bool _disposed;
    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _handlers = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public event EventHandler? Disconnected;

    public PipeClient(ManualLogSource log)
    {
        _log = log;
    }

    public void RegisterHandler(string messageType, Func<JsonElement, Task<object?>> handler)
    {
        lock (_lock)
        {
            _handlers[messageType] = handler;
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                _log.LogInfo($"[Pipe] Connection attempt {attempt}/5...");

                _client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await _client.ConnectAsync(10000, ct);
                _connected = true;
                _cts = new CancellationTokenSource();
                _listenTask = ListenAsync(_cts.Token);
                _log.LogInfo("[Pipe] Connected to launcher!");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Pipe] Attempt {attempt} failed: {ex.Message}");
                if (attempt < 5)
                    await Task.Delay(2000, ct);
            }
        }

        _log.LogWarning("[Pipe] Could not connect to launcher after 5 attempts.");
        return false;
    }

    public async Task SendMessageAsync(string messageType, object? payload = null, CancellationToken ct = default)
    {
        if (_client == null || !_client.IsConnected)
            return;

        var message = new Dictionary<string, object>
        {
            ["type"] = messageType,
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        if (payload != null)
            message["payload"] = payload;

        await WriteFrameAsync(JsonSerializer.Serialize(message), ct);
    }

    private async Task WriteFrameAsync(string json, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var data = Encoding.UTF8.GetBytes(json);
            var header = BitConverter.GetBytes(data.Length);
            await _client!.WriteAsync(header, ct);
            await _client.WriteAsync(data, ct);
            await _client.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SendRawAsync(string json)
    {
        await WriteFrameAsync(json, CancellationToken.None);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client != null && _client.IsConnected)
        {
            try
            {
                var header = new byte[HeaderSize];
                var headerRead = await _client.ReadAsync(header, ct);
                if (headerRead < HeaderSize) break;

                var length = BitConverter.ToInt32(header);
                if (length <= 0 || length > 1024 * 1024) break;

                var buffer = new byte[length];
                var totalRead = 0;
                while (totalRead < length)
                {
                    var read = await _client.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
                    if (read == 0) break;
                    totalRead += read;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, totalRead);
                var doc = JsonDocument.Parse(message);

                // Handle broadcasts from launcher and dispatch handlers
                if (doc.RootElement.TryGetProperty("type", out var typeProp))
                {
                    var msgType = typeProp.GetString() ?? "";
                    if (msgType == "restart")
                    {
                        _log.LogInfo("[Pipe] Received restart command from launcher.");
                        // The game will be killed by the launcher, so we just exit
                        break;
                    }

                    if (_handlers.TryGetValue(msgType, out var handler))
                    {
                        try
                        {
                            var result = await handler(doc.RootElement);
                            if (result != null)
                            {
                                var respId = doc.RootElement.TryGetProperty("id", out var idP) ? idP.GetString() : "";
                                var resp = new Dictionary<string, object>
                                {
                                    ["type"] = msgType + "_ack",
                                    ["id"] = respId ?? "",
                                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };
                                if (result is not string) resp["payload"] = result;
                                await SendRawAsync(JsonSerializer.Serialize(resp));
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError($"[Pipe] Handler {msgType} failed: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (EndOfStreamException) { break; }
            catch { break; }
        }

        _connected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
        _log.LogInfo("[Pipe] Disconnected from launcher.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        _cts?.Cancel();
        try { _client?.Dispose(); } catch { }
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
