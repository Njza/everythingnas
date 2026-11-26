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
    public string Hotkey { get; init; } = "Ctrl+Alt+Space";  // WPF KeyGesture text
    public string Exts { get; init; } = "";  // "pdf;docx": search only these formats, as in ext:pdf;docx; "" = all
    public string CertPin { get; init; } = "";  // SHA-256 of the NAS's certificate, seen at the first connection
    public bool SearchNas { get; init; } = true;  // the NAS is searched only when an address is set as well
    public bool SearchLocal { get; init; } = true;  // this PC's drives

    // The query sent to the server: the formats ticked in the bar's filter narrow every search
    public string Filtered(string q) => Exts == "" ? q : $"{q} ext:{Exts}";

    // https unless the address says http://: the NAS account's password must not cross the LAN in plain text
    [JsonIgnore]
    public Uri? Server
    {
        get
        {
            var a = Address.Trim();
            return a != "" && Uri.TryCreate(a.Contains("://") ? a : "https://" + a, UriKind.Absolute, out var u)
                   && u.Host != "" && u.Scheme is "https" or "http" ? u : null;
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
