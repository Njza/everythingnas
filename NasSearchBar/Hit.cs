using System.Text.Json;
using System.Text.Json.Serialization;

namespace NasSearchBar;

// Shape of nas_search.py's /search response. No WPF types here, so tests run on any OS.
public record SearchResponse(int Total, int Indexed, Hit[] Results)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

// nas_search.py's /exts: every file extension in the index, most files first
public record ExtsResponse(ExtCount[] Exts);
public record ExtCount(string Ext, int Count);

// Path is the UNC folder, e.g. \\DS920\docs\合同; Size is null for folders.
public record Hit(string Name, string Path, long? Size, double Mtime)
{
    string[] Parts => Path.TrimStart('\\').Split('\\', 3);  // [nas, share, folder]

    // Set by the window from Pins for display; never stored or sent
    [JsonIgnore]
    public bool Pinned { get; init; }

    // Set by the window from the settings address, e.g. 192.168.1.10, so paths open by the NAS's LAN
    // address instead of NAS_NAME; display only, never stored or sent (pins stay keyed by the server's path)
    [JsonIgnore]
    public string? Host { get; init; }

    public string FullPath => Path + "\\" + Name;
    public string Share => Parts.Length > 1 ? Parts[1] : "";
    public string Folder => Parts.Length > 2 ? Parts[2] : Parts.Length == 1 ? Path : "";

    // "C:" for this PC's own C$ admin share; else null
    public string? Drive =>
        Share is [var c, '$'] && char.IsAsciiLetter(c) && IsThisPc(Parts[0]) ? char.ToUpperInvariant(c) + ":" : null;

    static bool IsThisPc(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1"
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    // \\192.168.1.10\share\folder\name, or C:\folder\name for this PC's own drive
    public string OpenPath => In(Name);

    // The folder as you would type it, C:\Users\... or \\192.168.1.10\share\..., split so the row can trim the
    // outer part and always show the folder holding the file
    public string Location => In("");
    public string LocationOuter => Location.LastIndexOf('\\') is var i and >= 2 ? Location[..(i + 1)] : "";
    public string LocationInner => Location[LocationOuter.Length..];

    // the server's path with its \\NAS_NAME swapped for \\Host
    string Unc => Host is null ? Path : @"\\" + Host + Path[(2 + Parts[0].Length)..];

    string In(string name) => string.Join('\\', (Drive is { } d ? new[] { d, Folder, name } : [Unc, name]).Where(s => s != ""));

    public string SizeText => Size switch
    {
        null => "",
        < 1024 => $"{Size} B",
        long n => $"{n / Math.Pow(1024, Unit(n)):0.0} {"KMGT"[Unit(n) - 1]}B",
    };

    public string DateText
    {
        get
        {
            var d = DateTimeOffset.FromUnixTimeMilliseconds((long)(Mtime * 1000)).LocalDateTime;
            return d.Year == DateTime.Now.Year ? $"{d.Month}月{d.Day}日" : $"{d.Year}年{d.Month}月{d.Day}日";
        }
    }

    static int Unit(long n) => Math.Clamp((int)Math.Log(n, 1024), 1, 4);
}
