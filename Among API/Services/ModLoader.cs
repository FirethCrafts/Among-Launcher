using System.Text;

namespace AmongApi.Services;

public class ModLoader
{
    private readonly ManualLogSource _log;
    private readonly List<Assembly> _loadedAssemblies = [];

    public IReadOnlyList<Assembly> LoadedAssemblies => _loadedAssemblies;

    public ModLoader(ManualLogSource log)
    {
        _log = log;
    }

    public void LoadAndActivate(byte[] bytes, string modId)
    {
        try
        {
            var assembly = Assembly.Load(bytes);
            _loadedAssemblies.Add(assembly);

            var modTypes = assembly.GetTypes()
                .Where(t => typeof(IHostMod).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .ToList();

            if (modTypes.Count == 0)
            {
                _log.LogWarning($"[Loader] {modId}: no IHostMod implementations found. Applying Harmony patches.");
                TryPatchAll(assembly, modId);
                return;
            }

            foreach (var type in modTypes)
            {
                try
                {
                    var instance = (IHostMod)Activator.CreateInstance(type)!;
                    instance.OnLoad();
                    _log.LogInfo($"[Loader] {modId}: {type.Name} activated.");
                }
                catch (Exception ex)
                {
                    _log.LogError($"[Loader] {modId}: failed to activate {type.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"[Loader] {modId}: failed to load assembly: {ex.Message}");
            throw;
        }
    }

    private void TryPatchAll(Assembly assembly, string modId)
    {
        try
        {
            var harmonyType = Type.GetType("HarmonyLib.Harmony, HarmonyX");
            if (harmonyType == null)
            {
                _log.LogWarning($"[Loader] {modId}: HarmonyX not found. Skipping patches.");
                return;
            }

            var harmony = Activator.CreateInstance(harmonyType, $"amongapi.{modId}")!;
            var patchAllMethod = harmonyType.GetMethod("PatchAll", [typeof(Assembly)]);
            patchAllMethod?.Invoke(harmony, [assembly]);

            _log.LogInfo($"[Loader] {modId}: Harmony patches applied.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Loader] {modId}: Harmony patching failed: {ex.Message}");
        }
    }
}
