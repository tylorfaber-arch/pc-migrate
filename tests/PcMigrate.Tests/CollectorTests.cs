using PcMigrate.Core.Capture;

namespace PcMigrate.Tests;

public class CollectorTests
{
    [Theory]
    [InlineData("report.tmp", true)]
    [InlineData("~$Budget.xlsx", true)]
    [InlineData("THUMBS.DB", true)]
    [InlineData("Budget.xlsx", false)]
    public void FilePatterns(string name, bool excluded) => Assert.Equal(excluded, new CaptureRules().IsFileExcluded(name));

    [Fact]
    public void SkipsCachesAndLinksAndKeepsTheRest()
    {
        using var pc = new FakePc("old", "a");
        pc.WriteFile("a", "Documents/keep.txt", "1");
        pc.WriteFile("a", "Documents/sub/deep/keep2.txt", "2");
        pc.WriteFile("a", "Documents/junk.tmp", "x");
        pc.WriteFile("a", "Documents/Cache/big.bin", "x");
        var documents = Path.Combine(pc.Profile("a"), "Documents");
        // Like the "My Music" junction Windows puts in Documents.
        Directory.CreateSymbolicLink(Path.Combine(documents, "Loop"), pc.Profile("a"));

        var warnings = new List<string>();
        var files = FileCollector.Enumerate(documents, "Documents", new CaptureRules(), warnings.Add)
            .Select(f => f.RelativePath).OrderBy(p => p).ToList();

        Assert.Equal(["Documents/keep.txt", "Documents/sub/deep/keep2.txt"], files);
        Assert.Empty(warnings);
    }

    [Fact]
    public void MissingFolderYieldsNothing() =>
        Assert.Empty(FileCollector.Enumerate("/does/not/exist", "x", new CaptureRules(), _ => { }));
}
