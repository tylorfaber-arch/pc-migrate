using System.Buffers.Binary;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PcMigrate.Core.Clone;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Copies a whole disk onto another so the copy starts up on its own. In order:
/// <list type="number">
/// <item>Snapshots the source's lettered NTFS volumes, so a running Windows gives a consistent copy.</item>
/// <item>Wipes the target and writes the planned partition table, with new IDs so both drives can be plugged in together.</item>
/// <item>Takes the target offline and copies each partition sector by sector (for NTFS, only the clusters in use).</item>
/// <item>Brings it back, grows the main file system if the drive is bigger, and rebuilds the boot files
/// with Windows' bcdboot so they point at the new drive's partitions.</item>
/// </list>
/// The source is only read. Every step is logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskCloner(IMigrationLog log)
{
    private const int ChunkSize = 4 * 1024 * 1024;

    public sealed record Result(long BytesCopied, TimeSpan Elapsed, List<string> Notes);

    public async Task<Result> RunAsync(ClonePlan plan, CancellationToken ct)
    {
        if (!plan.CanRun) throw new InvalidOperationException(plan.Problem);
        var started = DateTime.UtcNow;
        var notes = new List<string>();
        CheckStillTheSame(plan);

        var snapshots = new Dictionary<string, ShadowCopy>(StringComparer.OrdinalIgnoreCase);
        var automountWasOn = AutomountEnabled();
        var targetOffline = false;
        try
        {
            foreach (var p in plan.Partitions.Select(x => x.Source).Where(p => p.IsNtfs && p.DriveLetter is not null))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    log.Info($"Taking a snapshot of {p.DriveLetter}: so it copies consistently...");
                    snapshots[p.DriveLetter!] = ShadowCopy.Create(p.DriveLetter + @":\");
                }
                catch (Exception ex)
                {
                    log.Warn($"Snapshot of {p.DriveLetter}: failed, copying it live instead ({ex.Message}). Close other programs to be safe.");
                }
            }

            // Stop Windows from giving the new, still-empty partitions drive letters (and
            // offering to format them) while they're being filled.
            if (automountWasOn) ProcessRunner.Run("mountvol.exe", "/N");

            log.Info($"Erasing {plan.Target.Describe()} and writing the new partition table...");
            Diskpart($"select disk {plan.Target.Number}", "attributes disk clear readonly", "online disk noerr", "clean");
            var ids = WriteLayout(plan);

            Diskpart($"select disk {plan.Target.Number}", "offline disk");
            targetOffline = true;

            long done = 0;
            using (var target = RawDisk.Open(RawDisk.PhysicalDrive(plan.Target.Number), write: true))
            {
                var index = 0;
                foreach (var part in plan.Partitions)
                {
                    index++;
                    ct.ThrowIfCancellationRequested();
                    log.Info($"Copying partition {index} of {plan.Partitions.Count} ({Label(part.Source)})...");
                    snapshots.TryGetValue(part.Source.DriveLetter ?? "", out var snapshot);
                    done = await CopyPartitionAsync(plan, part, snapshot, target, done, ct);
                }
            }

            Diskpart($"select disk {plan.Target.Number}", "online disk");
            targetOffline = false;
            FinishTarget(plan, ids, notes);
            return new Result(done, DateTime.UtcNow - started, notes);
        }
        finally
        {
            if (targetOffline) Diskpart($"select disk {plan.Target.Number}", "online disk");
            if (automountWasOn) ProcessRunner.Run("mountvol.exe", "/E");
            foreach (var s in snapshots.Values) s.Dispose();
        }
    }

    /// <summary>USB drives get renumbered when replugged; make sure disk N is still the drive that was picked.</summary>
    private static void CheckStillTheSame(ClonePlan plan)
    {
        var now = Disks.List();
        foreach (var (wanted, what) in new[] { (plan.Source, "source"), (plan.Target, "target") })
        {
            var disk = now.FirstOrDefault(d => d.Number == wanted.Number);
            if (disk is null || disk.Size != wanted.Size || disk.Model != wanted.Model || disk.Serial != wanted.Serial)
                throw new InvalidOperationException($"The {what} drive changed since it was picked (was it unplugged?). Pick the drives again.");
            if (what == "target" && disk.IsSystem)
                throw new InvalidOperationException("The target is the drive this PC is running from. Nothing was changed.");
        }
    }

    private List<Guid> WriteLayout(ClonePlan plan)
    {
        using var target = RawDisk.Open(RawDisk.PhysicalDrive(plan.Target.Number), write: true);
        var gpt = plan.Source.IsGpt;
        var diskId = Guid.NewGuid();
        var signature = (uint)Random.Shared.Next(1, int.MaxValue);
        RawDisk.CreateDisk(target, gpt, diskId, signature);
        var blank = RawDisk.ReadLayout(target);
        var ids = plan.Partitions.Select(_ => Guid.NewGuid()).ToList();
        var entries = plan.Partitions.Select((p, i) => (p.Source, p.Offset, p.Length, ids[i])).ToList();
        var layout = blank with { DiskId = diskId, MbrSignature = signature };
        RawDisk.Ioctl(target, RawDisk.IoctlSetDriveLayoutEx, LayoutCodec.Encode(layout, entries, plan.Target.SectorSize), 0);
        RawDisk.Ioctl(target, RawDisk.IoctlUpdateProperties, null, 0);
        return ids;
    }

    private async Task<long> CopyPartitionAsync(ClonePlan plan, PlannedPartition part, ShadowCopy? snapshot, SafeFileHandle target, long done, CancellationToken ct)
    {
        var src = part.Source;
        // Read through the snapshot, else the mounted volume, else straight off the disk.
        var (path, baseOffset) = snapshot is not null ? (snapshot.DeviceObject, 0L)
            : src.VolumePath is not null ? (src.VolumePath, 0L)
            : (RawDisk.PhysicalDrive(plan.Source.Number), src.Offset);
        using var source = RawDisk.Open(path, write: false);
        var buffer = GC.AllocateUninitializedArray<byte>(ChunkSize);

        async Task Copy(long from, long length)
        {
            for (long pos = 0; pos < length; pos += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(ChunkSize, length - pos);
                var read = await RandomAccess.ReadAsync(source, buffer.AsMemory(0, n), baseOffset + from + pos, ct);
                if (read < n) Array.Clear(buffer, read, n - read); // past the end of a volume: fill with zeros
                await RandomAccess.WriteAsync(target, buffer.AsMemory(0, n), part.Offset + from + pos, ct);
                done += n;
                log.FileProgress(0, done, Label(src));
            }
        }

        if (src.IsNtfs && baseOffset == 0)
        {
            // Only the clusters NTFS has in use, plus the backup boot sector in the last sector.
            var boot = new byte[plan.Source.SectorSize];
            await RandomAccess.ReadAsync(source, boot, 0, ct);
            var cluster = ClusterSize(boot);
            foreach (var (first, count) in RawDisk.UsedClusterRuns(source))
            {
                var from = first * cluster;
                await Copy(from, Math.Min(count * cluster, src.Length - from));
            }
            var last = src.Length - plan.Source.SectorSize;
            await RandomAccess.ReadAsync(source, boot, last, ct);
            await RandomAccess.WriteAsync(target, boot, part.Offset + last, ct);
        }
        else
        {
            await Copy(0, src.Length);
        }
        return done;
    }

    internal static long ClusterSize(ReadOnlySpan<byte> bootSector)
    {
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[0x0B..]);
        var raw = bootSector[0x0D];
        // Values above 0x80 mean 2^(256 - value), used for clusters bigger than 128 sectors.
        long sectorsPerCluster = raw <= 0x80 ? raw : 1L << (256 - raw);
        return bytesPerSector * sectorsPerCluster;
    }

    /// <summary>Grows the file system, repoints the boot files, and makes Windows on the clone call itself C: again.</summary>
    private void FinishTarget(ClonePlan plan, List<Guid> ids, List<string> notes)
    {
        var mainIndex = plan.Partitions.ToList().FindIndex(p => p.Source == plan.Main);
        var main = plan.Partitions[mainIndex];
        var bootIndex = plan.Partitions.ToList().FindIndex(p => p.Source.IsEfi || (!plan.Source.IsGpt && p.Source.MbrBootable));
        var letters = FreeLetters();
        var w = letters[0];
        var s = bootIndex >= 0 && bootIndex != mainIndex ? letters[1] : w;

        var script = new List<string> { $"select disk {plan.Target.Number}", $"select partition {mainIndex + 1}", $"assign letter={w}" };
        if (main.Grows) script.Add("extend filesystem noerr");
        if (s != w) script.AddRange([$"select partition {bootIndex + 1}", $"assign letter={s}"]);
        log.Info(main.Grows ? "Growing the main partition to fill the drive..." : "Checking the copy...");
        Diskpart([.. script]);
        try
        {
            var windows = $@"{w}:\Windows";
            if (!Directory.Exists(windows))
            {
                notes.Add("No Windows on the main partition, so no boot files were needed.");
                return;
            }

            log.Info("Making the clone start up on its own...");
            ForgetDriveLetters($@"{windows}\System32\config\SYSTEM");
            var result = ProcessRunner.Run("bcdboot.exe", windows, "/s", $"{s}:", "/f", plan.Source.IsGpt ? "UEFI" : "BIOS");
            if (result.ExitCode != 0)
                notes.Add($"Writing boot files failed ({result.Output}). The data is all there; run 'bcdboot {windows} /s {s}: /f {(plan.Source.IsGpt ? "UEFI" : "BIOS")}' to fix it.");
            if (plan.Partitions.Any(p => p.Source.IsRecovery))
                notes.Add("If Windows' recovery tools are needed on the clone, run 'reagentc /enable' on it once it's running.");
        }
        finally
        {
            var remove = new List<string> { $"select disk {plan.Target.Number}", $"select partition {mainIndex + 1}", $"remove letter={w} noerr" };
            if (s != w) remove.AddRange([$"select partition {bootIndex + 1}", $"remove letter={s} noerr"]);
            Diskpart([.. remove]);
        }
    }

    /// <summary>
    /// Windows remembers which partition is C: by its ID. The clone's partitions have new IDs,
    /// so those memories are cleared; at first start Windows gives its own partition C: again.
    /// </summary>
    private void ForgetDriveLetters(string systemHive)
    {
        var mount = "PCMIGRATE_CLONE_" + Guid.NewGuid().ToString("N")[..8];
        if (ProcessRunner.Run("reg.exe", "load", $@"HKLM\{mount}", systemHive).ExitCode != 0)
        {
            log.Warn("Couldn't open the clone's registry to reset drive letters; Windows on it may pick a different letter at first start.");
            return;
        }
        try
        {
            using var devices = Registry.LocalMachine.OpenSubKey($@"{mount}\MountedDevices", writable: true);
            foreach (var name in devices?.GetValueNames() ?? [])
                if (name.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase)) devices!.DeleteValue(name, throwOnMissingValue: false);
        }
        finally
        {
            UserHive.Unload($@"HKLM\{mount}");
        }
    }

    /// <summary>Uses Windows' own shrink to make a partition smaller, freeing space at its end. Files are kept.</summary>
    public static string ShrinkVolume(string letter, long bytes)
    {
        var mb = (bytes + 1024 * 1024 - 1) / (1024 * 1024);
        return Diskpart($"select volume {letter}", $"shrink desired={mb}");
    }

    private static string Diskpart(params string[] commands)
    {
        var script = Path.Combine(Path.GetTempPath(), "pcmigrate-diskpart-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(script, commands);
        try
        {
            var result = ProcessRunner.Run("diskpart.exe", "/s", script);
            if (result.ExitCode != 0)
                throw new IOException($"Windows' disk tool reported a problem: {LastLines(result.Output)}");
            return result.Output;
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static string LastLines(string output) =>
        string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));

    private static bool AutomountEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mountmgr");
        return key?.GetValue("NoAutoMount") is not int n || n == 0;
    }

    private static List<char> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return "WVUTSRQPONMLKJIHG".Where(c => !used.Contains(c)).ToList();
    }

    private static string Label(PartitionInfo p) =>
        p.IsEfi ? "boot (EFI)" : p.IsMsr ? "reserved" : p.IsRecovery ? "recovery" : p.DriveLetter is { } d ? $"{d}:" : p.FileSystem ?? "data";
}
