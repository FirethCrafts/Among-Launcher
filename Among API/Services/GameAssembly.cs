using System.Collections.Concurrent;

namespace AmongApi.Services;

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
        var acs = GetAssembly();
        if (acs != null)
        {
            var t = acs.GetType(name, false, true) ?? acs.GetTypes().FirstOrDefault(x => x.Name == name);
            if (t != null) return t;
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (ReferenceEquals(asm, acs)) continue;
            try
            {
                var t = asm.GetType(name, false, true) ?? asm.GetTypes().FirstOrDefault(x => x.Name == name);
                if (t != null) return t;
            }
            catch { }
        }

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
            Type? current = type;
            while (current != null)
            {
                var prop = ResolveProperty(current, name, isStatic: true);
                if (prop != null) return prop.GetValue(null);
                current = current.BaseType;
            }
            return null;
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
            Type? current = instance.GetType();
            while (current != null)
            {
                var prop = ResolveProperty(current, name, isStatic: false);
                if (prop != null) return prop.GetValue(instance);
                current = current.BaseType;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read {instance.GetType().Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static object? GetInstanceMember(object? instance, string name)
    {
        if (instance == null) return null;
        try
        {
            Type? current = instance.GetType();
            while (current != null)
            {
                var val = ResolveProperty(current, name, isStatic: false)?.GetValue(instance)
                       ?? ResolveField(current, name, isStatic: false)?.GetValue(instance);
                if (val != null) return val;
                current = current.BaseType;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read member {instance.GetType().Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static object? GetStaticMember(Type? type, string name)
    {
        if (type == null) return null;
        try
        {
            Type? current = type;
            while (current != null)
            {
                var val = ResolveProperty(current, name, isStatic: true)?.GetValue(null)
                       ?? ResolveField(current, name, isStatic: true)?.GetValue(null);
                if (val != null) return val;
                current = current.BaseType;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] Read static member {type.Name}.{name} failed: {ex.Message}");
            return null;
        }
    }

    public static object? CallStaticMethod(Type? type, string name, object?[]? args = null, Type[]? argTypes = null)
    {
        if (type == null) return null;
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var key = argTypes != null
                ? $"{type.FullName}::{name}({string.Join(",", argTypes.Select(t => t.Name))})"
                : $"{type.FullName}::{name}()";

            MethodInfo? method = null;
            if (MemberCache.TryGetValue(key, out var cached) && cached is MethodInfo m)
            {
                method = m;
            }
            else
            {
                Type? current = type;
                while (current != null && method == null)
                {
                    method = argTypes != null
                        ? current.GetMethod(name, flags, null, argTypes, null)
                        : current.GetMethod(name, flags);
                    current = current.BaseType;
                }
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
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            if (argTypes != null)
            {
                var key = $"{type.FullName}::{name}({string.Join(",", argTypes.Select(t => t.Name))})i";
                MethodInfo? method = null;
                if (MemberCache.TryGetValue(key, out var cached) && cached is MethodInfo m)
                {
                    method = m;
                }
                else
                {
                    Type? current = type;
                    while (current != null && method == null)
                    {
                        method = current.GetMethod(name, flags, null, argTypes, null);
                        current = current.BaseType;
                    }
                    if (method != null)
                        MemberCache[key] = method;
                }
                return method?.Invoke(instance, args);
            }

            var candidates = new List<MethodInfo>();
            Type? curr = type;
            while (curr != null)
            {
                candidates.AddRange(curr.GetMethods(flags)
                    .Where(m => m.Name == name && m.GetParameters().Length == (args?.Length ?? 0))
                    .Where(m => ArgsMatch(m, args)));
                curr = curr.BaseType;
            }

            if (candidates.Count == 0)
            {
                Log?.LogWarning($"[GameAssembly] No matching instance method {type.Name}.{name} for {args?.Length ?? 0} arg(s).");
                return null;
            }
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
            Type? current = instance.GetType();
            while (current != null)
            {
                if (current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(m => m.Name == name && m.GetParameters().Length == argCount))
                    return true;
                current = current.BaseType;
            }
            return false;
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
                    ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, argTypes, null);
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

    public static int ToInt(object? value)
    {
        if (value is null) return 0;
        if (value is int i) return i;
        if (value is uint u) return (int)u;
        if (value is short s) return s;
        if (value is ushort us) return us;
        if (value is long l) return (int)l;
        if (value is float f) return (int)f;
        if (value is double d) return (int)d;
        try { return Convert.ToInt32(value); }
        catch { return 0; }
    }

    public static bool ToBool(object? value) => value is bool b && b;

    public static string ToStr(object? value) => value as string ?? "";

    public static int GetPlayerLevel(object? playerInfo)
    {
        if (playerInfo == null) return 0;
        try
        {
            foreach (var name in new[] { "PlayerLevel", "Level", "playerLevel", "level" })
            {
                try
                {
                    var prop = ResolveProperty(playerInfo.GetType(), name, isStatic: false);
                    if (prop != null)
                    {
                        var val = prop.GetValue(playerInfo);
                        if (val != null) return ToInt(val);
                    }
                }
                catch { }

                try
                {
                    var field = ResolveField(playerInfo.GetType(), name, isStatic: false);
                    if (field != null)
                    {
                        var val = field.GetValue(playerInfo);
                        if (val != null) return ToInt(val);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] GetPlayerLevel failed: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// The LOCAL client's ping, read from <c>AmongUsClient.Instance.Ping</c>
    /// (the inherited <c>InnerNetClient.Ping</c> int). Among Us exposes no
    /// per-player ping, so this is the only real ping value available. Returns
    /// <c>-1</c> when it cannot be resolved — the same "unknown" sentinel used
    /// for every non-local player — so an unreadable ping is never mistaken for
    /// a genuine <c>0</c>.
    /// </summary>
    public static int GetLocalPing()
    {
        try
        {
            var client = AmongUsClient();
            if (client == null) return -1;
            var ping = GetInstanceMember(client, "Ping");
            if (ping == null) return -1;
            return ToInt(ping);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] GetLocalPing failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// True when <paramref name="playerInfo"/> is the local client's own
    /// <c>NetworkedPlayerInfo</c>. Joined on <c>PlayerId</c>
    /// (<c>PlayerControl.LocalPlayer.Data.PlayerId</c> vs the entry's) with a
    /// fallback to <c>ClientId</c> vs <c>InnerNetClient.CurrentClient</c>.
    /// </summary>
    public static bool IsLocalPlayer(object? playerInfo)
    {
        if (playerInfo == null) return false;
        try
        {
            var playerControlType = Type("PlayerControl");
            var localPlayer = playerControlType != null ? GetStaticMember(playerControlType, "LocalPlayer") : null;
            if (localPlayer != null)
            {
                var localData = GetInstanceProp(localPlayer, "Data");
                if (localData != null)
                {
                    // Guard the raw members before ToInt: ToInt(null) is 0, so an
                    // unresolved PlayerId on either side must not compare equal.
                    var localIdRaw = GetInstanceMember(localData, "PlayerId");
                    var thisIdRaw = GetInstanceMember(playerInfo, "PlayerId");
                    if (localIdRaw != null && thisIdRaw != null)
                    {
                        var localId = ToInt(localIdRaw);
                        var thisId = ToInt(thisIdRaw);
                        if (localId >= 0 && localId == thisId)
                            return true;
                    }
                }
            }

            var innerNetClientType = Type("InnerNet.InnerNetClient");
            if (innerNetClientType != null)
            {
                // Same guard on the fallback: an unresolved CurrentClient would
                // otherwise read as 0 and match the host's ClientId == 0.
                var currentClientRaw = GetStaticMember(innerNetClientType, "CurrentClient");
                if (currentClientRaw == null) return false;
                var clientIdRaw = GetInstanceMember(playerInfo, "ClientId");
                if (clientIdRaw == null) return false;
                var currentClient = ToInt(currentClientRaw);
                var clientId = ToInt(clientIdRaw);
                if (currentClient >= 0 && clientId == currentClient)
                    return true;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] IsLocalPlayer failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Best-effort ping for a specific <c>NetworkedPlayerInfo</c>. Among Us has
    /// no per-player ping, so only the local player's row carries a real value;
    /// every other player returns <c>-1</c> (a sentinel the launcher maps to
    /// "unknown" and hides).
    /// </summary>
    public static int GetPlayerPing(object? playerInfo)
    {
        if (playerInfo == null) return -1;
        try
        {
            if (!IsLocalPlayer(playerInfo)) return -1;
            return GetLocalPing();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] GetPlayerPing failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Player outfit colour name (lowercase) for a <c>NetworkedPlayerInfo</c>,
    /// read from <c>DefaultOutfit.ColorId</c>. Returns "" when unavailable.
    /// </summary>
    public static string GetPlayerColor(object? playerInfo)
    {
        if (playerInfo == null) return "";
        try
        {
            var outfit = GetInstanceMember(playerInfo, "DefaultOutfit");
            if (outfit == null) return "";

            var colorId = ToInt(GetInstanceMember(outfit, "ColorId"));
            if (colorId < 0 || colorId >= ColorNames.Length) return "";
            return ColorNames[colorId];
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] GetPlayerColor failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Index-aligned with <c>PlayerOutfit.ColorId</c> in Assembly-CSharp.
    /// </summary>
    private static readonly string[] ColorNames =
    {
        "red", "blue", "green", "pink", "orange", "yellow",
        "black", "white", "purple", "brown", "cyan", "lime"
    };

    public static bool InLobby()
    {
        var lobbyBehaviour = Type("LobbyBehaviour");
        if (GetStaticMember(lobbyBehaviour, "Instance") != null)
            return true;

        var client = AmongUsClient();
        if (client == null)
            return false;

        var gameStateEnum = Type("InnerNet.InnerNetClient")?.GetNestedType("GameStates");
        var state = GetInstanceProp(client, "GameState");
        if (!EnumEquals(state, EnumValue(gameStateEnum, "Joined")))
            return false;

        return ToBool(GetInstanceProp(client, "InOnlineScene"));
    }

    public static object? AmongUsClient() => GetStaticMember(Type("AmongUsClient"), "Instance");

    public static string CurrentRegionName()
    {
        var serverManagerType = Type("ServerManager");
        var serverManager = serverManagerType != null ? GetStaticMember(serverManagerType, "Instance") : null;
        if (serverManager == null)
            return "UNKNOWN";

        var region = GetInstanceProp(serverManager, "CurrentRegion");
        if (region == null)
            return "UNKNOWN";

        var name = ToStr(GetInstanceProp(region, "Name"));
        return string.IsNullOrEmpty(name) ? "UNKNOWN" : name;
    }

public static string LocalPlayerName()
    {
        // Path 1: PlayerControl.LocalPlayer.Data.PlayerName (standard Among Us path)
        try
        {
            var playerControlType = Type("PlayerControl");
            var localPlayer = playerControlType != null ? GetStaticMember(playerControlType, "LocalPlayer") : null;
            if (localPlayer != null)
            {
                var data = GetInstanceProp(localPlayer, "Data");
                if (data != null)
                {
                    var name = ToStr(GetInstanceProp(data, "PlayerName"));
                    if (!string.IsNullOrEmpty(name) && name != "UNKNOWN")
                        return name;
                }
            }
        }
        catch { }

        // Path 2: Direct PlayerName on PlayerControl
        try
        {
            var playerControlType = Type("PlayerControl");
            var localPlayer = playerControlType != null ? GetStaticMember(playerControlType, "LocalPlayer") : null;
            if (localPlayer != null)
            {
                var name = ToStr(GetInstanceProp(localPlayer, "PlayerName"));
                if (!string.IsNullOrEmpty(name) && name != "UNKNOWN")
                    return name;
            }
        }
        catch { }

        // Path 3: GameData.Players lookup by PlayerId
        try
        {
            var playerControlType = Type("PlayerControl");
            var localPlayer = playerControlType != null ? GetStaticMember(playerControlType, "LocalPlayer") : null;
            if (localPlayer != null)
            {
                var playerId = ToInt(GetInstanceProp(localPlayer, "PlayerId"));
                if (playerId > 0)
                {
                    var name = TryGetPlayerNameById(playerId);
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
        }
        catch { }

        FileLogger.Warn("[GameAssembly] LocalPlayerName: all attempts failed, returning UNKNOWN");
        return "UNKNOWN";
    }

    private static string? TryGetPlayerNameById(int playerId)
    {
        try
        {
            var gameDataType = Type("GameData");
            if (gameDataType == null) return null;
            
            var gameDataInstance = GetStaticProp(gameDataType, "Instance");
            if (gameDataInstance == null) return null;
            
            var allPlayers = GetInstanceProp(gameDataInstance, "AllPlayers");
            if (allPlayers == null) return null;
            
            var countObj = GetInstanceProp(allPlayers, "Count");
            var count = ToInt(countObj);
            
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var playerInfo = CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                    if (playerInfo == null) continue;
                    
                    var id = ToInt(GetInstanceProp(playerInfo, "PlayerId"));
                    if (id == playerId)
                    {
                        var name = ToStr(GetInstanceProp(playerInfo, "PlayerName"));
                        FileLogger.Info($"[GameAssembly] TryGetPlayerNameById: Found player {playerId}: '{name}'");
                        return name;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"[GameAssembly] TryGetPlayerNameById: player[{i}] failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] TryGetPlayerNameById failed: {ex.Message}");
        }
        return null;
    }

    private static PropertyInfo? ResolveProperty(Type type, string name, bool isStatic)
    {
        var key = $"{type.FullName}::{name}:{(isStatic ? 's' : 'i')}";
        if (MemberCache.TryGetValue(key, out var cached) && cached is PropertyInfo prop)
            return prop;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
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

        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
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

    public static void DebugObjectProperties(object? instance, string label)
    {
        if (instance == null)
        {
            FileLogger.Info($"[GameAssembly] DebugObjectProperties ({label}): instance is null");
            return;
        }

        try
        {
            var type = instance.GetType();
            FileLogger.Info($"[GameAssembly] DebugObjectProperties ({label}): type={type.FullName}");
            
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FileLogger.Info($"[GameAssembly] DebugObjectProperties ({label}): {props.Length} properties");
            
            foreach (var prop in props)
            {
                try
                {
                    var value = prop.GetValue(instance);
                    var valueStr = value?.ToString() ?? "null";
                    if (valueStr.Length > 100)
                        valueStr = valueStr.Substring(0, 100) + "...";
                    FileLogger.Info($"[GameAssembly] DebugObjectProperties ({label}): {prop.Name} = {valueStr}");
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"[GameAssembly] DebugObjectProperties ({label}): {prop.Name} failed: {ex.Message}");
                }
            }
            
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FileLogger.Info($"[GameAssembly] DebugObjectProperties ({label}): {fields.Length} fields");
            
            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(instance);
                    var valueStr = value?.ToString() ?? "null";
                    if (valueStr.Length > 100)
                        valueStr = valueStr.Substring(0, 100) + "...";
                    FileLogger.Info($"[GameAssembly] DebugObjectProperties ({label}): {field.Name} = {valueStr}");
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"[GameAssembly] DebugObjectProperties ({label}): {field.Name} failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] DebugObjectProperties ({label}) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads every player's name from <c>GameData.Instance.AllPlayers</c>.
    /// Pass <paramref name="log"/> = false for high-frequency polls so the
    /// reflection tracing does not flood the log.
    /// </summary>
    /// <remarks>
    /// The returned list is <b>raw-index aligned</b> with <c>AllPlayers</c>:
    /// a null/empty slot contributes an <c>""</c> placeholder instead of being
    /// skipped, so <c>names[i]</c> matches the same player as
    /// <c>GetAllPlayerLevels()[i]</c> / <c>GetAllPlayerPings()[i]</c> /
    /// <c>GetAllPlayerColors()[i]</c>. Consumers skip empty names when building
    /// per-player payloads.
    /// </remarks>
    public static List<string> GetAllPlayerNames(bool log = true)
    {
        var names = new List<string>();
        try
        {
            if (log) FileLogger.Info("[GameAssembly] GetAllPlayerNames: Starting...");

            var gameDataType = Type("GameData");
            if (log) FileLogger.Info($"[GameAssembly] GetAllPlayerNames: GameData type={gameDataType != null}");

            var gameDataInstance = GetStaticProp(gameDataType, "Instance");
            if (gameDataInstance == null)
            {
                if (log) FileLogger.Warn("[GameAssembly] GetAllPlayerNames: GameData.Instance is null");
                return names;
            }
            if (log) FileLogger.Info($"[GameAssembly] GetAllPlayerNames: GameData.Instance={gameDataInstance.GetType().FullName}");

            var allPlayers = GetInstanceProp(gameDataInstance, "AllPlayers");
            if (allPlayers == null)
            {
                if (log) FileLogger.Warn("[GameAssembly] GetAllPlayerNames: AllPlayers is null");
                return names;
            }
            if (log) FileLogger.Info($"[GameAssembly] GetAllPlayerNames: AllPlayers type={allPlayers.GetType().FullName}");

            // AllPlayers is Il2CppSystem.Collections.Generic.List<NetworkedPlayerInfo>
            var countObj = GetInstanceProp(allPlayers, "Count");
            var count = ToInt(countObj);
            if (log) FileLogger.Info($"[GameAssembly] GetAllPlayerNames: AllPlayers.Count={count}");

            // Match the level/ping/color readers: a lobby is never larger than
            // 15 players, so a count outside that range is a stale/garbage read.
            if (count <= 0 || count > 15)
                return names;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var playerInfo = CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                    if (playerInfo == null)
                    {
                        if (log) FileLogger.Warn($"[GameAssembly] GetAllPlayerNames: player[{i}] is null");
                        names.Add("");
                        continue;
                    }

                    // Emit a "" placeholder rather than skipping empty entries so
                    // names[i] stays aligned with AllPlayers[i] (and therefore with
                    // the level/ping/color arrays, which index the same raw list).
                    names.Add(ToStr(GetInstanceProp(playerInfo, "PlayerName")));
                }
                catch (Exception ex)
                {
                    if (log) FileLogger.Warn($"[GameAssembly] GetAllPlayerNames: player[{i}] failed: {ex.Message}");
                    names.Add("");
                }
            }

            if (log) FileLogger.Info($"[GameAssembly] GetAllPlayerNames: Returning {names.Count} names: [{string.Join(", ", names)}]");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[GameAssembly] GetAllPlayerNames failed: {ex.Message}");
            FileLogger.Error($"[GameAssembly] GetAllPlayerNames stack trace: {ex.StackTrace}");
        }
        return names;
    }

    /// <summary>
    /// Best-effort read of the real lobby host's name, for use by a guest
    /// client (whose own <see cref="LocalPlayerName"/> is NOT the host).
    /// <c>AmongUsClient.Instance.HostId</c> is the host's client id and each
    /// <c>GameData.Instance.AllPlayers</c> entry (<c>NetworkedPlayerInfo</c>)
    /// exposes a matching <c>ClientId</c>, so the two are joined on that.
    /// Returns "" when the host cannot be resolved — callers must never fall
    /// back to the local player's name here.
    /// </summary>
    public static string HostPlayerName()
    {
        try
        {
            var client = AmongUsClient();
            if (client == null)
                return "";

            var hostIdObj = GetInstanceMember(client, "HostId");
            if (hostIdObj == null)
                return "";
            var hostId = ToInt(hostIdObj);

            var gameDataType = Type("GameData");
            var gameDataInstance = gameDataType != null ? GetStaticProp(gameDataType, "Instance") : null;
            if (gameDataInstance == null)
                return "";

            var allPlayers = GetInstanceProp(gameDataInstance, "AllPlayers");
            if (allPlayers == null)
                return "";

            var count = ToInt(GetInstanceProp(allPlayers, "Count"));
            if (count <= 0 || count > 15)
                return "";

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var playerInfo = CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                    if (playerInfo == null)
                        continue;

                    var clientId = ToInt(GetInstanceMember(playerInfo, "ClientId"));
                    if (clientId != hostId)
                        continue;

                    var name = ToStr(GetInstanceProp(playerInfo, "PlayerName"));
                    if (!string.IsNullOrEmpty(name) && name != "UNKNOWN")
                    {
                        FileLogger.Info($"[GameAssembly] HostPlayerName: resolved host '{name}' (clientId {hostId}).");
                        return name;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"[GameAssembly] HostPlayerName: player[{i}] failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] HostPlayerName failed: {ex.Message}");
        }

        FileLogger.Warn("[GameAssembly] HostPlayerName: could not resolve the lobby host name; returning empty.");
        return "";
    }

    public static string GameVersion()
    {
        try
        {
            var client = AmongUsClient();
            if (client != null)
            {
                var version = ToStr(GetInstanceProp(client, "GameVersion"));
                if (!string.IsNullOrEmpty(version))
                    return version;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] GameVersion failed: {ex.Message}");
        }
        return "";
    }

    public static string MapName()
    {
        try
        {
            var client = AmongUsClient();
            if (client == null) return "";

            var gameOptions = GetInstanceProp(client, "GameHostOpts")
                ?? GetInstanceProp(client, "NormalOptions");
            if (gameOptions != null)
            {
                var mapId = ToInt(GetInstanceProp(gameOptions, "MapId"));
                return MapIdToName(mapId);
            }

            var playerControlType = Type("PlayerControl");
            var localPlayer = GetStaticMember(playerControlType, "LocalPlayer");
            if (localPlayer != null)
            {
                var gameOptions2 = GetInstanceProp(localPlayer, "GameOptions");
                if (gameOptions2 != null)
                {
                    var mapId = ToInt(GetInstanceProp(gameOptions2, "MapId"));
                    return MapIdToName(mapId);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] MapName failed: {ex.Message}");
        }
        return "";
    }

    private static string MapIdToName(int mapId) => mapId switch
    {
        0 => "The Skeld",
        1 => "MIRA HQ",
        2 => "Polus",
        3 => "Dleks",
        4 => "The Airship",
        5 => "The Fungle",
        _ => ""
    };

    public static string Language()
    {
        try
        {
            var client = AmongUsClient();
            if (client == null) return "";

            var gameOptions = GetInstanceProp(client, "GameHostOpts")
                ?? GetInstanceProp(client, "NormalOptions");
            if (gameOptions != null)
            {
                var lang = ToStr(GetInstanceProp(gameOptions, "Language"));
                if (!string.IsNullOrEmpty(lang)) return lang;
            }

            var playerControlType = Type("PlayerControl");
            var localPlayer = GetStaticMember(playerControlType, "LocalPlayer");
            if (localPlayer != null)
            {
                var gameOptions2 = GetInstanceProp(localPlayer, "GameOptions");
                if (gameOptions2 != null)
                {
                    var lang = ToStr(GetInstanceProp(gameOptions2, "Language"));
                    if (!string.IsNullOrEmpty(lang)) return lang;
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] Language failed: {ex.Message}");
        }
        return "";
    }

    public static string ChatType()
    {
        try
        {
            var client = AmongUsClient();
            if (client == null) return "";

            var gameOptions = GetInstanceProp(client, "GameHostOpts")
                ?? GetInstanceProp(client, "NormalOptions");
            if (gameOptions != null)
            {
                var chat = GetInstanceProp(gameOptions, "ChatType");
                if (chat != null)
                    return ToStr(chat);
            }

            var playerControlType = Type("PlayerControl");
            var localPlayer = GetStaticMember(playerControlType, "LocalPlayer");
            if (localPlayer != null)
            {
                var gameOptions2 = GetInstanceProp(localPlayer, "GameOptions");
                if (gameOptions2 != null)
                {
                    var chat = GetInstanceProp(gameOptions2, "ChatType");
                    if (chat != null)
                        return ToStr(chat);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] ChatType failed: {ex.Message}");
        }
        return "";
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
