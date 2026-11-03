using System.Runtime.Versioning;
using PcMigrate.Core.Clone;
using PcMigrate.Core.Rescue;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Makes a USB stick that starts any PC into pc-migrate, for PCs whose Windows won't start.
/// It's built from this PC's own Windows recovery environment (the blue "Choose an option"
/// screens), so nothing extra has to be downloaded from Microsoft:
/// <list type="number">
/// <item>The stick is erased and split into a small FAT32 boot part and, on bigger sticks, an NTFS part to save backups to.</item>
/// <item>A copy of the recovery image (winre.wim) is changed to open pc-migrate's menu instead of the recovery menu.</item>
/// <item>Windows' signed boot manager and a boot menu that starts that image are put on the boot part, so Secure Boot is happy.</item>
/// <item>pcmigrate-cli.exe (and the app, for use on working PCs) is copied on.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RescueUsbMaker(IMigrationLog log)
{
    public sealed record Result(string BootDrive, string? DataDrive, List<string> Notes);

    /// <summary>
    /// The pc-migrate files to put on the stick: the command-line tool (the only part that runs in
    /// the rescue system, which has no web view for the app) and this exe. The command-line tool is
    /// taken from beside this exe, or downloaded from the newest release.
    /// </summary>
    public static async Task<List<string>> ToolFilesAsync(string runningExe, string downloadFolder, IMigrationLog log, CancellationToken ct)
    {
        var files = new List<string> { runningExe };
        if (Path.GetFileName(runningExe).Equals(RescueMedia.CliName, StringComparison.OrdinalIgnoreCase)) return files;
        var beside = Path.Combine(Path.GetDirectoryName(runningExe)!, RescueMedia.CliName);
        if (File.Exists(beside)) return [beside, runningExe];

        log.Info("Downloading pcmigrate-cli.exe from the newest release...");
        var release = await Update.Updater.CheckAsync(RescueMedia.CliName, new Version(0, 0, 0, 0), ct)
            ?? throw new InvalidOperationException($"Couldn't download {RescueMedia.CliName} (no internet?). Put it in the same folder as pc-migrate.exe and try again.");
        Directory.CreateDirectory(downloadFolder);
        var target = Path.Combine(downloadFolder, RescueMedia.CliName);
        var downloaded = await Update.Updater.DownloadAsync(release, target, null, ct);
        File.Move(downloaded, target, overwrite: true);
        return [target, runningExe];
    }

    /// <summary>Why this disk can't become a rescue USB, or null if it can.</summary>
    public static string? Check(DiskInfo disk)
    {
        if (disk.IsSystem) return "This PC runs from that drive.";
        if (disk.Bus is not ("USB" or "SD card")) return "Only USB sticks and USB drives can be made into a rescue USB.";
        if (disk.Size < RescueMedia.MinBytes) return "The stick is too small; it needs at least 1 GB.";
        return null;
    }

    /// <param name="toolFiles">pcmigrate-cli.exe and any other files to put in the stick's pc-migrate folder.</param>
    public async Task<Result> MakeAsync(DiskInfo disk, IReadOnlyList<string> toolFiles, CancellationToken ct)
    {
        if (Check(disk) is { } problem) throw new InvalidOperationException(problem);
        var again = Disks.List().FirstOrDefault(d => d.Number == disk.Number);
        if (again is null || again.Size != disk.Size || again.Model != disk.Model)
            throw new InvalidOperationException("The USB stick changed since it was picked (was it unplugged?). Pick it again.");

        var notes = new List<string>();
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pcmigrate-rescue-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            log.Info("Finding this PC's Windows recovery environment...");
            var wim = Path.Combine(work, "boot.wim");
            CopyRecoveryImage(wim);
            ct.ThrowIfCancellationRequested();

            log.Info("Setting it up to open pc-migrate...");
            await CustomiseAsync(wim, Path.Combine(work, "mount"), ct);

            var letters = FreeLetters();
            var withData = disk.Size >= RescueMedia.MinBytesForDataPartition;
            var boot = letters[0] + @":\";
            var data = withData ? letters[1] + @":\" : null;
            log.Info($"Erasing {disk.Describe()} and setting it up...");
            Format(disk.Number, letters[0], withData ? letters[1] : null);

            log.Info("Copying the rescue system to the stick...");
            Directory.CreateDirectory(Path.Combine(boot, "sources"));
            await CopyAsync(wim, Path.Combine(boot, "sources", "boot.wim"), ct);
            WriteBootFiles(boot, work);

            log.Info("Copying pc-migrate to the stick...");
            var toolDir = Directory.CreateDirectory(Path.Combine(data ?? boot, RescueMedia.ToolFolder)).FullName;
            foreach (var file in toolFiles)
                await CopyAsync(file, Path.Combine(toolDir, Path.GetFileName(file)), ct);
            File.WriteAllText(Path.Combine(boot, "README.txt"), RescueMedia.ReadMe);
            if (data is not null) File.WriteAllText(Path.Combine(data, "README.txt"), RescueMedia.ReadMe);

            notes.Add("To use it: plug it into the PC, turn it on while tapping the boot menu key (F12 on most PCs, F9 on HP) and pick the USB stick.");
            if (data is null) notes.Add("The stick is small, so there's no room on it for backups. Save to another USB drive.");
            return new Result(boot, data, notes);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The recovery image lives on a hidden partition while it's switched on, or in System32\Recovery while it's off.</summary>
    private void CopyRecoveryImage(string destination)
    {
        var candidates = new List<string>();
        try
        {
            if (RescueMedia.RecoveryFolder(ProcessRunner.Run("reagentc.exe", "/info").Output) is { } folder)
                candidates.Add(Path.Combine(folder, "Winre.wim"));
        }
        catch (System.ComponentModel.Win32Exception) { }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\Recovery\Winre.wim"));

        foreach (var source in candidates)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                File.SetAttributes(destination, FileAttributes.Normal);
                log.Info($"Using the recovery image from {source}.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        throw new InvalidOperationException("This PC's Windows has no recovery environment to build the USB from (it may have been removed). Make the USB on another Windows 10 or 11 PC.");
    }

    /// <summary>Opens the image with Windows' DISM, adds pc-migrate's start-up files, and saves it.</summary>
    private async Task CustomiseAsync(string wim, string mount, CancellationToken ct)
    {
        Directory.CreateDirectory(mount);
        var opened = await ProcessRunner.RunAsync("dism.exe", ct, "/Mount-Image", $"/ImageFile:{wim}", "/Index:1", $"/MountDir:{mount}");
        if (opened.ExitCode != 0) throw new IOException($"Couldn't open the recovery image: {Last(opened.Output)}");
        var commit = false;
        try
        {
            var system32 = Path.Combine(mount, "Windows", "System32");
            File.WriteAllText(Path.Combine(system32, "winpeshl.ini"), RescueMedia.WinpeshlIni);
            File.WriteAllText(Path.Combine(system32, RescueMedia.RescueScript), RescueMedia.StartScript);
            commit = true;
        }
        finally
        {
            var closed = await ProcessRunner.RunAsync("dism.exe", CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", commit ? "/Commit" : "/Discard");
            if (commit && closed.ExitCode != 0) throw new IOException($"Couldn't save the recovery image: {Last(closed.Output)}");
        }
    }

    private static void Format(int disk, char bootLetter, char? dataLetter)
    {
        var script = new List<string>
        {
            $"select disk {disk}", "attributes disk clear readonly", "online disk noerr", "clean", "convert mbr",
            dataLetter is null ? "create partition primary" : $"create partition primary size={RescueMedia.BootPartitionMb}",
            $"format fs=fat32 quick label={RescueMedia.BootLabel}", "active", $"assign letter={bootLetter}",
        };
        if (dataLetter is { } d)
            script.AddRange(["create partition primary", $"format fs=ntfs quick label={RescueMedia.DataLabel}", $"assign letter={d}"]);
        Diskpart.Run([.. script]);
    }

    /// <summary>Windows' own signed boot manager, the RAM disk file it needs, and a boot menu.</summary>
    private void WriteBootFiles(string boot, string work)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var efiBoot = Directory.CreateDirectory(Path.Combine(boot, "EFI", "Boot")).FullName;
        File.Copy(Path.Combine(windows, @"Boot\EFI\bootmgfw.efi"), Path.Combine(efiBoot, "bootx64.efi"), overwrite: true);
        var sdi = Directory.CreateDirectory(Path.Combine(boot, "boot")).FullName;
        File.Copy(Path.Combine(windows, @"Boot\DVD\EFI\boot.sdi"), Path.Combine(sdi, "boot.sdi"), overwrite: true);

        var store = Path.Combine(work, "BCD");
        Bcd("/createstore", store);
        Bcd("/store", store, "/create", "{bootmgr}", "/d", "pc-migrate rescue");
        Bcd("/store", store, "/create", "{ramdiskoptions}", "/d", "pc-migrate rescue");
        var id = RescueMedia.NewEntryId(Bcd("/store", store, "/create", "/d", "pc-migrate rescue", "/application", "osloader"))
            ?? throw new IOException("Windows' boot tool didn't say which boot entry it made.");
        foreach (var command in RescueMedia.BootEntryCommands(store))
            Bcd([.. command.Select(a => a.Replace("{id}", id))]);

        var bcdFolder = Directory.CreateDirectory(Path.Combine(boot, "EFI", "Microsoft", "Boot")).FullName;
        File.Copy(store, Path.Combine(bcdFolder, "BCD"), overwrite: true);
    }

    private static string Bcd(params string[] args)
    {
        var result = ProcessRunner.Run("bcdedit.exe", args);
        if (result.ExitCode != 0) throw new IOException($"Windows' boot tool reported a problem: {Last(result.Output)}");
        return result.Output;
    }

    private static async Task CopyAsync(string from, string to, CancellationToken ct)
    {
        await using var input = File.OpenRead(from);
        await using var output = File.Create(to);
        await input.CopyToAsync(output, ct);
    }

    private static string Last(string output) =>
        string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(2));

    private static List<char> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return "RSTUVWQPONMLKJIHG".Where(c => !used.Contains(c)).ToList();
    }
}
