using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class WholeProfileTests
{
    [Fact]
    public void CopiesEveryTopFolderButAppDataAndToolCaches()
    {
        using var pc = new FakePc("old", "a");
        foreach (var f in new[] { "Documents/a.txt", "OneDrive/Work/b.docx", "Dropbox/c.pdf", "AppData/Local/Temp/x.tmp", ".nuget/packages/p.nupkg", "Clients/d.xlsx" })
            pc.WriteFile("a", f, "1");
        Directory.CreateSymbolicLink(Path.Combine(pc.Profile("a"), "My Documents"), Path.Combine(pc.Profile("a"), "Documents"));

        var folders = new CaptureRules().FoldersFor(pc.Profile("a"));

        Assert.Contains("OneDrive", folders);
        Assert.Contains("Dropbox", folders);
        Assert.Contains("Clients", folders);
        Assert.DoesNotContain("AppData", folders);
        Assert.DoesNotContain(".nuget", folders);
        Assert.DoesNotContain("My Documents", folders);
        Assert.Contains("AppData/Local/Microsoft/Outlook", folders);
        Assert.Equal(folders.Count, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void OnlyTheListedFoldersWhenWholeProfileIsOff()
    {
        using var pc = new FakePc("old", "a");
        pc.WriteFile("a", "Dropbox/c.pdf", "1");
        Assert.DoesNotContain("Dropbox", new CaptureRules { WholeProfile = false }.FoldersFor(pc.Profile("a")));
    }

    [Theory]
    [InlineData("mail.pst", false)]
    [InlineData("me@work.com.ost", true)]
    public void OutlookCachesAreLeftOut(string name, bool excluded) => Assert.Equal(excluded, new CaptureRules().IsFileExcluded(name));

    [Fact]
    public async Task ExtraProfileFoldersRoundTrip()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        oldPc.WriteFile("a", "OneDrive/Work/plan.docx", "plan");
        oldPc.WriteFile("a", "AppData/Local/Microsoft/Outlook/archive.pst", "mail");
        oldPc.WriteFile("a", "AppData/Local/Microsoft/Outlook/cache.ost", "cache");
        oldPc.WriteFile("a", "AppData/Local/Temp/junk.bin", "junk");
        using var store = new TempDir();
        var log = new ListLog();
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, log).RunAsync(sink, Options.Capture("a"));
        await new ApplyEngine(newPc, log).RunAsync(store.Path, new ApplyOptions());

        Assert.True(File.Exists(Path.Combine(newPc.Profile("a"), "OneDrive", "Work", "plan.docx")));
        Assert.True(File.Exists(Path.Combine(newPc.Profile("a"), "AppData", "Local", "Microsoft", "Outlook", "archive.pst")));
        Assert.False(File.Exists(Path.Combine(newPc.Profile("a"), "AppData", "Local", "Microsoft", "Outlook", "cache.ost")));
        Assert.False(File.Exists(Path.Combine(newPc.Profile("a"), "AppData", "Local", "Temp", "junk.bin")));
    }

    [Theory]
    [InlineData("defaultuser0", true)]
    [InlineData("defaultuser100000", true)]
    [InlineData("DefaultUser100001", true)]
    [InlineData("WDAGUtilityAccount", true)]
    [InlineData("rlave", false)]
    [InlineData("defaultuser", false)]
    public void WindowsSetupAccountsAreNotPeople(string name, bool setup) => Assert.Equal(setup, UserProfileInfo.IsSetupAccount(name));

    [Theory]
    [InlineData("Dell SupportAssist", "Dell Inc.")]
    [InlineData("Peripheral Manager", "Dell Inc.")]
    [InlineData("HP Smart", "HP Inc.")]
    [InlineData("Lenovo Vantage Service", "Lenovo")]
    public void PcMakerToolsAreDoneByHandWithTheRightReason(string name, string publisher)
    {
        using var pc = new FakePc("OLD", "tylor");
        var folder = Directory.CreateDirectory(Path.Combine(pc.Root, "Program Files", "X")).FullName;
        pc.Inventory.Apps.Add(new InstalledApp { Name = name, Publisher = publisher, Scope = "Machine", RegistryKey = @"HKLM\x\" + name, InstallLocation = folder });
        var plan = AppPlanner.Plan(pc.Inventory, new FakeApps { Winget = [] }, pc.Inventory.Users, new CaptureRules()).Single();
        Assert.Equal(AppMethod.Manual, plan.Method);
        Assert.Contains("maker", plan.Reason);
    }
}
