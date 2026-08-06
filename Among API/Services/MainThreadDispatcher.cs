using System.Collections.Concurrent;

namespace AmongApi.Services;

/// <summary>
/// Posts actions to the Unity main thread via SynchronizationContext.
/// Falls back to Task.Run if the context is unavailable.
/// </summary>
public static class MainThreadDispatcher
{
    private static SynchronizationContext? _unityContext;

    public static void CaptureContext()
    {
        _unityContext = SynchronizationContext.Current;
        if (_unityContext != null)
            FileLogger.Info("[Dispatcher] Captured Unity SynchronizationContext");
        else
            FileLogger.Warn("[Dispatcher] No SynchronizationContext available; using Task.Run fallback");
    }

    public static void Enqueue(Action action)
    {
        if (_unityContext != null)
        {
            _unityContext.Post(_ => action(), null);
        }
        else
        {
            Task.Run(action);
        }
    }

    public static Task EnqueueAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Wrapper()
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        if (_unityContext != null)
            _unityContext.Post(_ => Wrapper(), null);
        else
            Task.Run(Wrapper);

        return tcs.Task;
    }

    public static Task<T> EnqueueAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Wrapper()
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        if (_unityContext != null)
            _unityContext.Post(_ => Wrapper(), null);
        else
            Task.Run(Wrapper);

        return tcs.Task;
    }
}
