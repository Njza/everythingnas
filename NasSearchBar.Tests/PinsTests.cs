using NasSearchBar;

public class PinsTests
{
    static readonly Hit Contract = new("合同.docx", @"\\DS920\docs\合同", 48000, 1793682642);
    static readonly Hit Sheet = new("台账.xlsx", @"\\DS920\docs", 1200, 1793682642);
    static readonly Hit Movie = new("alien.mkv", @"\\DS920\video", 9, 0);

    static string TempFile() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "pins.json");

    [Fact]
    public void Toggle_adds_newest_first_then_removes()
    {
        var pins = new Pins(TempFile());
        pins.Toggle(Contract);
        pins.Toggle(Sheet);
        Assert.Equal([Sheet, Contract], pins.Items);
        pins.Toggle(Contract with { Pinned = true, Name = "合同.DOCX" });  // same file, any case, any display flag
        Assert.Equal([Sheet], pins.Items);
    }

    [Fact]
    public void Survives_restart_without_computed_fields()
    {
        var path = TempFile();
        new Pins(path).Toggle(Contract);
        Assert.Equal([Contract], new Pins(path).Items);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("fullPath", json);
        Assert.DoesNotContain("pinned", json);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Pinned_first_keeps_server_order_otherwise()
    {
        var pins = new Pins(TempFile());
        pins.Toggle(Movie);
        Assert.Equal([Movie, Contract, Sheet], pins.PinnedFirst([Contract, Movie, Sheet]));
    }

    [Fact]
    public void Broken_file_is_kept_aside_not_overwritten()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new Pins(path).Items);
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
    }
}
