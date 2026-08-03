using System.Collections.Concurrent;

namespace AmongApi.Services;

/// <summary>
/// Lazy reflection helper over the game's Assembly-CSharp interop assembly.
/// Resolves types and members at runtime so the plugin compiles without any
/// game-assembly reference. Resolution failures degrade to null + a log entry
/// and never throw.
/// </summary>
public static class GameAssembly
{
    private static readonly object _assemblyLock = new();
    private static readonly ConcurrentDictionary<string, Type?> TypeCache = new();
    private static readonly ConcurrentDictionary<string, MemberInfo?> MemberCache = new();
    private static Assembly? _assembly;

    public static ManualLogSource? Log { get; set; }

    public static Type? Type(string name)
    {
        if (TypeCache.TryGetValue(name, out var cached))
            return cached;

        Type? result = null;
        try
        {
            var asm = GetAssembly();
            if (asm != null)
            {
                result = asm.GetType(name, false, true);
                result ??= asm.GetTypes().FirstOrDefault(t => t.Name == name);
            }
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Type resolution failed for '{name}': {ex.Message}");
        }

        if (result != null)
        {
            TypeCache[name] = result;
            Log?.LogInfo($"[GameAssembly] Resolved type '{result.FullName}'");
        }
        return result;
    }

    public static object? GetStaticProp(Type? type, string name)
    {
        if (type == null) return null;
        try
        {
            var prop = ResolveProperty(type, name, isStatic: true);
            return prop?.GetValue(null);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read static {type.Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static object? GetInstanceProp(object? instance, string name)
    {
        if (instance == null) return null;
        try
        {
            var prop = ResolveProperty(instance.GetType(), name, isStatic: false);
            return prop?.GetValue(instance);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read {instance.GetType().Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static object? CallStaticMethod(Type? type, string name, object?[]? args = null, Type[]? argTypes = null)
    {
        if (type == null) return null;
        try
        {
            var flags = BindingFlags.Public | BindingFlags.Static;
            var key = argTypes != null
                ? $"{type.FullName}::{name}({string.Join(",", argTypes.Select(t => t.Name))})"
                : $"{type.FullName}::{name}()";

            MethodInfo? method;
            if (MemberCache.TryGetValue(key, out var cached) && cached is MethodInfo m)
            {
                method = m;
            }
            else
            {
                method = argTypes != null
                    ? type.GetMethod(name, flags, null, argTypes, null)
                    : type.GetMethod(name, flags);
                if (method != null)
                    MemberCache[key] = method;
            }
            return method?.Invoke(null, args);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Call {type.Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static object? EnumValue(Type? enumType, string name)
    {
        if (enumType == null || !enumType.IsEnum) return null;
        try
        {
            return Enum.Parse(enumType, name);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Enum value '{enumType.Name}.{name}' not found: {ex.Message}");
            return null;
        }
    }

    public static bool EnumEquals(object? value, object? expected)
        => value != null && expected != null && value.Equals(expected);

    public static int ToInt(object? value) => value is null ? 0 : Convert.ToInt32(value);

    public static bool ToBool(object? value) => value is bool b && b;

    public static string ToStr(object? value) => value as string ?? "";

    private static PropertyInfo? ResolveProperty(Type type, string name, bool isStatic)
    {
        var key = $"{type.FullName}::{name}:{(isStatic ? 's' : 'i')}";
        if (MemberCache.TryGetValue(key, out var cached) && cached is PropertyInfo prop)
            return prop;

        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var resolved = type.GetProperty(name, flags);
        if (resolved != null)
            MemberCache[key] = resolved;
        return resolved;
    }

    private static Assembly? GetAssembly()
    {
        if (_assembly != null) return _assembly;
        lock (_assemblyLock)
        {
            if (_assembly != null) return _assembly;
            try
            {
                _assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase));
                if (_assembly != null)
                {
                    Log?.LogInfo("[GameAssembly] Resolved Assembly-CSharp from loaded assemblies.");
                    return _assembly;
                }

                var path = Path.Combine(Environment.CurrentDirectory, "BepInEx", "interop", "Assembly-CSharp.dll");
                if (File.Exists(path))
                {
                    _assembly = Assembly.LoadFrom(path);
                    Log?.LogInfo($"[GameAssembly] Loaded Assembly-CSharp from '{path}'.");
                }
                else
                {
                    Log?.LogWarning("[GameAssembly] Assembly-CSharp not found in loaded assemblies or on disk.");
                }
            }
            catch (Exception ex)
            {
                _assembly = null;
                Log?.LogWarning($"[GameAssembly] Failed to load Assembly-CSharp: {ex.Message}");
            }
        }
        return _assembly;
    }
}
