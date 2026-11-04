using NasSearchBar;

public class LocalIndexTests
{
    // a temp tree: 合同/客户A.pdf, 合同/报价.docx, photos/IMG_0001.jpg, photos/Sample.JPG
    static readonly string Root = MakeTree();

    static string MakeTree()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        foreach (var f in new[] { @"合同\客户A.pdf", @"合同\报价.docx", @"photos\IMG_0001.jpg", @"photos\Sample.JPG" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, f))!);
            File.WriteAllText(Path.Combine(root, f), "x");
        }
        return root;
    }

    static readonly LocalIndex Index = new([Root], TimeSpan.FromHours(1));

    static async Task<string[]> Names(string q) =>
        (await Index.SearchAsync(q, CancellationToken.None)).Results.Select(h => h.Name).ToArray();

    [Theory]
    [InlineData("客户", "客户A.pdf")]
    [InlineData("jpg", "IMG_0001.jpg Sample.JPG")]  // case-insensitive, name order
    [InlineData("jpg !sample", "IMG_0001.jpg")]
    [InlineData("pdf|docx", "客户A.pdf 报价.docx")]
    [InlineData("<pdf|docx> !报价", "客户A.pdf")]
    [InlineData("IMG_????.jpg", "IMG_0001.jpg")]
    [InlineData("ext:pdf;docx", "客户A.pdf 报价.docx")]
    [InlineData("folder:合同", "合同")]
    [InlineData(@"photos\ img", "IMG_0001.jpg")]
    [InlineData(@"合同\报", "报价.docx")]
    [InlineData("case:Sample", "Sample.JPG")]
    [InlineData("case:sample", "")]
    [InlineData(@"regex:^IMG_\d+", "IMG_0001.jpg")]
    public async Task Everything_syntax(string q, string expected) =>
        Assert.Equal(expected, string.Join(' ', await Names(q)));

    [Fact]
    public async Task Hits_open_as_local_paths()
    {
        var h = (await Index.SearchAsync("客户A", CancellationToken.None)).Results.Single();
        Assert.Equal(Path.Combine(Root, "合同", "客户A.pdf"), h.OpenPath);
        Assert.Equal(1, h.Size);
    }

    [Fact]
    public async Task Counts_formats()
    {
        var exts = (await Index.ExtsAsync(CancellationToken.None)).Exts;
        Assert.Equal(2, exts.Single(e => e.Ext == "jpg").Count);
    }
}
