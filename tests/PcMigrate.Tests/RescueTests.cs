using PcMigrate.Core.Rescue;

namespace PcMigrate.Tests;

public class RescueMediaTests
{
    [Fact]
    public void FindsRecoveryFolderInReagentcOutput()
    {
        const string info = """

            Windows Recovery Environment (Windows RE) and system reset configuration
            Information:

                Windows RE status:         Enabled
                Windows RE location:       \\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE
                Boot Configuration Data (BCD) identifier: 7d8d5e2a-1c2b-11ef-9f1a-c8d9d2a1b3c4

            REAGENTC.EXE: Operation Successful.
            """;
        Assert.Equal(@"\\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE", RescueMedia.RecoveryFolder(info));
        Assert.Null(RescueMedia.RecoveryFolder("Windows RE status: Disabled\r\nWindows RE location:\r\n"));
    }

    [Fact]
    public void ReadsNewBootEntryId() =>
        Assert.Equal("{0b4c1e70-6b2d-11ef-9f1a-c8d9d2a1b3c4}", RescueMedia.NewEntryId("The entry {0b4c1e70-6b2d-11ef-9f1a-c8d9d2a1b3c4} was successfully created."));

    [Fact]
    public void BootMenuStartsTheImageFromARamDisk()
    {
        var commands = RescueMedia.BootEntryCommands(@"C:\t\BCD").Select(c => string.Join(' ', c)).ToList();
        Assert.All(commands, c => Assert.StartsWith(@"/store C:\t\BCD ", c));
        Assert.Contains(commands, c => c.EndsWith(@"osdevice ramdisk=[boot]\sources\boot.wim,{ramdiskoptions}"));
        Assert.Contains(commands, c => c.EndsWith(@"ramdisksdipath \boot\boot.sdi"));
        Assert.Contains(commands, c => c.EndsWith("winpe yes"));
        Assert.Contains(commands, c => c.EndsWith("/default {id}"));
    }

    [Fact]
    public void StartScriptLooksForTheToolOnEveryLetter()
    {
        Assert.Contains(@"%%d:\pc-migrate\pcmigrate-cli.exe", RescueMedia.StartScript);
        Assert.Contains("wpeinit", RescueMedia.StartScript);
        Assert.Contains(RescueMedia.RescueScript, RescueMedia.WinpeshlIni);
        Assert.StartsWith("[LaunchApps]", RescueMedia.WinpeshlIni);
    }
}
