using System.IO.Pipes;
using System.Text;

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
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _lock = new();

    public bool IsConnected => _connected;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public PipeClient(ManualLogSource log)
    {
        _log = log;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await _client.ConnectAsync(5000, ct);
            _connected = true;
            _cts = new CancellationTokenSource();
            _listenTask = ListenAsync(_cts.Token);
            Connected?.Invoke(this, EventArgs.Empty);
            _log.LogInfo("[Pipe] Connected to launcher.");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Pipe] Could not connect to launcher: {ex.Message}");
            return false;
        }
    }

    public async Task<JsonElement?> SendMessageAsync(string messageType, object? payload = null, CancellationToken ct = default)
    {
        if (_client == null || !_client.IsConnected)
            return null;

        var id = Guid.NewGuid().ToString("N")[..8];

        var message = new Dictionary<string, object>
        {
            ["type"] = messageType,
            ["id"] = id,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        if (payload != null)
            message["payload"] = payload;

        var tcs = new TaskCompletionSource<JsonElement>();
        lock (_lock)
        {
            _pending[id] = tcs;
        }

        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(data.Length);

        try
        {
            await _client.WriteAsync(header, ct);
            await _client.WriteAsync(data, ct);
            await _client.FlushAsync(ct);
        }
        catch
        {
            lock (_lock) { _pending.Remove(id); }
            return null;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch
        {
            lock (_lock) { _pending.Remove(id); }
            return null;
        }
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

                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetString() ?? "";
                    lock (_lock)
                    {
                        if (_pending.TryGetValue(id, out var tcs))
                        {
                            _pending.Remove(id);
                            tcs.TrySetResult(doc.RootElement);
                            continue;
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
