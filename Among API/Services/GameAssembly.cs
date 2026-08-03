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
            result = ResolveType(name);
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

    private static Type? ResolveType(string name)
    {
        // 1. Assembly-CSharp (all game types).
        var acs = GetAssembly();
        if (acs != null)
        {
            var t = acs.GetType(name, false, true) ?? acs.GetTypes().FirstOrDefault(x => x.Name == name);
            if (t != null) return t;
        }

        // 2. Any other already-loaded assembly (covers Il2CppInterop.Runtime, Il2CppSystem.*, UnityEngine.*).
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (ReferenceEquals(asm, acs)) continue;
            try
            {
                var t = asm.GetType(name, false, true) ?? asm.GetTypes().FirstOrDefault(x => x.Name == name);
                if (t != null) return t;
            }
            catch
            {
                // Some interop assemblies cannot enumerate their types; skip.
            }
        }

        // 3. On-disk fallback for the Il2CppInterop runtime (BepInEx core).
        var interopRuntime = LoadAssemblyByName("Il2CppInterop.Runtime");
        if (interopRuntime != null)
        {
            var t = interopRuntime.GetType(name, false, true) ?? interopRuntime.GetTypes().FirstOrDefault(x => x.Name == name);
            if (t != null) return t;
        }
        return null;
    }

    private static Assembly? LoadAssemblyByName(string simpleName)
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null) return loaded;

            foreach (var dir in new[]
                     {
                         Path.Combine(Environment.CurrentDirectory, "BepInEx", "core"),
                         Path.Combine(Environment.CurrentDirectory, "BepInEx", "interop")
                     })
            {
                var path = Path.Combine(dir, simpleName + ".dll");
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Load '{simpleName}' failed: {ex.Message}");
        }
        return null;
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

    /// <summary>
    /// Reads an instance member that may be a property or a field (e.g. HostId).
    /// Resolves silently against the cache; a single failure is logged, never thrown.
    /// </summary>
    public static object? GetInstanceMember(object? instance, string name)
    {
        if (instance == null) return null;
        try
        {
            var type = instance.GetType();
            return ResolveProperty(type, name, isStatic: false)?.GetValue(instance)
                ?? ResolveField(type, name, isStatic: false)?.GetValue(instance);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read member {instance.GetType().Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads a static member that may be a property or a field (e.g. CurrentClient).
    /// Resolves silently against the cache; a single failure is logged, never thrown.
    /// </summary>
    public static object? GetStaticMember(Type? type, string name)
    {
        if (type == null) return null;
        try
        {
            return ResolveProperty(type, name, isStatic: true)?.GetValue(null)
                ?? ResolveField(type, name, isStatic: true)?.GetValue(null);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read static member {type.Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static object? GetStaticField(Type? type, string name)
    {
        if (type == null) return null;
        try
        {
            var field = ResolveField(type, name, isStatic: true);
            return field?.GetValue(null);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read static field {type.Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static object? GetInstanceField(object? instance, string name)
    {
        if (instance == null) return null;
        try
        {
            var field = ResolveField(instance.GetType(), name, isStatic: false);
            return field?.GetValue(instance);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read {instance.GetType().Name}.{name} field failed: {ex.Message}");
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

    public static object? CallInstanceMethod(object? instance, string name, object?[]? args = null, Type[]? argTypes = null)
    {
        if (instance == null) return null;
        try
        {
            var type = instance.GetType();

            if (argTypes != null)
            {
                var key = $"{type.FullName}::{name}({string.Join(",", argTypes.Select(t => t.Name))})i";
                MethodInfo? method;
                if (MemberCache.TryGetValue(key, out var cached) && cached is MethodInfo m)
                {
                    method = m;
                }
                else
                {
                    method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, argTypes, null);
                    if (method != null)
                        MemberCache[key] = method;
                }
                return method?.Invoke(instance, args);
            }

            // Best-match fallback: resolve by name + parameter count + assignability.
            // Needed when the exact parameter type (e.g. Il2CppSystem.Collections.IEnumerator)
            // cannot be expressed with compile-time types.
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name && m.GetParameters().Length == (args?.Length ?? 0))
                .Where(m => ArgsMatch(m, args))
                .ToList();
            if (candidates.Count == 0)
            {
                Log?.LogWarning($"[GameAssembly] No matching instance method {type.Name}.{name} for {args?.Length ?? 0} arg(s).");
                return null;
            }
            if (candidates.Count > 1)
                Log?.LogWarning($"[GameAssembly] {type.Name}.{name} is ambiguous ({candidates.Count} matches); using first.");
            return candidates[0].Invoke(instance, args);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Call {instance.GetType().Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static bool HasInstanceMethod(object? instance, string name, int argCount)
    {
        if (instance == null) return false;
        try
        {
            return instance.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.Name == name && m.GetParameters().Length == argCount);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] HasMethod {instance.GetType().Name}.{name} failed: {ex.Message}");
            return false;
        }
    }

    public static object? CreateInstance(Type? type, object?[]? args, Type[]? argTypes = null)
    {
        if (type == null) return null;
        try
        {
            if (argTypes != null)
            {
                var key = $"{type.FullName}::new({string.Join(",", argTypes.Select(t => t.Name))})";
                ConstructorInfo? ctor;
                if (MemberCache.TryGetValue(key, out var cached) && cached is ConstructorInfo ci)
                {
                    ctor = ci;
                }
                else
                {
                    ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, argTypes, null);
                    if (ctor != null)
                        MemberCache[key] = ctor;
                }
                return ctor?.Invoke(args);
            }
            return Activator.CreateInstance(type, args);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Create {type.Name} failed: {ex.Message}");
            return null;
        }
    }

    public static Type? GenericType(string genericDefinitionName, params Type[] typeArgs)
    {
        var definition = Type(genericDefinitionName);
        if (definition == null) return null;
        try
        {
            return definition.MakeGenericType(typeArgs);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] MakeGenericType {genericDefinitionName} failed: {ex.Message}");
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

    private static FieldInfo? ResolveField(Type type, string name, bool isStatic)
    {
        var key = $"{type.FullName}::{name}:{(isStatic ? 's' : 'i')}f";
        if (MemberCache.TryGetValue(key, out var cached) && cached is FieldInfo field)
            return field;

        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var resolved = type.GetField(name, flags);
        if (resolved != null)
            MemberCache[key] = resolved;
        return resolved;
    }

    private static bool ArgsMatch(MethodInfo method, object?[]? args)
    {
        var parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var arg = args![i];
            if (arg == null)
            {
                if (param.ParameterType.IsValueType && Nullable.GetUnderlyingType(param.ParameterType) == null)
                    return false;
                continue;
            }
            if (!param.ParameterType.IsInstanceOfType(arg))
                return false;
        }
        return true;
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
