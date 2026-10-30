using System.Text;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class WingetListTests
{
    // Real "winget list" output shape, including the spinner it draws first.
    private const string Output = "   - \r   \\ \r                                                                                                                        \r" + """
Name                                    Id                                  Version          Available      Source
-------------------------------------------------------------------------------------------------------------------
Google Chrome                           Google.Chrome                       130.0.6723.70    130.0.6723.92  winget
Spotify                                 Spotify.Spotify                     1.2.48.405                      winget
Microsoft Visual Studio Code (User)     Microsoft.VisualStudioCode          1.95.0                          winget
Some Internal Tool                      ARP\Machine\X64\{1234-ABCD}         2.0
A Program With A Really Long Name That… Vendor.LongNameProgram              3.1                             winget
""";

    [Fact]
    public void ParsesRows()
    {
        var rows = WingetList.Parse(Output);
        Assert.Equal(5, rows.Count);
        Assert.Equal(new WingetRow("Google Chrome", "Google.Chrome", "130.0.6723.70", "winget"), rows[0]);
        Assert.Equal("Spotify.Spotify", rows[1].Id);
        Assert.Equal("", rows[3].Source);
        Assert.Equal("A Program With A Really Long Name That", rows[4].Name);
    }

    [Fact]
    public void NoTableMeansNoRows() => Assert.Empty(WingetList.Parse("No installed package found matching input criteria."));
}

