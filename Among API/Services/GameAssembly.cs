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
            var level = GetInstanceProp(playerInfo, "PlayerLevel");
            if (level != null) return ToInt(level);
            level = GetInstanceMember(playerInfo, "Level");
            if (level != null) return ToInt(level);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] GetPlayerLevel failed: {ex.Message}");
        }
        return 0;
    }

    public static int GetPlayerPing(object? playerInfo)
    {
        if (playerInfo == null) return 0;
        try
        {
            var ping = GetInstanceProp(playerInfo, "Ping");
            if (ping != null) return ToInt(ping);
            ping = GetInstanceMember(playerInfo, "Ping");
            if (ping != null) return ToInt(ping);
        }
        catch (Exception ex)
        {
            Log?.LogWarning($"[GameAssembly] GetPlayerPing failed: {ex.Message}");
        }
        return 0;
    }

    public static bool InLobby()
    {
        var lobbyBehaviour = Type("LobbyBehaviour");
        if (GetStaticProp(lobbyBehaviour, "Instance") != null)
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

    public static object? AmongUsClient() => GetStaticProp(Type("AmongUsClient"), "Instance");

    public static string CurrentRegionName()
    {
        var serverManagerType = Type("ServerManager");
        var serverManager = serverManagerType != null ? GetStaticProp(serverManagerType, "Instance") : null;
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
        FileLogger.Info("[GameAssembly] LocalPlayerName: Starting name resolution...");
        
        var playerControlType = Type("PlayerControl");
        FileLogger.Info($"[GameAssembly] LocalPlayerName: PlayerControl type resolved={playerControlType != null}");
        
        if (playerControlType == null)
        {
            FileLogger.Warn("[GameAssembly] LocalPlayerName: PlayerControl type not found");
            return "UNKNOWN";
        }

        var localPlayer = GetStaticMember(playerControlType, "LocalPlayer");
        FileLogger.Info($"[GameAssembly] LocalPlayerName: LocalPlayer={localPlayer != null}");
        
        if (localPlayer == null)
        {
            FileLogger.Warn("[GameAssembly] LocalPlayerName: PlayerControl.LocalPlayer is null");
            
            // Try alternative: GameData.Instance.AllPlayers to find local player by ID
            var localName = TryGetLocalPlayerFromGameData();
            if (!string.IsNullOrEmpty(localName))
            {
                FileLogger.Info($"[GameAssembly] LocalPlayerName: Found via GameData fallback: '{localName}'");
                return localName;
            }
            
            return "UNKNOWN";
        }

        var localPlayerType = localPlayer.GetType();
        FileLogger.Info($"[GameAssembly] LocalPlayerName: LocalPlayer type={localPlayerType.FullName}");
        
        // Debug: List all properties and fields on localPlayer
        DebugObjectProperties(localPlayer, "LocalPlayer");

        // Try Data.PlayerName (standard path)
        var data = GetInstanceProp(localPlayer, "Data");
        FileLogger.Info($"[GameAssembly] LocalPlayerName: Data={data != null}");
        
        if (data != null)
        {
            var dataType = data.GetType();
            FileLogger.Info($"[GameAssembly] LocalPlayerName: Data type={dataType.FullName}");
            
            // Debug: List all properties and fields on Data object
            DebugObjectProperties(data, "LocalPlayer.Data");
            
            var name = ToStr(GetInstanceProp(data, "PlayerName"));
            FileLogger.Info($"[GameAssembly] LocalPlayerName: Data.PlayerName='{name}'");
            if (!string.IsNullOrEmpty(name) && name != "UNKNOWN")
                return name;
        }
        else
        {
            FileLogger.Warn("[GameAssembly] LocalPlayerName: Data is null");
        }

        // Try direct PlayerName property on PlayerControl
        var directName = ToStr(GetInstanceProp(localPlayer, "PlayerName"));
        FileLogger.Info($"[GameAssembly] LocalPlayerName: direct PlayerName='{directName}'");
        if (!string.IsNullOrEmpty(directName) && directName != "UNKNOWN")
            return directName;

        // Try name property (Unity Object.name)
        var unityName = ToStr(GetInstanceProp(localPlayer, "name"));
        FileLogger.Info($"[GameAssembly] LocalPlayerName: name='{unityName}'");
        if (!string.IsNullOrEmpty(unityName) && unityName != "UNKNOWN")
            return unityName;

        // Try AmongUsClient.GetPlayerName (alternative method)
        var client = AmongUsClient();
        if (client != null)
        {
            var methodName = HasInstanceMethod(client, "GetPlayerName", 0) ? "GetPlayerName" : null;
            if (methodName != null)
            {
                var result = CallInstanceMethod(client, methodName);
                var clientName = ToStr(result);
                FileLogger.Info($"[GameAssembly] LocalPlayerName: client.GetPlayerName()='{clientName}'");
                if (!string.IsNullOrEmpty(clientName) && clientName != "UNKNOWN")
                    return clientName;
            }
        }

        // Try getting name from PlayerId field
        var playerId = GetInstanceProp(localPlayer, "PlayerId");
        FileLogger.Info($"[GameAssembly] LocalPlayerName: PlayerId={playerId}");
        if (playerId != null)
        {
            var playerNameFromId = TryGetPlayerNameById(ToInt(playerId));
            if (!string.IsNullOrEmpty(playerNameFromId))
            {
                FileLogger.Info($"[GameAssembly] LocalPlayerName: Found via PlayerId: '{playerNameFromId}'");
                return playerNameFromId;
            }
        }

        FileLogger.Warn("[GameAssembly] LocalPlayerName: all attempts failed, returning UNKNOWN");
        return "UNKNOWN";
    }

    private static string? TryGetLocalPlayerFromGameData()
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
            FileLogger.Info($"[GameAssembly] TryGetLocalPlayerFromGameData: AllPlayers.Count={count}");
            
            for (int i = 0; i < count; i++)
            {
                var playerInfo = CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                if (playerInfo == null) continue;
                
                var isLocal = ToBool(GetInstanceProp(playerInfo, "IsLocal"));
                if (isLocal)
                {
                    var name = ToStr(GetInstanceProp(playerInfo, "PlayerName"));
                    FileLogger.Info($"[GameAssembly] TryGetLocalPlayerFromGameData: Found local player at index {i}: '{name}'");
                    return name;
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[GameAssembly] TryGetLocalPlayerFromGameData failed: {ex.Message}");
        }
        return null;
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

    public static List<string> GetAllPlayerNames()
    {
        var names = new List<string>();
        try
        {
            FileLogger.Info("[GameAssembly] GetAllPlayerNames: Starting...");
            
            var gameDataType = Type("GameData");
            FileLogger.Info($"[GameAssembly] GetAllPlayerNames: GameData type={gameDataType != null}");
            
            var gameDataInstance = GetStaticProp(gameDataType, "Instance");
            if (gameDataInstance == null)
            {
                FileLogger.Warn("[GameAssembly] GetAllPlayerNames: GameData.Instance is null");
                return names;
            }
            FileLogger.Info($"[GameAssembly] GetAllPlayerNames: GameData.Instance={gameDataInstance.GetType().FullName}");

            var allPlayers = GetInstanceProp(gameDataInstance, "AllPlayers");
            if (allPlayers == null)
            {
                FileLogger.Warn("[GameAssembly] GetAllPlayerNames: AllPlayers is null");
                return names;
            }
            FileLogger.Info($"[GameAssembly] GetAllPlayerNames: AllPlayers type={allPlayers.GetType().FullName}");

            // AllPlayers is Il2CppSystem.Collections.Generic.List<PlayerInfo>
            var countObj = GetInstanceProp(allPlayers, "Count");
            var count = ToInt(countObj);
            FileLogger.Info($"[GameAssembly] GetAllPlayerNames: AllPlayers.Count={count}");

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var playerInfo = CallInstanceMethod(allPlayers, "get_Item", new object[] { i }, new[] { typeof(int) });
                    if (playerInfo == null)
                    {
                        FileLogger.Warn($"[GameAssembly] GetAllPlayerNames: player[{i}] is null");
                        continue;
                    }

                    var playerName = ToStr(GetInstanceProp(playerInfo, "PlayerName"));
                    if (!string.IsNullOrEmpty(playerName))
                    {
                        names.Add(playerName);
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"[GameAssembly] GetAllPlayerNames: player[{i}] failed: {ex.Message}");
                }
            }
            
            FileLogger.Info($"[GameAssembly] GetAllPlayerNames: Returning {names.Count} names: [{string.Join(", ", names)}]");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[GameAssembly] GetAllPlayerNames failed: {ex.Message}");
            FileLogger.Error($"[GameAssembly] GetAllPlayerNames stack trace: {ex.StackTrace}");
        }
        return names;
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
