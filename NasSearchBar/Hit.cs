using System.Text.Json;
using System.Text.Json.Serialization;

namespace NasSearchBar;

// Shape of nas_search.py's /search response. No WPF types here, so tests run on any OS.
public record SearchResponse(int Total, int Indexed, Hit[] Results)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

// Path is the UNC folder, e.g. \\DS920\docs\合同; Size is null for folders.
public record Hit(string Name, string Path, long? Size, double Mtime)
{
    string[] Parts => Path.TrimStart('\\').Split('\\', 3);  // [nas, share, folder]

    // Set by the window from Pins for display; never stored or sent
    [JsonIgnore]
    public bool Pinned { get; init; }

    public string FullPath => Path + "\\" + Name;
    public string Share => Parts.Length > 1 ? Parts[1] : "";
    public string Folder => Parts.Length > 2 ? Parts[2] : Parts.Length == 1 ? Path : "";

    // Z:\folder\name when this share is mapped to a drive letter, else the UNC path
    public string OpenPath(IReadOnlyDictionary<string, string> driveByShare) =>
        driveByShare.TryGetValue(Share, out var drive)
            ? string.Join('\\', new[] { drive, Folder, Name }.Where(s => s != ""))
            : FullPath;

    public string Kind => Size is null ? "文件夹" : System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant() switch
    {
        "" => "文件",
        var e when e.Length > 5 => e[..5],  // the tag is 40px wide
        var e => e,
    };

    // The app that opens it; the tag color follows this
    public string App => Kind switch
    {
        "doc" or "docx" or "rtf" or "odt" => "word",
        "xls" or "xlsx" or "xlsm" or "csv" or "ods" => "excel",
        "ppt" or "pptx" or "odp" => "powerpoint",
        "pdf" => "pdf",
        _ => "",
    };

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