public class AppHelperTests
{
    [Fact]
    public void RegRewrite_SwapsUserRoot()
    {
        var reg = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_USERS\\S-1-5-21-1-1001\\Software\\Spotify]\r\n\"Volume\"=dword:00000050\r\n\r\n[-HKEY_USERS\\S-1-5-21-1-1001\\Software\\Spotify\\Old]\r\n";
        var result = RegFile.RewriteRoot(reg, @"HKU\S-1-5-21-1-1001", @"HKU\PCMIGRATE_ab12");
        Assert.Contains(@"[HKEY_USERS\PCMIGRATE_ab12\Software\Spotify]", result);
        Assert.Contains(@"[-HKEY_USERS\PCMIGRATE_ab12\Software\Spotify\Old]", result);
        Assert.Contains("\"Volume\"=dword:00000050", result);
        Assert.DoesNotContain("1001", result);
    }

    [Fact]
    public void RegFile_RoundTripsAsUtf16()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.reg");
        RegFile.Write(path, "Windows Registry Editor Version 5.00\r\n[HKEY_USERS\\x]\r\n\"Name\"=\"Zoë\"\r\n");
        Assert.Equal(0xFF, File.ReadAllBytes(path)[0]); // UTF-16 LE byte order mark, as reg.exe expects
        Assert.Contains("Zoë", RegFile.Read(path));
    }

    [Fact]
    public void Shortcut_FindsTargetInEitherEncoding()
    {
        var unicode = Encoding.Unicode.GetBytes("junk C:\\Program Files\\Foo\\foo.exe junk");
        var ansi = Encoding.Latin1.GetBytes("junk c:\\program files\\foo\\foo.exe");
        Assert.True(Shortcuts.PointsInto(unicode, @"C:\Program Files\Foo"));
        Assert.True(Shortcuts.PointsInto(ansi, @"C:\Program Files\Foo\"));
        Assert.False(Shortcuts.PointsInto(ansi, @"C:\Program Files\Bar"));
    }

    [Fact]
    public void Key_IsStableAndPathSafe()
    {
        var a = AppNames.Key("Notepad++ (64-bit x64)", @"HKLM\...\Notepad++");
        Assert.Equal(a, AppNames.Key("Notepad++ (64-bit x64)", @"HKLM\...\Notepad++"));
        Assert.Matches("^[a-z0-9-]+$", a);
        Assert.NotEqual(a, AppNames.Key("Notepad++ (64-bit x64)", @"HKLM\...\Other"));
    }

    [Theory]
    [InlineData("Spotify", "Spotify AB", "spotify")]
    [InlineData("Notepad++", "Notepad++ Team", "notepad++")]
    [InlineData("VLC media player", "VideoLAN", "vlc")]
    [InlineData("OBS Studio", "OBS Project", "obs-studio")]
    [InlineData("Microsoft Teams", "Microsoft", "teams")]
    public void Candidates(string name, string publisher, string expected) =>
        Assert.Contains(expected, SettingsLocator.Candidates(name, publisher));

    [Fact]
    public void FindKeys_MatchesProgramAndPublisherProgram()
    {
        var app = new AppEntry { Key = "k", Name = "Steam", Publisher = "Valve Corporation", Reason = "", UninstallKey = "u", Scope = "Machine" };
        var keys = SettingsLocator.FindKeys(app, ["Valve", @"Valve\Steam", "Microsoft", @"Microsoft\Steam", "Classes"]);
        Assert.Equal([@"Valve\Steam"], keys);
    }
}

/// <summary>A pretend Windows for program moving.</summary>
internal sealed class FakeApps : IAppPlatform
{
    public List<WingetRow>? Winget { get; set; } = [];
    public HashSet<string> ServiceFolders { get; } = [];
    public Dictionary<string, List<string>> UserKeys { get; } = [];
    public List<string> Installed { get; } = [];
    public List<(string User, string Content)> ImportedForUser { get; } = [];
    public List<string> ImportedMachine { get; } = [];
    public bool FailInstalls { get; set; }

    public IReadOnlyList<WingetRow>? ListWinget() => Winget;
    public bool HasServiceUnder(string folder) => ServiceFolders.Contains(folder);
    public IReadOnlyList<string> UserSoftwareKeys(UserProfileInfo user) => UserKeys.TryGetValue(user.UserName, out var k) ? k : [];
    public IReadOnlyList<string> MachineSoftwareKeys() => ["FooCorp", @"FooCorp\FooTool"];

    public string? ExportUserKey(UserProfileInfo user, string subKey, string file)
    {
        RegFile.Write(file, $"Windows Registry Editor Version 5.00\r\n\r\n[HKEY_USERS\\{user.Sid}\\Software\\{subKey}]\r\n\"x\"=\"1\"\r\n");
        return $@"HKU\{user.Sid}";
    }

    public bool ExportKey(string keyPath, string file)
    {
        if (keyPath.Contains("WOW6432Node")) return false; // only the 64-bit key exists here
        RegFile.Write(file, $"Windows Registry Editor Version 5.00\r\n\r\n[{keyPath.Replace("HKLM", "HKEY_LOCAL_MACHINE")}]\r\n");
        return true;
    }

    public bool ImportUserFile(UserProfileInfo user, string file, string exportedRoot)
    {
        ImportedForUser.Add((user.UserName, RegFile.RewriteRoot(RegFile.Read(file), exportedRoot, $@"HKU\{user.Sid}")));
        return true;
    }

    public bool ImportFile(string file)
    {
        ImportedMachine.Add(RegFile.Read(file));
        return true;
    }

    public IReadOnlyList<string> SharedShortcutFolders() => [];

    public Task<(bool Ok, string Message)> WingetInstallAsync(string id, CancellationToken ct)
    {
        if (FailInstalls) return Task.FromResult((false, "boom"));
        Installed.Add(id);
        return Task.FromResult((true, ""));
    }
}

public class AppPlannerAndMoveTests
{
    private static InstalledApp App(string name, string? location = null, bool msi = false, string scope = "Machine", string? publisher = null, string? uninstall = null) => new()
    {
        Name = name, InstallLocation = location, IsMsi = msi, Scope = scope, Publisher = publisher,
        UninstallString = uninstall, RegistryKey = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
    };

    [Fact]
    public void Plan_RatesEachProgram()
    {
        using var pc = new FakePc("old", "tylor");
        var toolDir = Directory.CreateDirectory(Path.Combine(pc.Root, "Tools", "FooTool")).FullName;
        var serviceDir = Directory.CreateDirectory(Path.Combine(pc.Root, "Tools", "Agent")).FullName;
        pc.Inventory.Apps.AddRange(
        [
            App("Spotify", publisher: "Spotify AB"),
            App("FooTool 2.1", toolDir, publisher: "FooCorp"),
            App("Some Agent", serviceDir),
            App("Contoso Payroll", @"C:\nowhere", msi: true),
            App("Realtek High Definition Audio Driver"),
            App("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38"),
            App("Microsoft 365 - en-us"),
            App("Bloons TD 6", uninstall: "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/960090"),
        ]);
        var platform = new FakeApps { Winget = [new WingetRow("Spotify", "Spotify.Spotify", "1.2", "winget")] };
        platform.ServiceFolders.Add(serviceDir);

        var plans = AppPlanner.Plan(pc.Inventory, platform, pc.Inventory.Users, new CaptureRules()).ToDictionary(p => p.Name);

        Assert.Equal(AppMethod.Reinstall, plans["Spotify"].Method);
        Assert.Equal("Spotify.Spotify", plans["Spotify"].WingetId);
        Assert.Equal(AppMethod.Copy, plans["FooTool 2.1"].Method);
        Assert.Equal([@"FooCorp\FooTool"], plans["FooTool 2.1"].MachineRegistryKeys);
        Assert.Equal(AppMethod.Manual, plans["Some Agent"].Method);
        Assert.Contains("service", plans["Some Agent"].Reason);
        Assert.Equal(AppMethod.Manual, plans["Contoso Payroll"].Method);
        Assert.Equal(AppMethod.Manual, plans["Realtek High Definition Audio Driver"].Method);
        Assert.Equal(AppMethod.Manual, plans["Microsoft 365 - en-us"].Method);
        Assert.Contains("Steam", plans["Bloons TD 6"].Reason);
        Assert.DoesNotContain(plans.Keys, k => k.Contains("Redistributable"));
    }

    [Fact]
    public void Plan_FindsSettingsFolders()
    {
        using var pc = new FakePc("old", "tylor");
        pc.WriteFile("tylor", "AppData/Roaming/Spotify/prefs", "x");
        pc.WriteFile("tylor", "AppData/Local/Spotify/Storage/data", "x");
        pc.WriteFile("tylor", "AppData/Roaming/Microsoft/Spotify/nope", "x");
        pc.Inventory.Apps.Add(App("Spotify", publisher: "Spotify AB"));
        var platform = new FakeApps { Winget = [new WingetRow("Spotify", "Spotify.Spotify", "1.2", "winget")] };
        platform.UserKeys["tylor"] = ["Spotify", "Microsoft", @"Microsoft\Windows"];

        var plan = AppPlanner.Plan(pc.Inventory, platform, pc.Inventory.Users, new CaptureRules()).Single();

        Assert.Equal(["AppData/Local/Spotify", "AppData/Roaming/Spotify"], plan.SettingsFolders);
        Assert.Equal(["Spotify"], plan.UserRegistryKeys);
    }

    [Fact]
    public async Task CaptureAndApply_ReinstallsAndCopiesPrograms()
    {
        using var oldPc = new FakePc("old", "tylor");
        using var newPc = new FakePc("new", "tylor");
        using var store = new TempDir();
        oldPc.WriteFile("tylor", "AppData/Roaming/Spotify/prefs", "volume=80");
        // A per-user program, the kind that installs under AppData\Local\Programs.
        oldPc.WriteFile("tylor", "AppData/Local/Programs/FooTool/foo.exe", "MZ fake exe");
        oldPc.WriteFile("tylor", "AppData/Local/Programs/FooTool/lib/core.dll", "dll");
        oldPc.Inventory.Apps.AddRange(
        [
            App("Spotify", publisher: "Spotify AB"),
            App("FooTool", Path.Combine(oldPc.Profile("tylor"), "AppData", "Local", "Programs", "FooTool"), scope: "tylor", publisher: "FooCorp"),
            App("Realtek Audio Driver"),
        ]);
        var oldPlatform = new FakeApps { Winget = [new WingetRow("Spotify", "Spotify.Spotify", "1.2", "winget")] };
        oldPlatform.UserKeys["tylor"] = ["Spotify"];
        var plans = AppPlanner.Plan(oldPc.Inventory, oldPlatform, oldPc.Inventory.Users, new CaptureRules());
        Assert.Equal(3, plans.Count);

        var capture = Options.Capture();
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog(), oldPlatform).RunAsync(sink, new CaptureOptions
            {
                UseShadowCopy = false, IncludeWifi = false, IncludeUserSettings = false, Apps = plans,
            });

        var manifest = FolderSink.ReadManifest(store.Path);
        Assert.Equal(3, manifest.Apps.Count);
        var foo = manifest.Apps.Single(a => a.Name == "FooTool");
        Assert.NotNull(foo.ProgramStorePath);

        var newPlatform = new FakeApps();
        var result = await new ApplyEngine(newPc, new ListLog(), newPlatform).RunAsync(store.Path, new ApplyOptions());

        Assert.Equal(["Spotify.Spotify"], newPlatform.Installed);
        Assert.Equal("volume=80", File.ReadAllText(Path.Combine(newPc.Profile("tylor"), "AppData", "Roaming", "Spotify", "prefs")));
        Assert.Equal("MZ fake exe", File.ReadAllText(Path.Combine(newPc.Profile("tylor"), "AppData", "Local", "Programs", "FooTool", "foo.exe")));
        Assert.True(File.Exists(Path.Combine(newPc.Profile("tylor"), "AppData", "Local", "Programs", "FooTool", "lib", "core.dll")));
        var spotifyReg = Assert.Single(newPlatform.ImportedForUser);
        Assert.Contains($@"HKEY_USERS\{newPc.Inventory.Users[0].Sid}\Software\Spotify", spotifyReg.Content);

        var outcomes = result.Apps.ToDictionary(a => a.Name, a => a.Outcome);
        Assert.Equal(AppOutcome.Installed, outcomes["Spotify"]);
        Assert.Equal(AppOutcome.Copied, outcomes["FooTool"]);
        Assert.Equal(AppOutcome.Manual, outcomes["Realtek Audio Driver"]);
        Assert.Equal(["Realtek Audio Driver"], result.AppsToReinstall.Select(a => a.Name));
    }

    [Fact]
    public async Task Apply_SkipsInstallWhenAlreadyThere_AndHonoursSelection()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        using var store = new TempDir();
        oldPc.Inventory.Apps.AddRange([App("Spotify"), App("Discord")]);
        newPc.Inventory.Apps.Add(App("Spotify 1.2.50"));
        var platform = new FakeApps { Winget = [new WingetRow("Spotify", "Spotify.Spotify", "1", "winget"), new WingetRow("Discord", "Discord.Discord", "1", "winget")] };
        var plans = AppPlanner.Plan(oldPc.Inventory, platform, oldPc.Inventory.Users, new CaptureRules());
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog(), platform).RunAsync(sink, new CaptureOptions { UseShadowCopy = false, IncludeWifi = false, IncludeUserSettings = false, Apps = plans });

        var newPlatform = new FakeApps();
        var spotifyKey = plans.Single(p => p.Name == "Spotify").Key;
        var result = await new ApplyEngine(newPc, new ListLog(), newPlatform).RunAsync(store.Path, new ApplyOptions { AppKeys = new HashSet<string> { spotifyKey } });

        Assert.Empty(newPlatform.Installed);
        var outcomes = result.Apps.ToDictionary(a => a.Name, a => a.Outcome);
        Assert.Equal(AppOutcome.AlreadyInstalled, outcomes["Spotify"]);
        Assert.Equal(AppOutcome.NotSelected, outcomes["Discord"]);
    }

    [Fact]
    public async Task FailedInstall_IsReported()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        using var store = new TempDir();
        oldPc.Inventory.Apps.Add(App("Discord"));
        var platform = new FakeApps { Winget = [new WingetRow("Discord", "Discord.Discord", "1", "winget")] };
        var plans = AppPlanner.Plan(oldPc.Inventory, platform, oldPc.Inventory.Users, new CaptureRules());
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog(), platform).RunAsync(sink, new CaptureOptions { UseShadowCopy = false, IncludeWifi = false, IncludeUserSettings = false, Apps = plans });

        var log = new ListLog();
        var result = await new ApplyEngine(newPc, log, new FakeApps { FailInstalls = true }).RunAsync(store.Path, new ApplyOptions());

        var discord = Assert.Single(result.Apps);
        Assert.Equal(AppOutcome.Failed, discord.Outcome);
        Assert.Contains(log.Warnings, w => w.Contains("Discord"));
        Assert.Contains(result.AppsToReinstall, a => a.Name == "Discord");
    }
}
