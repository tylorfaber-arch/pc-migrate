using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class StorePathTests
{
    [Theory]
    [InlineData("files/users/a/Documents/x.txt", true)]
    [InlineData("settings/wifi/Home.xml", true)]
    [InlineData("../evil.txt", false)]
    [InlineData("files/../../evil.txt", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Windows/evil.dll", false)]
    [InlineData("files\\users\\a.txt", false)]
    [InlineData("files//a.txt", false)]
    [InlineData("files/./a.txt", false)]
    [InlineData("", false)]
    [InlineData("a.txt:hidden", false)]
    public void IsSafe(string path, bool expected) => Assert.Equal(expected, StorePath.IsSafe(path));

    [Fact]
    public void ToLocal_RejectsUnsafe() =>
        Assert.Throws<InvalidDataException>(() => StorePath.ToLocal(Path.GetTempPath(), "../x"));

    [Fact]
    public void TryParseUserFile()
    {
        Assert.True(StorePath.TryParseUserFile("files/users/tylor/Documents/a b.txt", out var user, out var rel));
        Assert.Equal("tylor", user);
        Assert.Equal("Documents/a b.txt", rel);
        Assert.False(StorePath.TryParseUserFile("settings/wifi/x.xml", out _, out _));
        Assert.False(StorePath.TryParseUserFile("files/users/tylor", out _, out _));
    }
}
