using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NasSearchBar;

public sealed record Settings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NasSearch");
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Address { get; init; } = "";  // host:port on the LAN, e.g. 192.168.1.10:8765
    public string User { get; init; } = "";
    public string ProtectedPassword { get; init; } = "";  // DPAPI-encrypted by the app, never plain text on disk
    public bool UseDriveLetters { get; init; }
    public string Hotkey { get; init; } = "Ctrl+Alt+Space";  // WPF KeyGesture text

    // shortcut: plain http, LAN only; add https and a second address if this ever has to work away from home
    [JsonIgnore]
    public Uri? Server
    {
        get
        {
            var host = Address.Trim();
            if (host.Contains("://")) host = host[(host.IndexOf("://") + 3)..];  // tolerate a pasted http://
            return host != "" && Uri.TryCreate("http://" + host, UriKind.Absolute, out var u) && u.Host != "" ? u : null;
        }
    }

    public static Settings Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Indented));
    }
}
