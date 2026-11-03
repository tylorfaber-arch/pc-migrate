using System.Buffers.Binary;
using System.Text;
using PcMigrate.Core.Recovery;

namespace PcMigrate.Tests;

/// <summary>Builds a tiny NTFS volume in memory: just the parts the undelete reader looks at.</summary>
internal sealed class FakeNtfs : IVolumeReader
{
    public const int Cluster = 512, RecordSize = 1024, MftLcn = 4, MftRecords = 32, BitmapLcn = 70;
    public byte[] Bytes { get; } = new byte[256 * Cluster];
    public int SectorSize => 512;
    public void Read(long offset, Span<byte> buffer)
    {
        Assert.Equal(0, offset % SectorSize);
        Assert.Equal(0, buffer.Length % SectorSize);
        Bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    }

    public FakeNtfs()
    {
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(Bytes, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(0x0B), 512);
        Bytes[0x0D] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(Bytes.AsSpan(0x30), MftLcn);
        Bytes[0x40] = unchecked((byte)(sbyte)-10); // 2^10 = 1024-byte records

        Record(0, inUse: true, dir: false, "$MFT", 5, NonResident(MftRecords * RecordSize, (MftLcn, MftRecords * RecordSize / Cluster)));
        Record(5, inUse: true, dir: true, ".", 5);
        Record(6, inUse: true, dir: false, "$Bitmap", 5, NonResident(Cluster, (BitmapLcn, 1)));
        Record(16, inUse: true, dir: true, "Users", 5);
        Record(17, inUse: false, dir: true, "Old stuff", 16);
    }

    public void MarkUsed(long from, long count)
    {
        for (var c = from; c < from + count; c++) Bytes[BitmapLcn * Cluster + c / 8] |= (byte)(1 << (int)(c % 8));
    }

    public void Record(int number, bool inUse, bool dir, string name, long parent, byte[]? data = null, DateTime? modified = null)
    {
        var r = new byte[RecordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(r, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x04), 0x30); // update sequence at 0x30
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x06), 3);    // check value + 2 sectors
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x14), 0x38); // first attribute
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x16), (ushort)((inUse ? 1 : 0) | (dir ? 2 : 0)));
        var attrs = new List<byte[]>
        {
            Resident(0x10, StandardInfo(modified ?? new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc))),
            Resident(0x30, FileName(name, parent)),
        };
        if (data is not null) attrs.Add(data);
        var p = 0x38;
        foreach (var a in attrs) { a.CopyTo(r, p); p += a.Length; }
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(p), -1);
        // Update sequence: the last two bytes of each 512-byte sector move to the side table.
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x30), 0x0001);
        for (var i = 1; i <= 2; i++)
        {
            var end = i * 512 - 2;
            r[0x30 + i * 2] = r[end]; r[0x30 + i * 2 + 1] = r[end + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), 0x0001);
        }
        r.CopyTo(Bytes, MftLcn * Cluster + number * RecordSize);
    }

    public static byte[] Resident(int type, byte[] content)
    {
        var len = (0x18 + content.Length + 7) / 8 * 8;
        var a = new byte[len];
        BinaryPrimitives.WriteInt32LittleEndian(a, type);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(4), len);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(0x10), content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x14), 0x18);
        content.CopyTo(a, 0x18);
        return a;
    }

    public static byte[] NonResident(long size, params (long Lcn, long Count)[] runs)
    {
        var encoded = new List<byte>();
        long prev = 0;
        foreach (var (lcn, count) in runs)
        {
            var delta = lcn - prev; prev = lcn;
            var lenBytes = BitConverter.GetBytes(count).Take(2).ToArray();
            var offBytes = BitConverter.GetBytes(delta).Take(2).ToArray();
            encoded.Add(0x22);
            encoded.AddRange(lenBytes);
            encoded.AddRange(offBytes);
        }
        encoded.Add(0);
        var len = (0x40 + encoded.Count + 7) / 8 * 8;
        var a = new byte[len];
        BinaryPrimitives.WriteInt32LittleEndian(a, 0x80);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(4), len);
        a[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x28), runs.Sum(r => r.Count) * Cluster);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x30), size);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x38), size);
        encoded.ToArray().CopyTo(a, 0x40);
        return a;
    }

    private static byte[] StandardInfo(DateTime modified)
    {
        var c = new byte[0x30];
        BinaryPrimitives.WriteInt64LittleEndian(c.AsSpan(0x08), modified.ToFileTimeUtc());
        return c;
    }

    private static byte[] FileName(string name, long parent)
    {
        var c = new byte[0x42 + name.Length * 2];
        BinaryPrimitives.WriteInt64LittleEndian(c, parent | (1L << 48));
        c[0x40] = (byte)name.Length;
        c[0x41] = 1; // Win32 name
        Encoding.Unicode.GetBytes(name).CopyTo(c, 0x42);
        return c;
    }
}

