namespace AmongApi.Models;

public class ModManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; }

    [JsonPropertyName("mods")]
    public List<ModEntry> Mods { get; set; } = [];
}
