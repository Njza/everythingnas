using System.IO;
using System.Text.Json;

namespace NasSearchBar;

// Files the user pinned: listed under the empty search box, and first among matching results
public sealed class Pins(string path)
{
    public static readonly string FilePath = Path.Combine(Settings.Dir, "pins.json");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IgnoreReadOnlyProperties = true, WriteIndented = true };

    public List<Hit> Items { get; } = Load(path);

    public bool Contains(Hit h) => Items.Any(p => Same(p, h));

    public IEnumerable<Hit> PinnedFirst(IEnumerable<Hit> hits) => hits.OrderBy(h => !Contains(h));  // stable: keeps server order

    // The newest pin goes on top; toggling a pinned file unpins it
    public void Toggle(Hit h)
    {
        if (Items.RemoveAll(p => Same(p, h)) == 0) Items.Insert(0, h);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Items, Json));
        File.Move(path + ".tmp", path, overwrite: true);  // never leaves a half-written pins.json
    }

    static bool Same(Hit a, Hit b) => string.Equals(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);

    static List<Hit> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Hit>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            File.Move(path, path + ".bad", overwrite: true);  // keep the broken file instead of overwriting it later
            return [];
        }
    }
}
