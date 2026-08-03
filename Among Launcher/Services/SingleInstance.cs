using System.IO.Pipes;
using System.Text;

namespace AmongLauncher.Services;

public static class SingleInstance
{
    private const string MutexName = @"Global\AmongLauncher.SingleInstance";
    private const string PipeName = "AmongLauncher.Redirect";

    public static bool TryBecomePrimary(out Mutex? mutex)
    {
        mutex = new Mutex(true, MutexName, out var createdNew);
        if (createdNew) return true;
        mutex.Dispose();
        mutex = null;
        return false;
    }

    public static void StartRedirectServer(Action<string> onDeepLink)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var link = await reader.ReadLineAsync();
                    if (!string.IsNullOrEmpty(link))
                        onDeepLink(link);
                }
                catch { await Task.Delay(1000); }
            }
        });
    }

    public static void ForwardDeepLink(string deepLink)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(deepLink);
        }
        catch { }
    }
}
