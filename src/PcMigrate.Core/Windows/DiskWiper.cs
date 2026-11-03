using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using PcMigrate.Core.Cleanup;
using PcMigrate.Core.Clone;

namespace PcMigrate.Core.Windows;

/// <summary>Wipes whole drives and a drive's free space, using the overwriting in <see cref="Wipe"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class DiskWiper(IMigrationLog log)
{
    public sealed record Result(long Bytes, TimeSpan Elapsed, string? NewLetter, List<string> Notes);

    /// <summary>
    /// Overwrites every byte of the drive. Afterwards, with <paramref name="makeUsable"/>, it gets one
    /// fresh empty NTFS partition so it's ready to use or give away; otherwise it's left blank.
    /// </summary>
    public async Task<Result> WipeDiskAsync(int number, WipePasses passes, bool makeUsable, string label, CancellationToken ct)
    {
        var disk = Disks.List().FirstOrDefault(d => d.Number == number) ?? throw new InvalidOperationException("That drive isn't there any more. Pick it again.");
        if (Wipe.CheckDisk(disk) is { } problem) throw new InvalidOperationException(problem);
        var notes = new List<string>();
        if (Wipe.Note(disk) is { } note) notes.Add(note);

        log.Info($"Clearing {disk.Describe()}'s partitions...");
        // "clean" takes the partitions (and so the drive letters) away, so nothing has the drive open.
        Diskpart.Run($"select disk {number}", "attributes disk clear readonly", "online disk noerr", "clean");
        var started = DateTime.UtcNow;
        using (var handle = RawDisk.Open(RawDisk.PhysicalDrive(number), write: true))
        {
            var device = new PhysicalDevice(handle, RawDisk.Length(handle));
            log.Info($"Overwriting {DiskInfo.FormatSize(device.Length)} ({(int)passes} pass{(passes == WipePasses.One ? "" : "es")})...");
            await Task.Run(() => Wipe.Device(device, passes, new Progress<long>(b => log.FileProgress(0, b, disk.Model)), ct), ct);
        }
        log.Info("Wipe checked: the drive reads back as zeros.");

        string? letter = null;
        if (makeUsable)
        {
            log.Info("Setting it up as one empty drive...");
            var safeLabel = new string(label.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').Take(32).ToArray()).Trim();
            Diskpart.Run($"select disk {number}", "convert gpt", "create partition primary", $"format fs=ntfs quick label=\"{(safeLabel.Length > 0 ? safeLabel : "Wiped")}\"", "assign");
            letter = Disks.List().FirstOrDefault(d => d.Number == number)?.Partitions.Select(p => p.DriveLetter).FirstOrDefault(l => l is not null);
        }
        return new Result(disk.Size * (int)passes, DateTime.UtcNow - started, letter, notes);
    }

    /// <summary>
    /// Overwrites the drive's free space, so files deleted from it in the past can't be recovered.
    /// Files still on the drive aren't touched. Uses Windows' own cipher /w.
    /// </summary>
    public async Task WipeFreeSpaceAsync(string letter, CancellationToken ct)
    {
        var root = letter.TrimEnd(':', '\\')[..1].ToUpperInvariant() + @":\";
        if (!Directory.Exists(root)) throw new InvalidOperationException($"{root} isn't there.");
        log.Info($"Overwriting the free space on {root} (three passes: zeros, ones, random). This can take hours on a big drive...");
        var result = await ProcessRunner.RunAsync("cipher.exe", ct, "/w:" + root);
        if (result.ExitCode != 0) throw new IOException($"Windows couldn't wipe the free space: {result.Output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()}");
    }

    private sealed class PhysicalDevice(SafeFileHandle handle, long length) : IBlockDevice
    {
        public long Length => length;
        public void Write(ReadOnlySpan<byte> data, long offset) => RandomAccess.Write(handle, data, offset);
        public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(handle, buffer, offset);
    }
}
