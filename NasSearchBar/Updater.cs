using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace NasSearchBar;

// Updates the exe from the repo's latest GitHub release. A running exe cannot be overwritten but can be renamed:
// the new one takes its name and runs from the next start; the old one is deleted then.
public static class Updater
{
    const string Repo = "Njza/everythingnas", Asset = "NasSearch.exe";

    public static readonly Version Current = Plain(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    static Updater() => Http.DefaultRequestHeaders.UserAgent.ParseAdd($"NasSearch/{Current}");  // GitHub's API wants one

    static string Exe => Environment.ProcessPath!;
    static string Old => Exe + ".old";

    // The version a previous update replaced; still in use until that run ended
    public static void CleanUp()
    {
        try { File.Delete(Old); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // "v1.6.0" newer than the running version? Tags that are no version (e.g. "nightly") never are.
    public static Version? Newer(string? tag, Version current) =>
        Version.TryParse(tag?.TrimStart('v', 'V'), out var v) && Plain(v) > current ? Plain(v) : null;

    // GitHub's "sha256:..." digest of a release asset; a release without one (older uploads) is trusted on HTTPS alone
    public static bool Matches(byte[] data, string? digest) =>
        digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)
        || Convert.ToHexStringLower(SHA256.HashData(data)) == digest["sha256:".Length..].ToLowerInvariant();

    // 1.5 and 1.5.0.0 are both 1.5.0: Version would rank 1.5.0 below 1.5.0.0
    static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // Downloads a newer release and puts it in place. Returns its version, or null when this one is the latest.
    // Throws HttpRequestException, IOException, UnauthorizedAccessException, JsonException or InvalidDataException.
    public static async Task<Version?> UpdateAsync(CancellationToken ct)
    {
        using var release = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        if (Newer(release.RootElement.GetProperty("tag_name").GetString(), Current) is not { } latest) return null;
        var asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == Asset);
        if (asset.ValueKind != JsonValueKind.Object) return null;  // a release without the exe, e.g. server-only

        var data = await Http.GetByteArrayAsync(asset.GetProperty("browser_download_url").GetString(), ct);
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (!Matches(data, digest)) throw new InvalidDataException("下载的文件校验不对");

        var next = Exe + ".new";
        await File.WriteAllBytesAsync(next, data, ct);
        CleanUp();
        if (File.Exists(Old))  // still running from an update earlier in this run: the exe in place is not in use
        {
            File.Move(next, Exe, overwrite: true);
            return latest;
        }
        File.Move(Exe, Old);  // allowed while it runs
        try
        {
            File.Move(next, Exe);
        }
        catch
        {
            File.Move(Old, Exe);  // put the running version back rather than leave no exe at all
            throw;
        }
        return latest;
    }
}
