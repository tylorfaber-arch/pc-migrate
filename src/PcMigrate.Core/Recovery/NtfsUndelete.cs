using System.Buffers.Binary;
using System.Text;

namespace PcMigrate.Core.Recovery;

/// <summary>Raw read access to a volume. Offsets and lengths passed in are always multiples of the sector size.</summary>
public interface IVolumeReader
{
    int SectorSize { get; }
    void Read(long offset, Span<byte> buffer);
}

/// <summary>How likely a deleted file is to come back whole.</summary>
public enum RecoveryChance
{
    /// <summary>None of its space has been reused yet.</summary>
    Good,
    /// <summary>Some of its space now belongs to other files, so parts of it are probably gone.</summary>
    Poor,
    /// <summary>Can't be recovered by this tool (compressed, encrypted, or spread over several records).</summary>
    No,
}

public sealed record DeletedFile(long Record, string Name, string Folder, long Size, DateTime Modified, RecoveryChance Chance, string? Why)
{
    public string Path => Folder.Length == 0 ? Name : Folder + "\\" + Name;
}

/// <summary>
/// Finds and recovers deleted files on an NTFS volume. When Windows deletes a file it only marks
/// the file's entry in the Master File Table (MFT) as free and its clusters as free in the bitmap;
/// the name, size and where the data sat stay put until something else takes the space. This reads
/// the MFT straight off the volume, lists entries marked free that still describe a file, checks
/// against the bitmap whether their clusters have been handed to anything else, and copies the
/// clusters out. Nothing is ever written to the scanned volume.
/// </summary>
public sealed class NtfsUndelete
{
    private const uint FileSignature = 0x454C4946; // "FILE"
    private const int AttrStandardInformation = 0x10, AttrAttributeList = 0x20, AttrFileName = 0x30, AttrData = 0x80, AttrEnd = unchecked((int)0xFFFFFFFF);
    private const long RootRecord = 5, BitmapRecord = 6;

    private readonly IVolumeReader _volume;
    private readonly int _bytesPerSector;
    private readonly long _clusterSize;
    private readonly int _recordSize;
    private readonly List<(long Lcn, long Count)> _mftRuns;
    private readonly long _mftBytes;
    private byte[]? _bitmap;

    private sealed record Entry(string Name, long Parent, bool InUse, bool IsDirectory);
    private readonly Dictionary<long, Entry> _names = [];

    public NtfsUndelete(IVolumeReader volume)
    {
        _volume = volume;
        var boot = new byte[Math.Max(512, volume.SectorSize)];
        volume.Read(0, boot);
        if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS") throw new InvalidDataException("This drive isn't NTFS. Deleted files can only be found on NTFS drives (Windows' own format).");
        _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        var spc = boot[0x0D];
        _clusterSize = _bytesPerSector * (spc <= 0x80 ? spc : 1L << (256 - spc));
        var mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        var perRecord = (sbyte)boot[0x40];
        _recordSize = perRecord > 0 ? (int)(perRecord * _clusterSize) : 1 << -perRecord;

