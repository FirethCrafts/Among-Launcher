using System.Collections.Concurrent;
using System.Reflection.Emit;
using Il2CppInterop.Runtime.Injection;

namespace AmongApi.Services;

/// <summary>
/// Runs actions on the Unity main thread.
///
/// <para>
/// BepInEx's IL2CPP <see cref="Plugin.Load"/> runs before Unity installs its
/// <see cref="SynchronizationContext"/>, so <c>SynchronizationContext.Current</c>
/// is null there and on every thread-pool continuation. There is therefore no
/// framework trampoline onto the main thread, and the previous implementation
/// silently fell back to <c>Task.Run</c> — which executed IL2CPP calls off the
/// Unity main thread and access-violated inside the game.
/// </para>
/// <para>
/// This class instead installs a real Unity main-thread pump: a <c>MonoBehaviour</c>
/// whose <c>Update()</c> drains a concurrent queue. Actions posted from any thread
/// are executed by that <c>Update()</c> on the main thread; nothing is ever run on
/// a thread-pool thread.
/// </para>
/// <para>
/// The pump component is emitted at runtime because the game's <c>UnityEngine</c>
/// interop assemblies are generated on the player machine and are not available as
/// compile-time references. The emitted type derives from the runtime
/// <c>UnityEngine.MonoBehaviour</c> and is registered through BepInEx /
/// Il2CppInterop's class injector (<see cref="ClassInjector.RegisterTypeInIl2Cpp(Type)"/>
/// + <see cref="IL2CPPChainloader.AddUnityComponent(Type)"/>).
/// </para>
/// </summary>
public static class MainThreadDispatcher
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static readonly object InitLock = new();

    private static bool _initialized;
    private static volatile bool _mainThreadKnown;
    private static volatile int _mainThreadId;

    /// <summary>
    /// True only when the current thread is the Unity main thread, as established
    /// by the pump's first <c>Update()</c>. Before the pump has run this is always
    /// false, so actions are queued rather than run off-thread.
    /// </summary>
    public static bool IsMainThread => _mainThreadKnown && Environment.CurrentManagedThreadId == _mainThreadId;

    /// <summary>
    /// Registers the Unity main-thread pump. Must be called once from
    /// <c>Plugin.Load()</c> (the chainloader invokes it on the Unity main thread).
    /// After this returns, every <see cref="Enqueue"/>/<see cref="EnqueueAsync(Action)"/>
    /// call is queued and drained by the pump's <c>Update()</c> on the main thread,
    /// so posted actions never run on a non-main (thread-pool) thread. If pump
    /// registration failed, queued actions stay dormant in the queue rather than
    /// falling back to running off-thread.
    /// </summary>
    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_initialized)
                return;
            _initialized = true;
        }

        try
        {
            var monoBehaviour = ResolveMonoBehaviourType();
            if (monoBehaviour == null)
            {
                FileLogger.Error(
                    "[Dispatcher] UnityEngine.MonoBehaviour was not found; the main-thread pump was NOT installed. " +
                    "Dispatched actions will queue until a pump becomes available.");
                return;
            }

            var pumpType = BuildPumpType(monoBehaviour);
            ClassInjector.RegisterTypeInIl2Cpp(pumpType);
            IL2CPPChainloader.AddUnityComponent(pumpType);
            FileLogger.Info($"[Dispatcher] Main-thread pump registered ({pumpType.FullName}).");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[Dispatcher] Failed to register the main-thread pump: {ex}");
        }
    }

    /// <summary>
    /// Called by the pump's <c>Update()</c> on the Unity main thread. Records the
    /// main-thread id on first call and drains the queue.
    /// </summary>
    /// <remarks>Public so the runtime-emitted pump can call it.</remarks>
    public static void Pump()
    {
        if (!_mainThreadKnown)
        {
            // Publish the id before the flag so a racing reader never sees
            // known=true with an unset id (managed thread ids are never 0).
            _mainThreadId = Environment.CurrentManagedThreadId;
            _mainThreadKnown = true;
            FileLogger.Info($"[Dispatcher] Main thread established as managed thread {_mainThreadId}.");
        }

        while (Queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                FileLogger.Error($"[Dispatcher] Main-thread action failed: {ex}");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the main thread (fire-and-forget). If the
    /// pump has not run yet the action is queued and will run on the first
    /// <c>Update()</c>; it is never run on the calling thread when that thread is
    /// not the main thread.
    /// </summary>
    public static void Enqueue(Action action)
    {
        if (IsMainThread)
        {
            action();
            return;
        }

        Queue.Enqueue(action);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the main thread and completes the returned
    /// task when the pump has executed it. Never blocks the main thread waiting on
    /// a posted action, so it cannot deadlock.
    /// </summary>
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
        Queue.Enqueue(() =>
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
        });
        return tcs.Task;
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the main thread and completes the returned
    /// task with its result or exception when the pump has executed it.
    /// </summary>
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
        Queue.Enqueue(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static Type? ResolveMonoBehaviourType()
    {
        var type = Type.GetType("UnityEngine.MonoBehaviour, UnityEngine.CoreModule", throwOnError: false);
        if (type != null)
            return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                type = assembly.GetType("UnityEngine.MonoBehaviour", throwOnError: false, ignoreCase: false);
            }
            catch
            {
                // Dynamic / reflection-only assemblies can throw; ignore them.
            }

            if (type != null)
                return type;
        }

        return null;
    }

    private static Type BuildPumpType(Type monoBehaviour)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("AmongApi.MainThreadPump"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("AmongApi.MainThreadPump");

        // Derive from the game's real UnityEngine.MonoBehaviour so ClassInjector
        // registers this as a MonoBehaviour and Unity drives its Update().
        var typeBuilder = module.DefineType(
            "AmongApi.Services.MainThreadPump",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            monoBehaviour);

        var pumpMethod = typeof(MainThreadDispatcher).GetMethod(
                             nameof(Pump), BindingFlags.Public | BindingFlags.Static)
                         ?? throw new MissingMethodException(nameof(MainThreadDispatcher), nameof(Pump));

        var update = typeBuilder.DefineMethod(
            "Update",
            MethodAttributes.Private | MethodAttributes.HideBySig,
            typeof(void),
            Type.EmptyTypes);

        var il = update.GetILGenerator();
        il.Emit(OpCodes.Call, pumpMethod);
        il.Emit(OpCodes.Ret);

        return typeBuilder.CreateType()
               ?? throw new InvalidOperationException("Failed to create the main-thread pump type.");
    }
}
