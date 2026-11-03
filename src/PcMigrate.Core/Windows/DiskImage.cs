using System.Management;
using System.Runtime.Versioning;
using PcMigrate.Core.Clone;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Whole-drive images, kept as a Windows virtual disk file (.vhdx).
/// <para>
/// Making one: an empty, growable .vhdx the size of the drive is created and attached, and the
/// drive is cloned into it. Only the space files use is written, so the file is about the size of
/// the data, not the drive. Because it's an ordinary virtual disk, double-clicking it in File
/// Explorer opens it like a USB drive, so single files can be pulled out of it at any time.
/// </para>
/// <para>
/// Restoring: the image is attached read-only and cloned onto the chosen drive, with all the
/// same steps as a drive-to-drive clone (new partition table, growing to fill a bigger drive,
/// fresh boot files).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskImage(IMigrationLog log)
{
    public const string Extension = ".vhdx";

    public sealed record ImageResult(string ImagePath, long BytesCopied, long FileSize, TimeSpan Elapsed, List<string> Notes);

    /// <summary>A suggested file name, e.g. "DESKTOP-ABC disk 0 2026-11-03.vhdx".</summary>
    public static string SuggestName(DiskInfo disk) =>
        $"{Environment.MachineName} disk {disk.Number} {DateTime.Now:yyyy-MM-dd}{Extension}";

    /// <summary>Why an image of this drive can't be written to this file, or null if it can.</summary>
    public static string? CheckCreate(DiskInfo source, string imagePath)
    {
        if (!imagePath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return $"The image file name has to end in {Extension}.";
        if (File.Exists(imagePath)) return "A file with that name is already there. Pick another name.";
        var root = Path.GetPathRoot(Path.GetFullPath(imagePath));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return "Save the image to a drive plugged into this PC (not a network folder).";
        var letter = root[..1];
        if (source.Partitions.Any(p => string.Equals(p.DriveLetter, letter, StringComparison.OrdinalIgnoreCase)))
            return $"{letter}: is on the drive being imaged. Save the image to a different drive.";
        var needed = NeededBytes(source);
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < needed)
            return $"{letter}: has {DiskInfo.FormatSize(free)} free, and the image needs about {DiskInfo.FormatSize(needed)}.";
        return null;
    }

    /// <summary>About how big the image file will be.</summary>
    public static long NeededBytes(DiskInfo source) => ImagePlanner.NeededBytes(source);

    public async Task<ImageResult> CreateAsync(DiskInfo source, string imagePath, CancellationToken ct)
    {
        imagePath = Path.GetFullPath(imagePath);
        if (CheckCreate(source, imagePath) is { } problem) throw new InvalidOperationException(problem);
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);

        var megabytes = (source.Size + 1024 * 1024 - 1) / (1024 * 1024);
        log.Info($"Creating the image file {imagePath}...");
        Diskpart.Run($"create vdisk file=\"{imagePath}\" maximum={megabytes} type=expandable");
        DiskCloner.Result? result = null;
        try
        {
            Diskpart.Run($"select vdisk file=\"{imagePath}\"", "attach vdisk");
            var target = await FindAttachedAsync(imagePath, ct);
            if (target.SectorSize != source.SectorSize)
                throw new InvalidOperationException($"This drive uses {source.SectorSize}-byte sectors, which Windows' image files don't support. Clone it to another drive instead.");
            var plan = ClonePlanner.Plan(source, target);
            if (!plan.CanRun) throw new InvalidOperationException(plan.Problem);
            result = await new DiskCloner(log).RunAsync(plan, ct);
        }
        finally
        {
            Detach(imagePath);
            // Half an image is no use and could be mistaken for a good one.
            if (result is null) TryDelete(imagePath);
        }

        var notes = result.Notes.Where(n => !n.Contains("reagentc", StringComparison.OrdinalIgnoreCase)).ToList();
        notes.Add("Double-click the image file in File Explorer to open it and copy single files out.");
        return new ImageResult(imagePath, result.BytesCopied, new FileInfo(imagePath).Length, result.Elapsed, notes);
    }

    /// <summary>Describes an image without changing it: the drive it was made from, as it was.</summary>
    public async Task<DiskInfo> ReadAsync(string imagePath, CancellationToken ct)
    {
        imagePath = CheckImage(imagePath);
        Diskpart.Run($"select vdisk file=\"{imagePath}\"", "attach vdisk readonly");
        try { return await FindAttachedAsync(imagePath, ct); }
        finally { Detach(imagePath); }
    }

    /// <summary>The plan for putting an image onto a drive. The image is attached while planning.</summary>
    public async Task<ClonePlan> PlanRestoreAsync(string imagePath, int targetDisk, CancellationToken ct)
    {
        var image = await ReadAsync(imagePath, ct);
        return ImagePlanner.PlanRestore(image, Disks.List().FirstOrDefault(d => d.Number == targetDisk)
            ?? throw new InvalidOperationException("Pick the drive to restore to."));
    }

    public async Task<DiskCloner.Result> RestoreAsync(string imagePath, int targetDisk, CancellationToken ct)
    {
        imagePath = CheckImage(imagePath);
        Diskpart.Run($"select vdisk file=\"{imagePath}\"", "attach vdisk readonly");
        try
        {
            var image = await FindAttachedAsync(imagePath, ct);
            var target = Disks.List().FirstOrDefault(d => d.Number == targetDisk)
                ?? throw new InvalidOperationException("The drive to restore to isn't there any more. Pick it again.");
            var plan = ImagePlanner.PlanRestore(image, target);
            if (!plan.CanRun) throw new InvalidOperationException(plan.Problem);
            log.Info($"Restoring {Path.GetFileName(imagePath)} onto {target.Describe()}...");
            // The image is read-only and nothing else uses it, so no snapshot is needed.
            return await new DiskCloner(log).RunAsync(plan, ct, useSnapshots: false);
        }
        finally
        {
            Detach(imagePath);
        }
    }

    private static string CheckImage(string imagePath)
    {
        var full = Path.GetFullPath(imagePath);
        if (!File.Exists(full)) throw new FileNotFoundException("The image file isn't there.", full);
        if (!full.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pick a .vhdx image file.");
        return full;
    }

    /// <summary>Waits for Windows to list the attached virtual disk, and returns it.</summary>
    private static async Task<DiskInfo> FindAttachedAsync(string imagePath, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (DiskNumberOf(imagePath) is { } number && Disks.List().FirstOrDefault(d => d.Number == number) is { } disk) return disk;
            await Task.Delay(500, ct);
        }
        throw new IOException("Windows attached the image but didn't list it as a drive. Try again.");
    }

    private static int? DiskNumberOf(string imagePath)
    {
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT Number, Location FROM MSFT_Disk");
        foreach (var disk in searcher.Get().OfType<ManagementObject>())
        {
            using (disk)
            {
                if (string.Equals((disk["Location"] as string)?.Trim(), imagePath, StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(disk["Number"]);
            }
        }
        return null;
    }

    private void Detach(string imagePath)
    {
        try { Diskpart.Run($"select vdisk file=\"{imagePath}\"", "detach vdisk noerr"); }
        catch (IOException ex) { log.Warn($"Couldn't detach the image ({ex.Message}). It detaches when the PC restarts."); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