        // Record 0 is the MFT itself; its data runs say where the rest of the MFT is.
        var first = ReadAligned(mftLcn * _clusterSize, _recordSize);
        if (!Fixup(first)) throw new InvalidDataException("The drive's file table couldn't be read.");
        var data = FindAttribute(first, AttrData) ?? throw new InvalidDataException("The drive's file table has no data.");
        (_mftRuns, _mftBytes) = (DecodeRuns(first, data), BinaryPrimitives.ReadInt64LittleEndian(first.AsSpan(data + 0x30)));
    }

    public long ClusterSize => _clusterSize;
    public long RecordCount => _mftBytes / _recordSize;

    /// <summary>Reads every entry of the file table and returns the deleted files that still describe some data.</summary>
    public List<DeletedFile> Scan(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        _bitmap ??= LoadBitmap();
        var deleted = new List<(long Record, byte[] Data, int NameAttr)>();
        var count = RecordCount;
        const int batch = 1024;
        long record = 0;
        foreach (var (lcn, clusters) in _mftRuns)
        {
            var runBytes = clusters * _clusterSize;
            for (long done = 0; done < runBytes && record < count; )
            {
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(batch, Math.Min((runBytes - done) / _recordSize, count - record));
                if (n <= 0) break;
                var chunk = ReadAligned(lcn * _clusterSize + done, n * _recordSize);
                for (var i = 0; i < n; i++, record++)
                {
                    var buf = chunk.AsSpan(i * _recordSize, _recordSize).ToArray();
                    if (BinaryPrimitives.ReadUInt32LittleEndian(buf) != FileSignature || !Fixup(buf)) continue;
                    var flags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x16));
                    var inUse = (flags & 1) != 0;
                    var isDir = (flags & 2) != 0;
                    // Extension records (part of another file) have a base record reference.
                    if ((BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0x20)) & 0xFFFFFFFFFFFF) != 0) continue;
                    var nameAttr = BestFileName(buf);
                    if (nameAttr < 0) continue;
                    var (name, parent) = ReadFileName(buf, nameAttr);
                    _names[record] = new Entry(name, parent, inUse, isDir);
                    if (!inUse && !isDir) deleted.Add((record, buf, nameAttr));
                }
                done += (long)n * _recordSize;
                progress?.Report((double)record / count);
            }
        }

        var result = new List<DeletedFile>();
        foreach (var (rec, buf, nameAttr) in deleted)
        {
            var (name, parent) = ReadFileName(buf, nameAttr);
            var modified = DateTime.FromFileTimeUtc(Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(nameAttr + 0x18))));
            if (FindAttribute(buf, AttrStandardInformation) is { } si && Resident(buf, si) is var (siOffset, siLength) && siLength >= 0x20)
                modified = DateTime.FromFileTimeUtc(Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(siOffset + 0x08))));
            var (size, chance, why) = Assess(buf);
            if (size < 0) continue;
            result.Add(new DeletedFile(rec, name, FolderOf(parent), size, modified.ToLocalTime(), chance, why));
        }
        return result.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Copies a deleted file's data to <paramref name="output"/>. Returns false if it can't be recovered.</summary>
    public bool Recover(long record, Stream output, CancellationToken ct = default)
    {
        var buf = ReadRecord(record);
        if (buf is null) return false;
        if (FindAttribute(buf, AttrAttributeList) is not null) return false;
        var data = FindAttribute(buf, AttrData);
        if (data is null) return false;
        var attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(data.Value + 0x0C));
        if ((attrFlags & 0x4001) != 0) return false; // compressed or encrypted
        if (buf[data.Value + 8] == 0)
        {
            var (offset, length) = Resident(buf, data.Value);
            output.Write(buf, offset, length);
            return true;
        }
        var size = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(data.Value + 0x30));
        long left = size;
        var chunk = new byte[Math.Max(_clusterSize, 1 << 20) / _clusterSize * _clusterSize];
        foreach (var (lcn, count) in DecodeRuns(buf, data.Value))
        {
            for (long c = 0; c < count && left > 0; )
            {
                ct.ThrowIfCancellationRequested();
                var take = Math.Min(chunk.Length / _clusterSize, count - c);
                var bytes = (int)(take * _clusterSize);
                if (lcn < 0) Array.Clear(chunk, 0, bytes); // a sparse run: zeros
                else _volume.Read((lcn + c) * _clusterSize, chunk.AsSpan(0, bytes));
                var write = (int)Math.Min(bytes, left);
                output.Write(chunk, 0, write);
                left -= write;
                c += take;
            }
        }
        return left == 0;
    }

    private (long Size, RecoveryChance Chance, string? Why) Assess(byte[] buf)
    {
        if (FindAttribute(buf, AttrAttributeList) is not null) return (0, RecoveryChance.No, "Spread over several file table entries");
        var data = FindAttribute(buf, AttrData);
        if (data is null) return (-1, RecoveryChance.No, null);
        var a = data.Value;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(a + 0x0C));
        if (buf[a + 8] == 0) return (Resident(buf, a).Length, RecoveryChance.Good, null);
        var size = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(a + 0x30));
        if ((flags & 0x0001) != 0) return (size, RecoveryChance.No, "Compressed by Windows");
        if ((flags & 0x4000) != 0) return (size, RecoveryChance.No, "Encrypted by Windows (EFS)");
        var runs = DecodeRuns(buf, a);
        if (runs.Count == 0 && size > 0) return (size, RecoveryChance.No, "Where its data was is no longer recorded");
        long reused = 0, total = 0;
        foreach (var (lcn, count) in runs)
        {
            if (lcn < 0) continue;
            for (long c = 0; c < count; c++)
            {
                total++;
                if (InUse(lcn + c)) reused++;
            }
        }
        return reused == 0 ? (size, RecoveryChance.Good, null) : (size, RecoveryChance.Poor, $"{reused * 100 / Math.Max(total, 1)}% of it has been reused by other files");
    }

    private bool InUse(long lcn)
    {
        if (_bitmap is null || lcn < 0 || lcn / 8 >= _bitmap.Length) return true;
        return (_bitmap[lcn / 8] & (1 << (int)(lcn % 8))) != 0;
    }

    private byte[]? LoadBitmap()
    {
        var buf = ReadRecord(BitmapRecord);
        if (buf is null || FindAttribute(buf, AttrData) is not { } data || buf[data + 8] == 0) return null;
        var size = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(data + 0x30));
        using var ms = new MemoryStream();
        if (!Recover(BitmapRecord, ms)) return null;
        return ms.ToArray().AsSpan(0, (int)Math.Min(size, ms.Length)).ToArray();
    }

    private string FolderOf(long parent)
    {
        var parts = new List<string>();
        var seen = new HashSet<long>();
        while (parent != RootRecord && seen.Add(parent) && parts.Count < 64)
        {
            if (!_names.TryGetValue(parent, out var e)) { parts.Add("(unknown folder)"); break; }
            parts.Add(e.InUse ? e.Name : e.Name + " (deleted)");
            parent = e.Parent;
        }
        parts.Reverse();
        return string.Join("\\", parts);
    }

    private byte[]? ReadRecord(long record)
    {
        var offset = record * _recordSize;
        foreach (var (lcn, count) in _mftRuns)
        {
            var bytes = count * _clusterSize;
            if (offset < bytes)
            {
                var buf = ReadAligned(lcn * _clusterSize + offset, _recordSize);
                return BinaryPrimitives.ReadUInt32LittleEndian(buf) == FileSignature && Fixup(buf) ? buf : null;
            }
            offset -= bytes;
        }
        return null;
    }

    private byte[] ReadAligned(long offset, int length)
    {
        var sector = _volume.SectorSize;
        var start = offset / sector * sector;
        var end = (offset + length + sector - 1) / sector * sector;
        var buf = new byte[end - start];
        _volume.Read(start, buf);
        return start == offset && buf.Length == length ? buf : buf.AsSpan((int)(offset - start), length).ToArray();
    }

    /// <summary>NTFS keeps the last two bytes of each sector of a record in a side table (the "update sequence"); put them back.</summary>
    private bool Fixup(byte[] record)
    {
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x04));
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x06));
        if (usaOffset + usaCount * 2 > record.Length || usaCount == 0) return false;
        var check = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
        // One entry per 512-byte stride of the record, whatever the disk's sector size.
        var stride = usaCount > 1 ? record.Length / (usaCount - 1) : record.Length;
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * stride - 2;
            if (end + 2 > record.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(end)) != check) return false;
            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    private static int? FindAttribute(byte[] rec, int type)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(0x14));
        while (offset + 8 <= rec.Length)
        {
            var t = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(offset));
            if (t == AttrEnd) return null;
            var len = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(offset + 4));
            if (len <= 0 || offset + len > rec.Length) return null;
            // The file's data is the unnamed $DATA; named ones are extra streams.
            if (t == type && (type != AttrData || rec[offset + 9] == 0)) return offset;
            offset += len;
        }
        return null;
    }

    /// <summary>The Windows-style long name, not the 8.3 short one.</summary>
    private static int BestFileName(byte[] rec)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(0x14));
        int best = -1;
        while (offset + 8 <= rec.Length)
        {
            var t = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(offset));
            if (t == AttrEnd) break;
            var len = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(offset + 4));
            if (len <= 0 || offset + len > rec.Length) break;
            if (t == AttrFileName && rec[offset + 8] == 0)
            {
                var (c, _) = Resident(rec, offset);
                if (c + 0x42 <= rec.Length)
                {
                    var ns = rec[c + 0x41];
                    if (best < 0 || ns != 2) best = c; // 2 = DOS short name; prefer anything else
                }
            }
            offset += len;
        }
        return best;
    }

    private static (string Name, long Parent) ReadFileName(byte[] rec, int c)
    {
        var parent = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(c)) & 0xFFFFFFFFFFFF;
        var chars = rec[c + 0x40];
        var name = Encoding.Unicode.GetString(rec, c + 0x42, Math.Min(chars * 2, rec.Length - c - 0x42));
        return (name, parent);
    }

    private static (int Offset, int Length) Resident(byte[] rec, int attr) =>
        (attr + BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(attr + 0x14)), BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(attr + 0x10)));

    /// <summary>Data runs: (first cluster, how many). A first cluster of -1 is a sparse (all-zero) run.</summary>
    internal static List<(long Lcn, long Count)> DecodeRuns(byte[] rec, int attr)
    {
        var runs = new List<(long, long)>();
        var p = attr + BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(attr + 0x20));
        var end = attr + BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(attr + 4));
        long lcn = 0;
        while (p < end && rec[p] != 0)
        {
            var lenSize = rec[p] & 0x0F;
            var offSize = rec[p] >> 4;
            p++;
            if (lenSize == 0 || lenSize > 8 || offSize > 8 || p + lenSize + offSize > end) break;
            long count = 0;
            for (var i = 0; i < lenSize; i++) count |= (long)rec[p + i] << (8 * i);
            p += lenSize;
            if (offSize == 0)
            {
                runs.Add((-1, count));
                continue;
            }
            long delta = 0;
            for (var i = 0; i < offSize; i++) delta |= (long)rec[p + i] << (8 * i);
            if ((rec[p + offSize - 1] & 0x80) != 0) delta |= -1L << (8 * offSize); // negative
            p += offSize;
            lcn += delta;
            runs.Add((lcn, count));
        }
        return runs;
    }
}

