using PcMigrate.Core.Apply;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Keys;
using PcMigrate.Core.Models;
using PcMigrate.Core.Report;
using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class ClientReportTests
{
    [Fact]
    public async Task MoveReport_HasJobPeopleProgramsAndToDo()
    {
        using var oldPc = new FakePc("RLAVE-OLD", "rlave");
        using var newPc = new FakePc("new", "rlave");
        using var store = new TempDir();
        oldPc.WriteFile("rlave", "Documents/taxes.pdf", "pdf");
        oldPc.WriteFile("rlave", "Pictures/cat.jpg", "jpg");
        oldPc.Inventory.Apps.AddRange(
        [
            new InstalledApp { Name = "Spotify", Scope = "Machine", RegistryKey = "k1" },
            new InstalledApp { Name = "Realtek Audio Driver", Scope = "Machine", RegistryKey = "k2" },
        ]);
        var platform = new FakeApps { Winget = [new WingetRow("Spotify", "Spotify.Spotify", "1", "winget")] };
        var plans = AppPlanner.Plan(oldPc.Inventory, platform, oldPc.Inventory.Users, new CaptureRules());
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog(), platform).RunAsync(sink, new CaptureOptions
            {
                UseShadowCopy = false, IncludeWifi = false, IncludeUserSettings = false, IncludeLicenseKeys = false, Apps = plans,
                Job = new JobInfo { Client = "Rachel <Lave>", JobNumber = "2611-03" },
            });

        var tech = new TechnicianSettings { Enabled = true, Company = "Faber PC Repair", Technician = "Tylor", Contact = "(555) 010-2030" };
        var result = await new ApplyEngine(newPc, new ListLog(), new FakeApps()).RunAsync(store.Path, new ApplyOptions { Technician = tech });

        Assert.NotNull(result.ClientReportPath);
        Assert.Contains("Rachel Lave", Path.GetFileName(result.ClientReportPath));
        var html = File.ReadAllText(result.ClientReportPath!);
        Assert.Contains("PC move report", html);
        Assert.Contains("Rachel &lt;Lave&gt;", html); // escaped
        Assert.Contains("2611-03", html);
        Assert.Contains("Faber PC Repair", html);
        Assert.Contains("RLAVE-OLD", html);
        Assert.Contains("<td>rlave</td>", html);
        Assert.Contains("Spotify", html);
        Assert.Contains("To install by hand", html);
        Assert.Contains("Realtek Audio Driver", html);
        Assert.DoesNotContain("<script", html);
        if (Environment.GetEnvironmentVariable("PCM_REPORT_OUT") is { } outPath) File.Copy(result.ClientReportPath!, outPath, overwrite: true);
    }

    [Fact]
    public void KeysAreOnlyCountedUnlessAskedFor()
    {
        var manifest = new Manifest
        {
            Inventory = new Inventory { Machine = new MachineInfo { ComputerName = "PC", OsDescription = "Windows 11" } },
            LicenseKeys = [new LicenseKey("Windows 11 Pro", "VK7JG-NPHTM-C97JM-9MPGT-3V66T", null, "registry")],
            Job = new JobInfo { Client = "A" },
        };
        var counted = ClientReport.Build(new ClientReport.Input { Manifest = manifest, Technician = new TechnicianSettings { Enabled = true }, SavedTo = @"E:\Backups" });
        Assert.DoesNotContain("3V66T", counted);
        Assert.Contains("1 product key(s)", counted);
        Assert.Contains("Data backup report", counted);
        var shown = ClientReport.Build(new ClientReport.Input { Manifest = manifest, Technician = new TechnicianSettings { Enabled = true, ShowKeys = true } });
        Assert.Contains("VK7JG-NPHTM-C97JM-9MPGT-3V66T", shown);
    }

    [Fact]
    public async Task NoReportWhenTechnicianModeIsOff()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        using var store = new TempDir();
        oldPc.WriteFile("a", "Documents/x.txt", "x");
        await using (var sink = new FolderSink(store.Path))
            await new CaptureEngine(oldPc, new ListLog()).RunAsync(sink, Options.Capture());
        var result = await new ApplyEngine(newPc, new ListLog()).RunAsync(store.Path, new ApplyOptions { Technician = new TechnicianSettings { Enabled = false } });
        Assert.Null(result.ClientReportPath);
    }
}
