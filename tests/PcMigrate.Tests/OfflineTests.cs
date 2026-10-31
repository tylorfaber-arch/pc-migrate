using PcMigrate.Core;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Offline;
using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class OfflinePathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pulled");
    private static string Here(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void MapsTheOldSystemDriveOntoThePulledDrive()
    {
        var paths = new OfflinePaths(Root);
        Assert.Equal(Here("Users", "tylor", "Documents"), paths.ToReadable(@"C:\Users\tylor\Documents"));
        Assert.Equal(Here("Users", "tylor"), paths.ToReadable(@"c:\Users\tylor"));
    }

    [Fact]
    public void LeavesOtherDrivesAlone()
    {
        var paths = new OfflinePaths(Root);
        Assert.Equal(@"D:\Games", paths.ToReadable(@"D:\Games"));
        Assert.Equal(@"C:foo", paths.ToReadable(@"C:foo"));
    }

    [Fact]
    public void ExpandsVariablesWithTheOldPcsValues()
    {
        var paths = new OfflinePaths(Root, "C:", new Dictionary<string, string> { ["ProgramData"] = @"%SystemDrive%\ProgramData" });
        Assert.Equal(@"C:\Program Files (x86)\Foo\foo.exe", paths.Expand(@"""%ProgramFiles(x86)%\Foo\foo.exe"""));
        Assert.Equal(@"C:\ProgramData\Foo", paths.Expand(@"%ProgramData%\Foo"));
        Assert.Equal(@"C:\Windows\System32\drivers\x.sys", paths.Expand(@"\SystemRoot\System32\drivers\x.sys"));
        Assert.Equal(@"C:\Tools\svc.exe", paths.Expand(@"\??\C:\Tools\svc.exe"));
        Assert.Equal(@"%UNKNOWN%\x", paths.Expand(@"%UNKNOWN%\x"));
    }

    [Fact]
    public void ToOriginalReversesToReadable()
    {
        var paths = new OfflinePaths(Root);
        var original = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Foo.lnk";
        Assert.Equal(original, paths.ToOriginal(paths.ToReadable(original)));
    }
}

public class OfflineWifiTests
{
    [Fact]
    public void RemovesTheEncryptedPasswordButKeepsTheNetwork()
    {
        const string xml = """
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>HomeWifi</name>
              <SSIDConfig><SSID><name>HomeWifi</name></SSID></SSIDConfig>
              <MSM><security>
                <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
                <sharedKey><keyType>passPhrase</keyType><protected>true</protected><keyMaterial>01000000D08C9DDF0115D1118C7A</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;
        var (result, name) = OfflineWifi.RemovePassword(xml);
        Assert.Equal("HomeWifi", name);
        Assert.DoesNotContain("keyMaterial", result);
        Assert.Contains("WPA2PSK", result);
        Assert.StartsWith("<?xml", result);
    }
}

/// <summary>A dead PC's drive: profiles are recorded as C:\Users\..., but the files are under a temp folder.</summary>
internal sealed class FakePulledDrive : ISystemProbe, IDisposable
{
    public FakePulledDrive(params string[] users)
    {
        Root = Path.Combine(Path.GetTempPath(), "pcmigrate-tests", "pulled-" + Guid.NewGuid().ToString("N")[..8]);
        Paths = new OfflinePaths(Root);
        Inventory = new Inventory
        {
            Machine = new MachineInfo { ComputerName = "DEAD-PC", OsDescription = "Test OS" },
            Users = users.Select((u, i) => new UserProfileInfo { UserName = u, Sid = $"S-1-5-21-9-9-9-{1000 + i}", ProfilePath = $@"C:\Users\{u}" }).ToList(),
        };
    }

    public string Root { get; }
    public OfflinePaths Paths { get; }
    public Inventory Inventory { get; }
    public string? CurrentUserSid => null;
    public OfflinePaths? Offline => Paths;
    public Inventory GetInventory() => Inventory;

    public void WriteFile(string user, string relative, string content)
    {
        var path = Paths.ToReadable($@"C:\Users\{user}\{relative.Replace('/', '\\')}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class OfflineCaptureTests
{
    [Fact]
    public async Task CapturesFromAPulledDriveAndKeepsTheOldPaths()
    {
        using var drive = new FakePulledDrive("sam");
        drive.WriteFile("sam", "Documents/taxes.pdf", "money");
        drive.WriteFile("sam", "Desktop/note.txt", "hi");
        using var store = new TempDir();

        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(drive, new ListLog()).RunAsync(sink, Options.Capture("sam"));

        var manifest = FolderSink.ReadManifest(store.Path);
        Assert.Equal("DEAD-PC", manifest.Inventory.Machine.ComputerName);
        Assert.Equal(@"C:\Users\sam", manifest.Inventory.Users.Single().ProfilePath);
        Assert.Contains(manifest.Files, f => f.StorePath == "files/users/sam/Documents/taxes.pdf");
        Assert.Contains(manifest.Files, f => f.StorePath == "files/users/sam/Desktop/note.txt");
        Assert.False(manifest.UsedShadowCopy);
    }

    [Fact]
    public void EstimatesFromThePulledDrive()
    {
        using var drive = new FakePulledDrive("sam");
        drive.WriteFile("sam", "Documents/a.txt", "12345");
        var (files, bytes) = CaptureEngine.Estimate(drive.Inventory.Users[0], new CaptureRules(), default, drive.Paths);
        Assert.Equal(1, files);
        Assert.Equal(5, bytes);
    }

    [Fact]
    public async Task MovingFromAHoldingStoreEmptiesItAndPutsFilesInPlace()
    {
        using var drive = new FakePulledDrive("sam");
        drive.WriteFile("sam", "Documents/taxes.pdf", "money");
        using var store = new TempDir();
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(drive, new ListLog()).RunAsync(sink, Options.Capture("sam"));

        using var newPc = new FakePc("NEW", "sam");
        var result = await new ApplyEngine(newPc, new ListLog()).RunAsync(store.Path, new ApplyOptions { IncludeWifi = false, IncludeUserSettings = false, MoveFromStore = true });

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal("money", File.ReadAllText(Path.Combine(newPc.Profile("sam"), "Documents", "taxes.pdf")));
        Assert.False(File.Exists(Path.Combine(store.Path, "files", "users", "sam", "Documents", "taxes.pdf")));
    }
}