/// <summary>Copies chosen deleted files into a folder, keeping their folder layout.</summary>
public static class UndeleteCopier
{
    public sealed record Outcome(int Recovered, int Failed, long Bytes, List<string> Problems);

    public static Outcome RecoverTo(NtfsUndelete undelete, IEnumerable<DeletedFile> files, string outputRoot, IMigrationLog log, CancellationToken ct = default)
    {
        int ok = 0, failed = 0;
        long bytes = 0;
        var problems = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Chance == RecoveryChance.No)
            {
                failed++;
                problems.Add($"{file.Path}: {file.Why}");
                continue;
            }
            var target = UniquePath(Path.Combine(outputRoot, Safe(file.Folder), SafeName(file.Name)));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool done;
                using (var output = File.Create(target)) done = undelete.Recover(file.Record, output, ct);
                if (!done) { File.Delete(target); failed++; problems.Add($"{file.Path}: its data couldn't be read back."); continue; }
                try { File.SetLastWriteTime(target, file.Modified); } catch (Exception ex) when (ex is IOException or ArgumentException) { }
                ok++;
                bytes += file.Size;
                log.FileProgress(ok, bytes, file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                problems.Add($"{file.Path}: {ex.Message}");
            }
        }
        return new Outcome(ok, failed, bytes, problems);
    }

    private static string Safe(string folder) =>
        string.Join(Path.DirectorySeparatorChar, folder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(SafeName));

    private static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*', '\\', '/']).ToHashSet();
        var clean = new string(name.Select(c => bad.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "_" : clean;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
