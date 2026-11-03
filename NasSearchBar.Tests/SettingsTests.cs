using NasSearchBar;

public class SettingsTests
{
    [Fact]
    public void Saves_and_loads()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "settings.json");
        var s = new Settings { Address = "192.168.1.10:8765", User = "me", ProtectedPassword = "x", Hotkey = "Ctrl+Shift+F", Exts = "pdf;docx" };
        s.Save(path);
        Assert.Equal(s, Settings.Load(path));
        Assert.DoesNotContain("Server", File.ReadAllText(path).Replace("Address", ""));
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        Assert.Equal(new Settings(), Settings.Load("/nonexistent/settings.json"));
        Assert.Equal("Ctrl+Alt+Space", new Settings().Hotkey);
    }

    [Fact]
    public void Ticked_formats_narrow_the_query()
    {
        Assert.Equal("合同", new Settings().Filtered("合同"));
        Assert.Equal("合同 ext:pdf;docx", new Settings { Exts = "pdf;docx" }.Filtered("合同"));
    }

    [Theory]
    [InlineData("192.168.1.10:8765", "http://192.168.1.10:8765/")]
    [InlineData(" ds920:8765 ", "http://ds920:8765/")]
    [InlineData("http://192.168.1.10:8765", "http://192.168.1.10:8765/")]  // pasted scheme is tolerated
    [InlineData("", null)]
    [InlineData("my nas", null)]
    public void Server_from_address(string address, string? url) =>
        Assert.Equal(url, new Settings { Address = address }.Server?.ToString());
}
