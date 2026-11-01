namespace PcMigrate.Core.Clone;

/// <summary>A partition as found on a disk.</summary>
public sealed record PartitionInfo
{
    public required int Number { get; init; }
    public required long Offset { get; init; }
    public required long Length { get; init; }
    /// <summary>GPT partition type (EFI, Microsoft reserved, basic data, recovery...). Null on MBR disks.</summary>
    public Guid? GptType { get; init; }
    public Guid? GptId { get; init; }
    public ulong GptAttributes { get; init; }
    public string? GptName { get; init; }
    /// <summary>MBR partition type byte (0x07 NTFS, 0x27 recovery...). 0 on GPT disks.</summary>
    public byte MbrType { get; init; }
    public bool MbrBootable { get; init; }
    /// <summary>"NTFS", "FAT32"... null when Windows doesn't mount it (MSR, unformatted).</summary>
    public string? FileSystem { get; init; }
    public long? UsedBytes { get; init; }
    public string? DriveLetter { get; init; }
    /// <summary>\\?\Volume{...} without the trailing backslash, if Windows mounted it.</summary>
    public string? VolumePath { get; init; }

    public bool IsNtfs => string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
    public bool IsEfi => GptType == KnownPartitions.Efi || MbrType == 0xEF;
    public bool IsMsr => GptType == KnownPartitions.MicrosoftReserved;
    public bool IsRecovery => GptType == KnownPartitions.Recovery || MbrType == 0x27;
    public long End => Offset + Length;
}

public static class KnownPartitions
{
    public static readonly Guid Efi = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b726a5c7");
    public static readonly Guid Recovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
}

/// <summary>A physical disk and what's on it.</summary>
public sealed record DiskInfo
{
    public required int Number { get; init; }
    public required string Model { get; init; }
    public string? Serial { get; init; }
    public required long Size { get; init; }
    public string Bus { get; init; } = "";
    /// <summary>Holds the Windows that is running right now. Never a clone target.</summary>
    public bool IsSystem { get; init; }
    public bool IsGpt { get; init; }
    public int SectorSize { get; init; } = 512;
    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = [];

    public string Describe() => $"Disk {Number}: {Model}{(Serial is null ? "" : " (" + Serial + ")")}, {FormatSize(Size)}{(Bus.Length > 0 ? ", " + Bus : "")}";

    internal static string FormatSize(long bytes) => Capture.CaptureEngine.FormatBytes(bytes);
}

/// <summary>Where one partition goes on the target disk.</summary>
public sealed record PlannedPartition(PartitionInfo Source, long Offset, long Length)
{
    public bool Grows => Length > Source.Length;
    /// <summary>Bytes that will actually be read and written: only the used part of NTFS partitions.</summary>
    public long BytesToCopy => Source.IsNtfs && Source.UsedBytes is { } used ? Math.Min(used, Source.Length) : Source.Length;
}

public sealed record ClonePlan
{
    public required DiskInfo Source { get; init; }
    public required DiskInfo Target { get; init; }
    public IReadOnlyList<PlannedPartition> Partitions { get; init; } = [];
    /// <summary>Why the clone can't go ahead as is, in words for people. Null when it can.</summary>
    public string? Problem { get; init; }
    /// <summary>
    /// When the target is too small only because the main partition is bigger than its contents:
    /// shrinking that partition on the source by this many bytes (with Windows' own shrink) makes it fit.
    /// </summary>
    public long ShrinkSourceBy { get; init; }
    public PartitionInfo? Main { get; init; }
    public long BytesToCopy => Partitions.Sum(p => p.BytesToCopy);
    public bool CanRun => Problem is null;
}

/// <summary>
/// Decides how the source disk's partitions fit on the target. Everything before the main
/// (biggest) partition keeps its place. Partitions after it, usually Windows' recovery tools,
/// move to the very end of the target. The main partition takes whatever is left, so on a
/// bigger drive it grows, and Windows' own "extend" makes its file system fill it afterwards.
/// Partitions are never cut down in the copy: NTFS can't simply be truncated, so when the
/// target is smaller, the plan says how much to shrink the source first.
/// </summary>
public static class ClonePlanner
{
    public const long Align = 1024 * 1024;
    // GPT keeps a backup copy of the partition table in the last 33 sectors.
    private const int GptBackupSectors = 33;
    // Space kept free when suggesting a shrink, so the shrunk partition fits with room to spare.
    private const long ShrinkMargin = 256 * Align;

    public static ClonePlan Plan(DiskInfo source, DiskInfo target)
    {
        ClonePlan Fail(string problem, long shrink = 0, PartitionInfo? main = null) =>
            new() { Source = source, Target = target, Problem = problem, ShrinkSourceBy = shrink, Main = main };

        if (source.Number == target.Number) return Fail("The source and target are the same drive.");
        if (target.IsSystem) return Fail("The target is the drive this PC is running Windows from. Pick a different target.");
        if (source.SectorSize != target.SectorSize)
            return Fail($"The drives use different sector sizes ({source.SectorSize} and {target.SectorSize} bytes), so a partition copied from one won't open on the other. Try a different USB adapter or drive.");
        if (source.Partitions.Count == 0) return Fail("The source drive has no partitions to copy.");

        var parts = source.Partitions.OrderBy(p => p.Offset).ToList();
        var main = parts.MaxBy(p => p.Length)!;
        var mainIndex = parts.IndexOf(main);

        // The last byte a partition can use on the target.
        var limit = target.IsGpt || source.IsGpt
            ? target.Size - (long)GptBackupSectors * target.SectorSize
            : Math.Min(target.Size, (long)uint.MaxValue * target.SectorSize); // MBR can't reach past 2 TB
        limit = limit / Align * Align;

        var planned = new List<PlannedPartition>();
        foreach (var p in parts.Take(mainIndex)) planned.Add(new PlannedPartition(p, p.Offset, p.Length));

        // Partitions after the main one, packed against the end, in the same order.
        var after = parts.Skip(mainIndex + 1).ToList();
        var tail = new List<PlannedPartition>();
        var end = limit;
        foreach (var p in Enumerable.Reverse(after))
        {
            var length = (p.Length + Align - 1) / Align * Align;
            end -= length;
            tail.Insert(0, new PlannedPartition(p, end, p.Length));
        }

        var mainLength = (end - main.Offset) / Align * Align;
        if (mainLength < main.Length)
        {
            var shortBy = main.Length - mainLength;
            var needed = target.Size + shortBy;
            var used = main.UsedBytes ?? main.Length;
            if (mainLength < used + ShrinkMargin)
                return Fail($"The target is too small: the source holds about {DiskInfo.FormatSize(used + (source.Size - main.Length))} of data. Use a drive of at least {DiskInfo.FormatSize(needed)}.", 0, main);
            var shrink = (shortBy + Align - 1) / Align * Align;
            return Fail($"The target is {DiskInfo.FormatSize(shortBy)} too small for the source's partitions, but the data would fit. " +
                        $"Shrink {(main.DriveLetter is { } d ? d + ":" : "the main partition")} by {DiskInfo.FormatSize(shrink)} first (Windows' own shrink; no files are touched).",
                        shrink, main);
        }
        planned.Add(new PlannedPartition(main, main.Offset, mainLength));
        planned.AddRange(tail);

        return new ClonePlan { Source = source, Target = target, Partitions = planned, Main = main };
    }
}
