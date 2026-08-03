using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AmongLauncher.Ipc;

public class PipeClient : IDisposable
{
    private const string PipeName = "AmongLauncher.IPC";
    private const int HeaderSize = 4;

    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _handlers = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private NamedPipeClientStream? _client;
    private NamedPipeServerStream? _server;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _connected;

    public bool IsConnected => _connected;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? RawMessageReceived;

    public PipeClient()
    {
        RegisterHandler("heartbeat_ack", _ => Task.FromResult<object?>(null));
        RegisterHandler("install_mod", HandleInstallModAsync);
        RegisterHandler("uninstall_mod", HandleUninstallModAsync);
        RegisterHandler("get_mod_status", HandleGetModStatusAsync);
        RegisterHandler("restart_game", HandleRestartGameAsync);
        RegisterHandler("mod_status_ack", _ => Task.FromResult<object?>(null));
        RegisterHandler("mod_installed_ack", _ => Task.FromResult<object?>(null));
        RegisterHandler("mod_uninstalled_ack", _ => Task.FromResult<object?>(null));
        RegisterHandler("game_ready_ack", _ => Task.FromResult<object?>(null));
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
        try
        {
            // Create a server that the EXE will connect to
            _server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            // Also try connecting as client to the EXE's server
            _client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await _client.ConnectAsync(3000, ct);
                _connected = true;
                Connected?.Invoke(this, EventArgs.Empty);
                _cts = new CancellationTokenSource();
                _listenTask = ListenAsync(_client, _cts.Token);
                return true;
            }
            catch
            {
                // If we can't connect as client, start as server
                _client?.Dispose();
                _client = null;

                _cts = new CancellationTokenSource();
                _ = WaitForServerConnectionAsync(_server, _cts.Token);
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForServerConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            await server.WaitForConnectionAsync(ct);
            _connected = true;
            Connected?.Invoke(this, EventArgs.Empty);
            _listenTask = ListenAsync(server, ct);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task ListenAsync(PipeStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && stream.IsConnected)
        {
            try
            {
                var message = await ReadMessageAsync(stream, ct);
                if (string.IsNullOrEmpty(message)) break;

                RawMessageReceived?.Invoke(this, message);

                var doc = JsonDocument.Parse(message);
                if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                    continue;

                var msgType = typeProp.GetString() ?? "";

                Func<JsonElement, Task<object?>>? handler;
                lock (_lock)
                {
                    _handlers.TryGetValue(msgType, out handler);
                }

                if (handler != null)
                {
                    var response = await handler(doc.RootElement);
                    if (response != null)
                    {
                        var responseJson = JsonSerializer.Serialize(response);
                        await SendMessageAsync(stream, responseJson, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (EndOfStreamException) { break; }
            catch { break; }
        }

        _connected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task SendMessageAsync(string messageType, object? payload = null)
    {
        PipeStream? stream = null;
        if (_client != null && _client.IsConnected)
            stream = _client;
        else if (_server != null && _server.IsConnected)
            stream = _server;

        if (stream == null || !stream.IsConnected) return;

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
            await SendMessageAsync(stream, json, CancellationToken.None);
        }
        catch { }
    }

    private static async Task SendMessageAsync(PipeStream stream, string json, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> ReadMessageAsync(PipeStream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var headerRead = await stream.ReadAsync(header, ct);
        if (headerRead < HeaderSize) return string.Empty;

        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > 1024 * 1024) return string.Empty;

        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, totalRead);
    }

    public void Disconnect()
    {
        _connected = false;
        _cts?.Cancel();
        try { _client?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }
    }

    private Task<object?> HandleInstallModAsync(JsonElement element)
    {
        return Task.FromResult<object?>(null);
    }

    private Task<object?> HandleUninstallModAsync(JsonElement element)
    {
        return Task.FromResult<object?>(null);
    }

    private Task<object?> HandleGetModStatusAsync(JsonElement element)
    {
        return Task.FromResult<object?>(new { type = "mod_status", status = "ok", mods = Array.Empty<object>() });
    }

    private Task<object?> HandleRestartGameAsync(JsonElement element)
    {
        return Task.FromResult<object?>(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