public class UndeleteTests
{
    private static FakeNtfs Volume()
    {
        var v = new FakeNtfs();
        v.MarkUsed(0, FakeNtfs.BitmapLcn + 1);
        // A deleted photo whose clusters are still free.
        var photo = new byte[1300];
        new Random(1).NextBytes(photo);
        for (var i = 0; i < photo.Length; i++) v.Bytes[100 * FakeNtfs.Cluster + i] = photo[i];
        v.Record(20, inUse: false, dir: false, "photo.jpg", 16, FakeNtfs.NonResident(photo.Length, (100, 3)));
        // A small deleted file kept inside its table entry.
        v.Record(21, inUse: false, dir: false, "note.txt", 17, FakeNtfs.Resident(0x80, "hello there"u8.ToArray()));
        // A deleted file whose space has since gone to something else.
        v.Record(22, inUse: false, dir: false, "old.docx", 16, FakeNtfs.NonResident(1024, (110, 2)));
        v.MarkUsed(110, 1);
        // A file that isn't deleted.
        v.Record(23, inUse: true, dir: false, "live.txt", 16, FakeNtfs.Resident(0x80, "live"u8.ToArray()));
        return v;
    }

    [Fact]
    public void FindsDeletedFilesWithTheirFoldersAndChances()
    {
        var undelete = new NtfsUndelete(Volume());
        var found = undelete.Scan().ToDictionary(f => f.Name);

        Assert.Equal(["note.txt", "old.docx", "photo.jpg"], found.Keys.Order());
        Assert.Equal(@"Users\photo.jpg", found["photo.jpg"].Path);
        Assert.Equal(1300, found["photo.jpg"].Size);
        Assert.Equal(RecoveryChance.Good, found["photo.jpg"].Chance);
        Assert.Equal(@"Users\Old stuff (deleted)\note.txt", found["note.txt"].Path);
        Assert.Equal(RecoveryChance.Poor, found["old.docx"].Chance);
        Assert.Contains("50%", found["old.docx"].Why);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), found["photo.jpg"].Modified.ToUniversalTime());
    }

    [Fact]
    public void RecoversTheBytes()
    {
        var volume = Volume();
        var undelete = new NtfsUndelete(volume);
        var found = undelete.Scan().ToDictionary(f => f.Name);

        using var photo = new MemoryStream();
        Assert.True(undelete.Recover(found["photo.jpg"].Record, photo));
        Assert.Equal(volume.Bytes.AsSpan(100 * FakeNtfs.Cluster, 1300).ToArray(), photo.ToArray());

        using var note = new MemoryStream();
        Assert.True(undelete.Recover(found["note.txt"].Record, note));
        Assert.Equal("hello there", Encoding.ASCII.GetString(note.ToArray()));
    }

    [Fact]
    public void RefusesOtherFileSystems()
    {
        var v = new FakeNtfs();
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(v.Bytes, 3);
        Assert.Throws<InvalidDataException>(() => new NtfsUndelete(v));
    }

    [Theory]
    [InlineData(new byte[] { 0x21, 0x10, 0x00, 0x01, 0x00 }, 256L, 16L)] // 16 clusters at 256
    [InlineData(new byte[] { 0x21, 0x04, 0x00, 0x01, 0x01, 0x04, 0x00 }, 256L, 4L)] // then a sparse run
    public void DecodesRuns(byte[] runs, long lcn, long count)
    {
        var attr = new byte[0x40 + runs.Length + 8];
        BinaryPrimitives.WriteInt32LittleEndian(attr.AsSpan(4), attr.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attr.AsSpan(0x20), 0x40);
        runs.CopyTo(attr, 0x40);
        var decoded = NtfsUndelete.DecodeRuns(attr, 0);
        Assert.Equal((lcn, count), decoded[0]);
        if (runs.Length > 5) Assert.Equal((-1L, 4L), decoded[1]);
    }
}
