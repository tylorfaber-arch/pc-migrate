using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;

namespace PcMigrate.Tests;

public class PlannerRuleTests
{
    private static AppMethod MethodFor(string name, string? uninstall = null, string? location = null)
    {
        using var pc = new FakePc("OLD", "tylor");
        var folder = location ?? Directory.CreateDirectory(Path.Combine(pc.Root, "Program Files", "X")).FullName;
        pc.Inventory.Apps.Add(new InstalledApp { Name = name, Scope = "Machine", RegistryKey = @"HKLM\x\" + name, InstallLocation = folder, UninstallString = uninstall });
        var apps = new FakeApps { Winget = [] };
        return AppPlanner.Plan(pc.Inventory, apps, pc.Inventory.Users, new CaptureRules()).Single().Method;
    }

    [Theory]
    [InlineData("Microsoft OneNote - en-us")]
    [InlineData("Microsoft Outlook")]
    [InlineData("Microsoft SQL Server 2025 (64-bit)")]
    public void BigMicrosoftInstallsAreDoneByHand(string name) => Assert.Equal(AppMethod.Manual, MethodFor(name));

    [Fact]
    public void ClickToRunIsDoneByHand() =>
        Assert.Equal(AppMethod.Manual, MethodFor("Some Office Thing", @"""C:\Program Files\Common Files\Microsoft Shared\ClickToRun\OfficeClickToRun.exe"" scenario=install"));

    [Fact]
    public void VisualStudioInstallerAppsAreDoneByHand() =>
        Assert.Equal(AppMethod.Manual, MethodFor("SQL Server Management Studio 22", @"""C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe"" uninstall"));

    [Fact]
    public void SqlServerToolsAreNotTreatedAsTheServer() =>
        Assert.Equal(AppMethod.Copy, MethodFor("Microsoft SQL Server 2025 Setup (English)"));

    [Fact]
    public void ProgramFoldersSkipDesktopIni() =>
        Assert.True(new CaptureRules { ExcludedFolderNames = [], ExcludedFilePatterns = ["desktop.ini"] }.IsFileExcluded("DESKTOP.INI"));
}
