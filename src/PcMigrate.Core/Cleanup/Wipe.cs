using System.Security.Cryptography;
using PcMigrate.Core.Clone;

namespace PcMigrate.Core.Cleanup;

/// <summary>Something that can be overwritten end to end: a whole disk, or a stand-in for one in tests.</summary>
public interface IBlockDevice
{
    long Length { get; }
    void Write(ReadOnlySpan<byte> data, long offset);
    int Read(Span<byte> buffer, long offset);
}

public enum WipePasses
{
    /// <summary>One pass of zeros. Enough for any drive made this century (NIST 800-88 "Clear").</summary>
    One = 1,
    /// <summary>Random, random, then zeros. Slower; for people who want to be extra sure.</summary>
    Three = 3,
}

/// <summary>
/// Overwrites drives and files so what was on them can't be read back, by recovery tools
/// (including this one's) or anyone else.
/// </summary>
public static class Wipe
{
    private const int Chunk = 4 * 1024 * 1024;

    /// <summary>Why this drive can't be wiped, or null if it can.</summary>
    public static string? CheckDisk(DiskInfo disk)
    {
        if (disk.IsSystem) return "This is the drive Windows is running from. It can't be wiped while it's in use; use the rescue USB, or wipe it from another PC.";
        if (disk.Size <= 0) return "Windows can't tell how big this drive is.";
        return null;
    }

    /// <summary>A note worth showing before wiping, or null.</summary>
    public static string? Note(DiskInfo disk) =>
        disk.Bus is "NVMe" || disk.Model.Contains("SSD", StringComparison.OrdinalIgnoreCase)
            ? "This looks like an SSD. Overwriting it makes the data unreadable by normal means, but SSDs keep spare space that can't be overwritten from Windows. For the strongest wipe, also use the maker's tool's Secure Erase (Samsung Magician, Crucial Storage Executive, WD Dashboard)."
            : null;

    /// <summary>
    /// Overwrites every byte of the device, then reads back samples to check the last pass landed.
    /// Progress is bytes written across all passes.
    /// </summary>
    public static void Device(IBlockDevice device, WipePasses passes, IProgress<long>? progress, CancellationToken ct)
    {
        var total = (int)passes;
        var buffer = new byte[Chunk];
        long done = 0;
        for (var pass = 1; pass <= total; pass++)
        {
            var zeros = pass == total;
            if (zeros) Array.Clear(buffer);
            for (long offset = 0; offset < device.Length; offset += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(Chunk, device.Length - offset);
                if (!zeros) RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
                device.Write(buffer.AsSpan(0, n), offset);
                done += n;
                progress?.Report(done);
            }
        }
        Verify(device, ct);
    }

    /// <summary>Reads 64 spots spread over the device (plus the very start and end) and checks they're all zero.</summary>
    internal static void Verify(IBlockDevice device, CancellationToken ct)
    {
        const int Sample = 64 * 1024;
        var buffer = new byte[Sample];
        var spots = Enumerable.Range(0, 64).Select(i => device.Length / 64 * i).Append(Math.Max(0, device.Length - Sample));
        foreach (var spot in spots)
        {
            ct.ThrowIfCancellationRequested();
            var at = spot / 4096 * 4096;
            var n = (int)Math.Min(Sample, device.Length - at);
            if (n <= 0) continue;
            var read = device.Read(buffer.AsSpan(0, n), at);
            if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0)
                throw new IOException($"Checking the wipe found data still there at {DiskInfo.FormatSize(at)} in. The drive may be failing or refusing writes.");
        }
    }

    public sealed record ShredResult(int Files, long Bytes, List<string> Problems);

    /// <summary>
    /// Overwrites files (and everything in folders) and then deletes them, with their names scrambled
    /// first so the old names aren't left in the drive's file list either.
    /// </summary>
    public static ShredResult Files(IEnumerable<string> paths, WipePasses passes, IProgress<(int Files, long Bytes, string Path)>? progress, CancellationToken ct)
    {
        int files = 0; long bytes = 0; var problems = new List<string>();
        var folders = new List<string>();
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            if (ProtectedPlace(path) is { } why) { problems.Add($"Left {path}: {why}"); continue; }
            IEnumerable<string> targets;
            if (Directory.Exists(path))
            {
                folders.Add(path);
                targets = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
            }
            else if (File.Exists(path)) targets = [path];
            else { problems.Add($"{path} isn't there."); continue; }

            foreach (var file in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var size = ShredFile(file, passes, ct);
                    files++; bytes += size;
                    progress?.Report((files, bytes, file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"Couldn't wipe {file}: {ex.Message}");
                }
            }
        }
        foreach (var folder in folders)
        {
            try { DeleteEmptyTree(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"Couldn't remove the folder {folder}: {ex.Message}"); }
        }
        return new ShredResult(files, bytes, problems);
    }

    internal static long ShredFile(string file, WipePasses passes, CancellationToken ct)
    {
        var info = new FileInfo(file);
        if (info.IsReadOnly) info.IsReadOnly = false;
        var length = info.Length;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
        {
            var buffer = new byte[(int)Math.Min(Chunk, Math.Max(length, 1))];
            for (var pass = 1; pass <= (int)passes; pass++)
            {
                var zeros = pass == (int)passes;
                if (zeros) Array.Clear(buffer);
                stream.Position = 0;
                for (long done = 0; done < length;)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(buffer.Length, length - done);
                    if (!zeros) RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
                    stream.Write(buffer, 0, n);
                    done += n;
                }
                stream.Flush(flushToDisk: true);
            }
            stream.SetLength(0);
        }
        // A scrambled name, so the original isn't left behind in the file list either.
        var scrambled = Path.Combine(Path.GetDirectoryName(file)!, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp");
        File.Move(file, scrambled);
        File.Delete(scrambled);
        return length;
    }

    private static void DeleteEmptyTree(string folder)
    {
        foreach (var sub in Directory.GetDirectories(folder)) DeleteEmptyTree(sub);
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    /// <summary>Places that must never be wiped from here: drive roots, Windows, programs, whole profiles.</summary>
    internal static string? ProtectedPlace(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(path);
        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            return "that's a whole drive. Use \"Whole drive\" to wipe a drive.";
        var p = full.Replace('/', '\\') + "\\";
        string[] system = [@":\Windows\", @":\Program Files\", @":\Program Files (x86)\", @":\ProgramData\", @"\System Volume Information\"];
        if (system.Any(s => p.Contains(s, StringComparison.OrdinalIgnoreCase))) return "Windows and programs need it.";
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 3 && parts.Length >= 2 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
            return "that's a whole user's profile (or all of them).";
        return null;
    }
}
