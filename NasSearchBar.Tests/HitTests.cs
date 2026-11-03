using System.Text.Json;
using NasSearchBar;

public class HitTests
{
    // Captured from nas_search.py's /search endpoint
    const string ServerJson = """
        {"total": 2, "indexed": 13, "scanned": 1793682644.4, "results": [
          {"name": "\u732b\u54aa.jpg", "path": "\\\\DS920\\photo\\2024", "size": 846338, "mtime": 1793682642.7},
          {"name": "docs", "path": "\\\\DS920", "size": null, "mtime": 1793682642.7}]}
        """;

    [Theory]
    [InlineData(@"\\N\docs\合同\客户A", null, @"\\N\docs\合同\", "客户A")]
    [InlineData(@"\\N\docs\合同\客户A", "Z:", @"Z:\合同\", "客户A")]
    [InlineData(@"\\N\docs", "Z:", "", "Z:")]  // file at the share root
    [InlineData(@"\\N", null, "", @"\\N")]  // a share at the NAS root
    public void Location_split(string path, string? drive, string outer, string inner)
    {
        var h = new Hit("a.pdf", path, 1, 0) { Drive = drive };
        Assert.Equal(outer, h.LocationOuter);
        Assert.Equal(inner, h.LocationInner);
    }

    [Fact]
    public void Admin_share_on_this_pc_is_its_drive()
    {
        var none = new Dictionary<string, string>();
        Assert.Equal("C:", new Hit("a", @"\\localhost\c$\Users", 1, 0).DriveIn(none));
        Assert.Equal(@"C:\Users\a", new Hit("a", @"\\localhost\C$\Users", 1, 0).OpenPath(none));
        Assert.Null(new Hit("a", @"\\DS920\C$\Users", 1, 0).DriveIn(none));  // another machine's C: is not ours
        Assert.Null(new Hit("a", @"\\localhost\docs", 1, 0).DriveIn(none));
    }

    [Fact]
    public void Parses_server_response()
    {
        var r = JsonSerializer.Deserialize<SearchResponse>(ServerJson, SearchResponse.Json)!;
        Assert.Equal(2, r.Total);
        var cat = r.Results[0];
        Assert.Equal("猫咪.jpg", cat.Name);
        Assert.Equal(@"\\DS920\photo\2024\猫咪.jpg", cat.FullPath);
        Assert.Equal("photo", cat.Share);
        Assert.Equal("2024", cat.Folder);
        Assert.Equal("826.5 KB", cat.SizeText);

        var share = r.Results[1];  // a share folder sits at the NAS root
        Assert.Equal("", share.Share);
        Assert.Equal(@"\\DS920", share.Folder);
        Assert.Equal("文件夹", share.Kind);
        Assert.Equal("", share.SizeText);
    }

    [Fact]
    public void Open_path_uses_mapped_drive_letter()
    {
        var drives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DOCS"] = "Z:" };
        Assert.Equal(@"Z:\合同\a.docx", new Hit("a.docx", @"\\DS920\docs\合同", 1, 0).OpenPath(drives));
        Assert.Equal(@"Z:\a.docx", new Hit("a.docx", @"\\DS920\docs", 1, 0).OpenPath(drives));
        Assert.Equal(@"\\DS920\video\m.mkv", new Hit("m.mkv", @"\\DS920\video", 1, 0).OpenPath(drives));
        Assert.Equal(@"\\DS920\docs", new Hit("docs", @"\\DS920", null, 0).OpenPath(drives));
    }

    [Theory]
    [InlineData("合同.DOCX", "docx", "word")]
    [InlineData("台账.xlsx", "xlsx", "excel")]
    [InlineData("deck.pptx", "pptx", "powerpoint")]
    [InlineData("scan.pdf", "pdf", "pdf")]
    [InlineData("movie.mkv", "mkv", "")]
    [InlineData("README", "文件", "")]
    [InlineData("notes.markdown", "markd", "")]
    public void Kind_and_app_from_extension(string name, string kind, string app)
    {
        var h = new Hit(name, @"\\DS920\docs", 1, 0);
        Assert.Equal(kind, h.Kind);
        Assert.Equal(app, h.App);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(5L << 40, "5.0 TB")]
    public void Size_text(long size, string text) => Assert.Equal(text, new Hit("a", @"\\N\s", size, 0).SizeText);

    [Fact]
    public void Date_this_year_omits_year()
    {
        var now = DateTime.Now;
        Assert.Equal($"{now.Month}月{now.Day}日", new Hit("a", @"\\N\s", 1, DateTimeOffset.Now.ToUnixTimeSeconds()).DateText);
        Assert.Equal("2001年", new Hit("a", @"\\N\s", 1, 1000000000).DateText[..5]);  // 2001-09-09 UTC
    }
}
