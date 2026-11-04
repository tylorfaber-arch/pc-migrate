using PcMigrate.Core.Apply;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class InstallerFinderTests
{
    [Theory]
    [InlineData("Foxit PDF Reader", "FoxitPDFReader20241_enu_Setup_Prom", true)]
    [InlineData("Foxit PDF Reader", "FoxitPhantomPDF", false)]
    [InlineData("QuickBooks Desktop 2024", "QuickBooksDesktop2024", true)]
    [InlineData("Notepad++ (64-bit x64)", "npp.8.6.Installer.x64", false)]
    [InlineData("Notepad++ (64-bit x64)", "notepad++ setup", true)]
    [InlineData("Zoom", "ZoomInstaller", false)] // one short word could be anything
    [InlineData("Audacity 3.4.2", "audacity-win-3.4.2-64bit", true)]
    [InlineData("TurboTax 2019", "Windows_PC_TurboTax_Download (1)", false)] // one downloader isn't every year's installer
    [InlineData("TurboTax 2024", "TurboTax 2024", true)]
    [InlineData("TurboTax 2023", "TurboTax 2024", false)]
    public void NameMatching(string program, string file, bool expected) =>
        Assert.Equal(expected, InstallerFinder.NameMatches(program, file));

    [Theory]
    [InlineData("\"C:\\ProgramData\\Package Cache\\{abc}\\VC_redist.x64.exe\"  /uninstall", "C:\\ProgramData\\Package Cache\\{abc}\\VC_redist.x64.exe")]
    [InlineData("C:\\ProgramData\\Package Cache\\{abc}\\setup.exe /uninstall", "C:\\ProgramData\\Package Cache\\{abc}\\setup.exe")]
    [InlineData("\"C:\\Program Files\\Foo\\uninst.exe\"", null)]
    [InlineData("MsiExec.exe /X{1234}", null)]
    public void PackageCacheFromUninstall(string uninstall, string? expected) =>
        Assert.Equal(expected, InstallerFinder.CachedFromUninstall(uninstall));

    [Fact]
    public void Find_PrefersBundleCacheThenMsiSourceThenDownloads()
    {
        using var dir = new TempDir();
        var bundle = Path.Combine(dir.Path, "bundle.exe");
        File.WriteAllText(bundle, "bundle");
        var source = Directory.CreateDirectory(Path.Combine(dir.Path, "src")).FullName;
        File.WriteAllText(Path.Combine(source, "contoso.msi"), "msi");
        var downloads = Directory.CreateDirectory(Path.Combine(dir.Path, "Downloads")).FullName;
        File.WriteAllText(Path.Combine(downloads, "Contoso_Payroll_Setup.exe"), "dl");

        InstalledApp App(string? bundlePath, string? installSource) => new()
        {
            Name = "Contoso Payroll", Scope = "Machine", RegistryKey = "k", IsMsi = true, BundleCachePath = bundlePath, InstallSource = installSource,
        };

        Assert.Equal(bundle, InstallerFinder.Find(App(bundle, source), p => p, [downloads]));
        Assert.Equal(Path.Combine(source, "contoso.msi"), InstallerFinder.Find(App(null, source), p => p, [downloads]));
        Assert.Equal(Path.Combine(downloads, "Contoso_Payroll_Setup.exe"), InstallerFinder.Find(App(null, null), p => p, [downloads]));
        Assert.Null(InstallerFinder.Find(App(null, null), p => p, [Path.Combine(dir.Path, "missing")]));
    }

    [Fact]
    public void Switches()
    {
        Assert.Equal(["/passive", "/norestart"], AppRestore.InstallerArgs(@"x\setup.msi", null));
        Assert.Equal(["/passive", "/norestart"], AppRestore.InstallerArgs(@"x\setup.exe", @"C:\ProgramData\Package Cache\{a}\setup.exe"));
        Assert.Empty(AppRestore.InstallerArgs(@"x\setup.exe", @"C:\Users\a\Downloads\setup.exe"));
    }
}

