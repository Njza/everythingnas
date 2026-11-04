using System.Security.Cryptography;
using NasSearchBar;

public class UpdaterTests
{
    [Theory]
    [InlineData("v1.6.0", "1.5.0", "1.6.0")]
    [InlineData("v1.5.1", "1.5.0", "1.5.1")]
    [InlineData("1.10.0", "1.9.0", "1.10.0")]  // numbers, not text: 10 > 9
    [InlineData("v1.5.0", "1.5.0", null)]  // the same version, also when the exe says 1.5.0.0
    [InlineData("v1.4.9", "1.5.0", null)]
    [InlineData("v2.0", "1.5.0", "2.0.0")]
    [InlineData("nightly", "1.5.0", null)]
    [InlineData(null, "1.5.0", null)]
    public void Newer_release(string? tag, string current, string? expected) =>
        Assert.Equal(expected, Updater.Newer(tag, new Version(current))?.ToString());

    [Fact]
    public void Running_version_has_three_parts()
    {
        Assert.Equal(-1, Updater.Current.Revision);  // compares as equal to a "v1.5.0" tag
        Assert.Null(Updater.Newer("v" + Updater.Current, Updater.Current));
    }

    [Fact]
    public void Download_checked_against_githubs_digest()
    {
        byte[] data = [1, 2, 3];
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));
        Assert.True(Updater.Matches(data, digest));
        Assert.True(Updater.Matches(data, digest.ToUpperInvariant().Replace("SHA256:", "sha256:")));
        Assert.False(Updater.Matches([1, 2, 4], digest));
        Assert.True(Updater.Matches(data, null));  // older uploads have no digest
    }
}
