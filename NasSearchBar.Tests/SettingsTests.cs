using NasSearchBar;

public class SettingsTests
{
    [Fact]
    public void Saves_and_loads()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "settings.json");
        var s = new Settings { Address = "192.168.1.10:8765", User = "me", ProtectedPassword = "x", UseDriveLetters = true, Hotkey = "Ctrl+Shift+F" };
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

    [Theory]
    [InlineData("192.168.1.10:8765", "http://192.168.1.10:8765/")]
    [InlineData(" ds920:8765 ", "http://ds920:8765/")]
    [InlineData("http://192.168.1.10:8765", "http://192.168.1.10:8765/")]  // pasted scheme is tolerated
    [InlineData("", null)]
    [InlineData("my nas", null)]
    public void Server_from_address(string address, string? url) =>
        Assert.Equal(url, new Settings { Address = address }.Server?.ToString());
}
