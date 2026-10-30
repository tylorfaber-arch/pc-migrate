using PcMigrate.Core.Apply;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class CaptureApplyTests
{
    private static readonly DateTime Old = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public async Task FolderRoundTrip_CopiesFilesWithTimestampsIntoMappedUser()
    {
        using var oldPc = new FakePc("old", "tylor", "guest");
        using var newPc = new FakePc("new", "tfaber");
        using var store = new TempDir();
        oldPc.WriteFile("tylor", "Documents/taxes/2023.pdf", "pdf bytes", Old);
        oldPc.WriteFile("tylor", "Desktop/notes.txt", "hello");
        oldPc.WriteFile("tylor", "AppData/Local/Google/Chrome/User Data/Default/Bookmarks", "{}");
        oldPc.WriteFile("tylor", "AppData/Local/Google/Chrome/User Data/Default/Cache/data_1", "cache");
        oldPc.WriteFile("tylor", "AppData/Local/SomeGame/save.dat", "not in the rules");
        oldPc.WriteFile("guest", "Documents/guest.txt", "guest");

        var log = new ListLog();
        await using (var sink = new FolderSink(store.Path))
        {
            var manifest = await new CaptureEngine(oldPc, log).RunAsync(sink, Options.Capture("tylor"));
            Assert.Equal(3, manifest.Files.Count);
        }

        var result = await new ApplyEngine(newPc, log).RunAsync(store.Path, new ApplyOptions
        {
            UserMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tylor"] = "tfaber" },
        });

        Assert.Equal(3, result.FilesCopied);
        Assert.Equal(0, result.FilesFailed);
        var pdf = Path.Combine(newPc.Profile("tfaber"), "Documents", "taxes", "2023.pdf");
        Assert.Equal("pdf bytes", File.ReadAllText(pdf));
        Assert.Equal(Old, File.GetLastWriteTimeUtc(pdf));
        Assert.True(File.Exists(Path.Combine(newPc.Profile("tfaber"), "AppData", "Local", "Google", "Chrome", "User Data", "Default", "Bookmarks")));
        Assert.False(Directory.Exists(Path.Combine(newPc.Profile("tfaber"), "AppData", "Local", "SomeGame")));
        Assert.NotNull(result.ReportPath);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task NewerWins_KeepsNewerFileOnNewPc()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        using var store = new TempDir();
        oldPc.WriteFile("a", "Documents/older.txt", "from old", Old);
        oldPc.WriteFile("a", "Documents/newer.txt", "from old", Old.AddDays(10));
        newPc.WriteFile("a", "Documents/older.txt", "edited on new", Old.AddDays(5));
        newPc.WriteFile("a", "Documents/newer.txt", "stale on new", Old.AddDays(5));

        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog()).RunAsync(sink, Options.Capture());
        var result = await new ApplyEngine(newPc, new ListLog()).RunAsync(store.Path, new ApplyOptions());

        Assert.Equal("edited on new", File.ReadAllText(Path.Combine(newPc.Profile("a"), "Documents", "older.txt")));
        Assert.Equal("from old", File.ReadAllText(Path.Combine(newPc.Profile("a"), "Documents", "newer.txt")));
        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public async Task DryRun_ChangesNothing()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        using var store = new TempDir();
        oldPc.WriteFile("a", "Documents/x.txt", "x");
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog()).RunAsync(sink, Options.Capture());

        var result = await new ApplyEngine(newPc, new ListLog()).RunAsync(store.Path, new ApplyOptions { DryRun = true });

        Assert.Equal(1, result.FilesCopied);
        Assert.False(File.Exists(Path.Combine(newPc.Profile("a"), "Documents", "x.txt")));
    }

    [Fact]
    public async Task DamagedStoreFile_IsReportedNotCopied()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        using var store = new TempDir();
        oldPc.WriteFile("a", "Documents/x.txt", "original");
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog()).RunAsync(sink, Options.Capture());
        File.WriteAllText(StorePath.ToLocal(store.Path, "files/users/a/Documents/x.txt"), "tampered");

        var log = new ListLog();
        var result = await new ApplyEngine(newPc, log).RunAsync(store.Path, new ApplyOptions());

        Assert.Equal(1, result.FilesFailed);
        Assert.False(File.Exists(Path.Combine(newPc.Profile("a"), "Documents", "x.txt")));
        Assert.Contains(log.Warnings, w => w.Contains("damaged"));
    }

    [Fact]
    public async Task MissingUser_IsReported()
    {
        using var oldPc = new FakePc("old", "bob");
        using var newPc = new FakePc("new", "alice");
        using var store = new TempDir();
        oldPc.WriteFile("bob", "Documents/x.txt", "x");
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog()).RunAsync(sink, Options.Capture());

        var result = await new ApplyEngine(newPc, new ListLog()).RunAsync(store.Path, new ApplyOptions());

        Assert.Equal(["bob"], result.MissingUsers);
        Assert.Equal(0, result.FilesCopied);
    }

    [Fact]
    public async Task SkippedUser_IsNotCopiedOrReportedMissing()
    {
        using var oldPc = new FakePc("old", "a", "b");
        using var newPc = new FakePc("new", "a", "b");
        using var store = new TempDir();
        oldPc.WriteFile("a", "Documents/a.txt", "a");
        oldPc.WriteFile("b", "Documents/b.txt", "b");
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog()).RunAsync(sink, Options.Capture());

        var result = await new ApplyEngine(newPc, new ListLog()).RunAsync(store.Path, new ApplyOptions
        {
            SkipUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B" },
        });

        Assert.Equal(1, result.FilesCopied);
        Assert.Empty(result.MissingUsers);
        Assert.False(File.Exists(Path.Combine(newPc.Profile("b"), "Documents", "b.txt")));
    }

    [Fact]
    public void Estimate_MatchesWhatCaptureCopies()
    {
        using var pc = new FakePc("old", "a");
        pc.WriteFile("a", "Documents/x.txt", "12345");
        pc.WriteFile("a", "Desktop/y.txt", "123");
        pc.WriteFile("a", "Documents/skip.tmp", "123456789");
        var (files, bytes) = CaptureEngine.Estimate(pc.Inventory.Users[0], new CaptureRules());
        Assert.Equal(2, files);
        Assert.Equal(8, bytes);
    }

    [Fact]
    public async Task UnknownUserInCapture_Throws()
    {
        using var oldPc = new FakePc("old", "a");
        using var store = new TempDir();
        await using var sink = new FolderSink(store.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => new CaptureEngine(oldPc, new ListLog()).RunAsync(sink, Options.Capture("nobody")));
    }

    [Fact]
    public void ReadManifest_WithoutManifest_ExplainsIncompleteCapture()
    {
        using var store = new TempDir();
        var ex = Assert.Throws<FileNotFoundException>(() => FolderSink.ReadManifest(store.Path));
        Assert.Contains("did not finish", ex.Message);
    }

    [Theory]
    [InlineData("Google Chrome", "Google Chrome")]
    [InlineData("7-Zip 23.01 (x64)", "7 zip")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33130", "microsoft visual c++ 2015 2022 redistributable")]
    [InlineData("VLC media player 3.0.20", "vlc media player")]
    public void NormalizeAppName(string input, string expected) =>
        Assert.Equal(expected.ToLowerInvariant(), ApplyEngine.NormalizeAppName(input));

    [Fact]
    public void FindMissingApps_IgnoresVersionDifferences()
    {
        static InstalledApp App(string name) => new() { Name = name, Scope = "Machine", RegistryKey = name };
        var missing = ApplyEngine.FindMissingApps(
            [App("Google Chrome"), App("7-Zip 23.01 (x64)"), App("Spotify")],
            [App("Google Chrome"), App("7-Zip 24.08 (x64)")]).Select(a => a.Name);
        Assert.Equal(["Spotify"], missing);
    }
}
