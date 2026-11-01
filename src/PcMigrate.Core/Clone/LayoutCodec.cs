using System.Buffers.Binary;
using System.Text;

namespace PcMigrate.Core.Clone;

/// <summary>
/// Reads and writes the partition table in the binary form Windows' disk driver uses
/// (DRIVE_LAYOUT_INFORMATION_EX: a 48-byte header followed by one 144-byte entry per
/// partition). Kept free of Windows calls so the byte layout can be tested anywhere.
/// </summary>
public static class LayoutCodec
{
    public const int HeaderSize = 48;
    public const int EntrySize = 144;
    private const int StyleMbr = 0, StyleGpt = 1;

    public sealed record Layout(bool IsGpt, Guid DiskId, uint MbrSignature, long StartingUsableOffset, long UsableLength, uint MaxPartitionCount, IReadOnlyList<PartitionInfo> Partitions);

    public static Layout Decode(ReadOnlySpan<byte> buffer)
    {
        var style = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var count = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
        var gpt = style == StyleGpt;
        var parts = new List<PartitionInfo>();
        for (var i = 0; i < count; i++)
        {
            var e = buffer.Slice(HeaderSize + i * EntrySize, EntrySize);
            var length = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
            var number = BinaryPrimitives.ReadInt32LittleEndian(e[24..]);
            if (gpt)
            {
                var name = Encoding.Unicode.GetString(e.Slice(72, 72)).TrimEnd('\0');
                parts.Add(new PartitionInfo
                {
                    Number = number,
                    Offset = BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                    Length = length,
                    GptType = new Guid(e.Slice(32, 16)),
                    GptId = new Guid(e.Slice(48, 16)),
                    GptAttributes = BinaryPrimitives.ReadUInt64LittleEndian(e[64..]),
                    GptName = name.Length == 0 ? null : name,
                });
            }
            else
            {
                // MBR tables always have 4 slots; empty ones have type 0.
                var type = e[32];
                if (type == 0 || length == 0) continue;
                parts.Add(new PartitionInfo
                {
                    Number = number,
                    Offset = BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                    Length = length,
                    MbrType = type,
                    MbrBootable = e[33] != 0,
                });
            }
        }
        return gpt
            ? new Layout(true, new Guid(buffer.Slice(8, 16)), 0, BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(buffer[32..]), BinaryPrimitives.ReadUInt32LittleEndian(buffer[40..]), parts)
            : new Layout(false, Guid.Empty, BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]), 0, 0, 0, parts);
    }

    /// <summary>Builds the table for IOCTL_DISK_SET_DRIVE_LAYOUT_EX. Every entry is marked to be written.</summary>
    public static byte[] Encode(Layout layout, IReadOnlyList<(PartitionInfo Source, long Offset, long Length, Guid NewId)> partitions, int sectorSize = 512)
    {
        // An MBR table is always written as a whole number of 4-slot tables.
        var count = layout.IsGpt ? partitions.Count : Math.Max(4, (partitions.Count + 3) / 4 * 4);
        var buffer = new byte[HeaderSize + count * EntrySize];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, layout.IsGpt ? StyleGpt : StyleMbr);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], count);
        if (layout.IsGpt)
        {
            layout.DiskId.TryWriteBytes(span.Slice(8, 16));
            BinaryPrimitives.WriteInt64LittleEndian(span[24..], layout.StartingUsableOffset);
            BinaryPrimitives.WriteInt64LittleEndian(span[32..], layout.UsableLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span[40..], layout.MaxPartitionCount == 0 ? 128 : layout.MaxPartitionCount);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], layout.MbrSignature);
        }

        for (var i = 0; i < count; i++)
        {
            var e = span.Slice(HeaderSize + i * EntrySize, EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(e, layout.IsGpt ? StyleGpt : StyleMbr);
            e[28] = 1; // RewritePartition
            if (i >= partitions.Count) continue;
            var (source, offset, length, id) = partitions[i];
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], offset);
            BinaryPrimitives.WriteInt64LittleEndian(e[16..], length);
            BinaryPrimitives.WriteInt32LittleEndian(e[24..], i + 1);
            if (layout.IsGpt)
            {
                (source.GptType ?? KnownPartitions.BasicData).TryWriteBytes(e.Slice(32, 16));
                id.TryWriteBytes(e.Slice(48, 16));
                BinaryPrimitives.WriteUInt64LittleEndian(e[64..], source.GptAttributes);
                var name = Encoding.Unicode.GetBytes((source.GptName ?? "").Length > 36 ? source.GptName![..36] : source.GptName ?? "");
                name.CopyTo(e.Slice(72, 72));
            }
            else
            {
                e[32] = source.MbrType;
                e[33] = (byte)(source.MbrBootable ? 1 : 0);
                e[34] = 1; // RecognizedPartition
                BinaryPrimitives.WriteUInt32LittleEndian(e[36..], (uint)(offset / sectorSize));
            }
        }
        return buffer;
    }
}
