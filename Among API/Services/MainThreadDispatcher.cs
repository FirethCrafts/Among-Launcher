using System.Collections.Concurrent;

namespace AmongApi.Services;

/// <summary>
/// Posts actions to the Unity main thread via SynchronizationContext.
/// </summary>
public static class MainThreadDispatcher
{
    private static SynchronizationContext? _unityContext;
    private static int _mainThreadId;

    public static void CaptureContext()
    {
        _unityContext = SynchronizationContext.Current;
        _mainThreadId = Environment.CurrentManagedThreadId;
        if (_unityContext != null)
            FileLogger.Info($"[Dispatcher] Captured Unity SynchronizationContext on thread {_mainThreadId}");
        else
            FileLogger.Warn($"[Dispatcher] No SynchronizationContext available on thread {_mainThreadId}; using direct fallback");
    }

    public static bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public static void Enqueue(Action action)
    {
        if (IsMainThread)
        {
            action();
            return;
        }

        if (_unityContext != null)
            _unityContext.Post(_ => action(), null);
        else
            Task.Run(action);
    }

    public static Task EnqueueAsync(Action action)
    {
        if (IsMainThread)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

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
        if (IsMainThread)
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

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
