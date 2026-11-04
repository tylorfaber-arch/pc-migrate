using PcMigrate.Core.Update;

namespace PcMigrate.Tests;

public class UpdaterTests
{
    private static string Release(string tag, string digest = "sha256:ABC123", string body = "What's new") => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/tylorfaber-arch/pc-migrate/releases/tag/{{tag}}",
          "body": "{{body}}",
          "assets": [
            { "name": "pcmigrate-cli.exe", "size": 10, "browser_download_url": "https://example.test/cli.exe", "digest": "sha256:ffff" },
            { "name": "pc-migrate.exe", "size": 70000000, "browser_download_url": "https://example.test/app.exe", "digest": "{{digest}}" }
          ]
        }
        """;

    [Fact]
    public void OffersANewerBuild()
    {
        var update = Updater.Parse(Release("v0.1.0.61"), "pc-migrate.exe", new Version(0, 1, 0, 57));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 1, 0, 61), update.Version);
        Assert.Equal("https://example.test/app.exe", update.DownloadUrl);
        Assert.Equal(70000000, update.Size);
        Assert.Equal("abc123", update.Sha256);
    }

    [Theory]
    [InlineData("v0.1.0.57")]
    [InlineData("v0.1.0.12")]
    [InlineData("v0.0.9.99")]
    public void IgnoresTheSameOrOlderBuild(string tag) =>
        Assert.Null(Updater.Parse(Release(tag), "pc-migrate.exe", new Version(0, 1, 0, 57)));

    [Fact]
    public void ALocalBuildCountsAsOlderThanAnyRelease() =>
        Assert.NotNull(Updater.Parse(Release("v0.1.0.1"), "pc-migrate.exe", new Version(0, 1, 0)));

    [Fact]
    public void NeedsTheRightAsset() =>
        Assert.Null(Updater.Parse(Release("v0.2.0.1"), "something-else.exe", new Version(0, 1, 0, 1)));

    [Fact]
    public void WorksWithoutAChecksum()
    {
        var update = Updater.Parse(Release("v0.2.0.1", digest: "md5:zz"), "pc-migrate.exe", new Version(0, 1, 0, 1));
        Assert.NotNull(update);
        Assert.Null(update.Sha256);
    }

    [Fact]
    public void SwapKeepsTheOldVersionUntilCleanUp()
    {
        using var dir = new TempDir();
        var exe = Path.Combine(dir.Path, "pc-migrate.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(exe + ".new", "new");

        Updater.Swap(exe, exe + ".new");
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));

        Updater.CleanUp(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    private const string Marked = "<!-- required-version: 0.2.0 | Backups made with older versions can't be restored. -->\\n**Required update**";

    [Fact]
    public void AnOlderCopyIsToldTheUpdateIsRequired()
    {
        var update = Updater.Parse(Release("v0.2.0.60", body: Marked), "pc-migrate.exe", new Version(0, 1, 0, 49));
        Assert.NotNull(update);
        Assert.True(update.Required);
        Assert.Equal("Backups made with older versions can't be restored.", update.RequiredBecause);
    }

    [Fact]
    public void ACopyAtOrPastTheMinimumOnlyGetsAnOptionalUpdate()
    {
        var update = Updater.Parse(Release("v0.2.0.61", body: Marked), "pc-migrate.exe", new Version(0, 2, 0, 60));
        Assert.NotNull(update);
        Assert.False(update.Required);
    }

    [Fact]
    public void NotesWithoutTheMarkerAreNeverRequired() =>
        Assert.False(Updater.Parse(Release("v0.2.0.61"), "pc-migrate.exe", new Version(0, 1, 0, 1))!.Required);

    [Theory]
    [InlineData("<!--required-version:0.1.0.49|x-->", "0.1.0.49", "x")]
    [InlineData("intro\n<!-- required-version: 0.2.0 |  -->\nmore", "0.2.0", null)]
    public void ReadsTheMarker(string notes, string version, string? reason)
    {
        var req = Updater.RequiredBy(notes);
        Assert.NotNull(req);
        Assert.Equal(Version.Parse(version), req.Value.Minimum);
        if (reason is not null) Assert.Equal(reason, req.Value.Reason);
        else Assert.False(string.IsNullOrWhiteSpace(req.Value.Reason));
    }
}