public class RuntimeTests
{
    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33130", "Microsoft.VCRedist.2015+.x64", "/install /quiet /norestart")]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.40664", "Microsoft.VCRedist.2013.x86", "/install /quiet /norestart")]
    [InlineData("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "Microsoft.VCRedist.2010.x64", "/q /norestart")]
    [InlineData("Microsoft Visual C++ 2005 Redistributable", "Microsoft.VCRedist.2005.x86", "/q")]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.11 (x64)", "Microsoft.DotNet.DesktopRuntime.8", "/install /quiet /norestart")]
    [InlineData("Microsoft .NET Runtime - 6.0.36 (x64)", "Microsoft.DotNet.Runtime.6", "/install /quiet /norestart")]
    [InlineData("Java 8 Update 431", "Oracle.JavaRuntimeEnvironment", "/s")]
    public void Recognised(string name, string id, string quiet)
    {
        var r = Runtimes.Match(name);
        Assert.NotNull(r);
        Assert.Equal(id, r.WingetId);
        Assert.Equal(quiet, r.QuietArgs);
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.38")] // part of the redistributable, not its own
    [InlineData("Google Chrome")]
    public void NotRuntimes(string name) => Assert.Null(Runtimes.Match(name));

    [Fact]
    public void On_ListsEachOnce()
    {
        InstalledApp A(string n) => new() { Name = n, Scope = "Machine", RegistryKey = n };
        var list = Runtimes.On([A("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38"), A("Microsoft Visual C++ 2019 Redistributable (x64) - 14.29"), A("Spotify")]);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", Assert.Single(list).WingetId);
    }
}

public class InstallerMoveTests
{
    private static InstalledApp App(string name, bool msi = false, string? location = null) => new()
    {
        Name = name, IsMsi = msi, Scope = "Machine", InstallLocation = location,
        RegistryKey = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
    };

    [Fact]
    public async Task SavedInstallerAndRuntimes_RunOnTheNewPc()
    {
        using var oldPc = new FakePc("old", "tylor");
        using var newPc = new FakePc("new", "tylor");
        using var store = new TempDir();
        oldPc.WriteFile("tylor", "Downloads/Contoso-Payroll-Setup-5.2.exe", "contoso installer");
        oldPc.Inventory.Apps.AddRange(
        [
            App("Contoso Payroll", msi: true),
            App("Unknown Thing", msi: true),
            App("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38"),
            App("Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.40664"),
        ]);
        var oldPlatform = new FakeApps();
        oldPlatform.Downloads["Microsoft.VCRedist.2013.x86"] = "vc2013 installer";

        var plans = AppPlanner.Plan(oldPc.Inventory, oldPlatform, oldPc.Inventory.Users, new CaptureRules());
        var contoso = plans.Single(p => p.Name == "Contoso Payroll");
        Assert.Equal(AppMethod.Installer, contoso.Method);
        Assert.Contains("Contoso-Payroll-Setup-5.2.exe", contoso.Reason);
        Assert.Equal(AppMethod.Manual, plans.Single(p => p.Name == "Unknown Thing").Method);

        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog(), oldPlatform).RunAsync(sink, new CaptureOptions
            {
                UseShadowCopy = false, IncludeWifi = false, IncludeUserSettings = false, IncludeLicenseKeys = false, Apps = plans,
            });

        var manifest = FolderSink.ReadManifest(store.Path);
        Assert.NotNull(manifest.Apps.Single(a => a.Name == "Contoso Payroll").InstallerStorePath);
        Assert.Equal(2, manifest.Runtimes.Count);
        Assert.NotNull(manifest.Runtimes.Single(r => r.WingetId == "Microsoft.VCRedist.2013.x86").InstallerStorePath);

        // The new PC already has the 2015+ runtime and no internet for the 2013 one.
        newPc.Inventory.Apps.Add(App("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40"));
        var newPlatform = new OfflineFakeApps();
        var result = await new ApplyEngine(newPc, new ListLog(), newPlatform).RunAsync(store.Path, new ApplyOptions());

        Assert.Equal([("vc2013 installer", "/install /quiet /norestart"), ("contoso installer", "")], newPlatform.RanInstallers);
        var outcomes = result.Apps.ToDictionary(a => a.Name, a => a.Outcome);
        Assert.Equal(AppOutcome.Installed, outcomes["Contoso Payroll"]);
        Assert.Equal(AppOutcome.Installed, outcomes["Visual C++ 2013 runtime (x86)"]);
        Assert.Equal(AppOutcome.Manual, outcomes["Unknown Thing"]);
        Assert.DoesNotContain(result.AppsToReinstall, a => a.Name == "Contoso Payroll");
    }

    /// <summary>A new PC where winget can't reach the internet.</summary>
    private sealed class OfflineFakeApps : IAppPlatform
    {
        private readonly FakeApps _inner = new();
        public List<(string Content, string Args)> RanInstallers => _inner.RanInstallers;
        public IReadOnlyList<WingetRow>? ListWinget(IReadOnlyList<InstalledApp> apps) => null;
        public bool HasServiceUnder(string folder) => false;
        public IReadOnlyList<string> UserSoftwareKeys(UserProfileInfo user) => [];
        public IReadOnlyList<string> MachineSoftwareKeys() => [];
        public string? ExportUserKey(UserProfileInfo user, string subKey, string file) => null;
        public bool ExportKey(string keyPath, string file) => false;
        public bool ImportUserFile(UserProfileInfo user, string file, string exportedRoot) => true;
        public bool ImportFile(string file) => true;
        public IReadOnlyList<string> SharedShortcutFolders() => [];
        public Task<(bool Ok, string Message)> WingetInstallAsync(string id, CancellationToken ct) => Task.FromResult((false, "no internet"));
        public Task<(bool Ok, string Message)> RunInstallerAsync(string file, IReadOnlyList<string> args, CancellationToken ct) => _inner.RunInstallerAsync(file, args, ct);
    }
}
