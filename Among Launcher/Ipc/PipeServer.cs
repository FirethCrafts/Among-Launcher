using System.IO.Pipes;
using System.Text;

namespace AmongLauncher.Ipc;

public class PipeServer
{
    private const string PipeName = "AmongLauncher IPC";

    public event EventHandler<string>? MessageReceived;

    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenerTask = ListenAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                var buffer = new byte[4096];
                var bytesRead = await server.ReadAsync(buffer, ct);

                if (bytesRead > 0)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    MessageReceived?.Invoke(this, message);

                    // Send acknowledgement
                    var ack = Encoding.UTF8.GetBytes("{\"type\":\"ack\"}");
                    await server.WriteAsync(ack, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Connection error, retry after delay
                await Task.Delay(1000, ct);
            }
        }
    }

    public async Task SendMessageAsync(string message)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            await client.ConnectAsync(5000);

            var buffer = Encoding.UTF8.GetBytes(message);
            await client.WriteAsync(buffer);
        }
        catch
        {
            // Failed to send, will retry later
        }
    }
}
