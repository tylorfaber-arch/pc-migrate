namespace PcMigrate.Core.Clone;

/// <summary>What a partition can be resized to, and what's in the way of growing it.</summary>
public sealed record ResizeOption
{
    public required PartitionInfo Partition { get; init; }
    /// <summary>Smallest size Windows can shrink it to (its files have to fit).</summary>
    public required long Min { get; init; }
    /// <summary>Biggest size it can grow to as things are (into free space right after it).</summary>
    public required long Max { get; init; }
    /// <summary>Why it can't grow, when it can't: usually the recovery partition right after it.</summary>
    public string? Blocked { get; init; }
    /// <summary>The recovery partition right after it, which can be moved to the end of the drive to make room.</summary>
    public PartitionInfo? Recovery { get; init; }
    /// <summary>Where the recovery partition would go, and how big this partition could then be.</summary>
    public long RecoveryNewOffset { get; init; }
    public long MaxAfterMovingRecovery { get; init; }

    public bool CanMoveRecovery => Recovery is not null && MaxAfterMovingRecovery > Max;
}

/// <summary>
/// Works out partition resizes. Windows does the resizing itself (it moves files out of the way
/// when shrinking); this decides what's possible and spots the commonest problem: a new, bigger
/// drive whose main partition can't grow because the recovery partition sits right after it.
/// </summary>
public static class ResizePlanner
{
    public const long Mb = 1024 * 1024;

    /// <summary>The end of the space partitions may use (GPT keeps a backup table in the last sectors).</summary>
    public static long UsableEnd(DiskInfo disk) =>
        AlignDown(disk.IsGpt ? disk.Size - 34L * disk.SectorSize : disk.Size);

    public static long AlignDown(long bytes) => bytes / Mb * Mb;

    /// <param name="supportedMin">What Windows says the partition can shrink to.</param>
    /// <param name="supportedMax">What Windows says the partition can grow to.</param>
    public static ResizeOption Plan(DiskInfo disk, PartitionInfo part, long supportedMin, long supportedMax)
    {
        var ordered = disk.Partitions.OrderBy(p => p.Offset).ToList();
        var next = ordered.FirstOrDefault(p => p.Offset >= part.End);
        var option = new ResizeOption { Partition = part, Min = Math.Min(supportedMin, part.Length), Max = Math.Max(supportedMax, part.Length) };

        string? blocked = null;
        if (option.Max - part.Length < Mb)
        {
            blocked = next is null ? "There's no free space after it on the drive."
                : next.IsRecovery ? "The recovery partition sits right after it, so it can't grow into the free space."
                : $"Another partition ({Label(next)}) sits right after it.";
        }
        option = option with { Blocked = blocked };

        // A recovery partition directly after it (nothing in between but maybe a little gap), with
        // free space after the recovery partition: moving recovery to the end frees that space.
        if (next is { IsRecovery: true } recovery && part.IsNtfs)
        {
            var after = ordered.FirstOrDefault(p => p.Offset >= recovery.End);
            var spaceEnd = after?.Offset ?? UsableEnd(disk);
            var newOffset = AlignDown(spaceEnd - recovery.Length);
            if (newOffset > recovery.Offset)
            {
                option = option with
                {
                    Recovery = recovery,
                    RecoveryNewOffset = newOffset,
                    MaxAfterMovingRecovery = newOffset - part.Offset,
                };
            }
        }
        return option;
    }

    /// <summary>Why this new size can't be used, or null if it can. Sizes are rounded to whole megabytes.</summary>
    public static string? Check(ResizeOption option, long newSize, bool moveRecovery)
    {
        var max = moveRecovery && option.CanMoveRecovery ? option.MaxAfterMovingRecovery : option.Max;
        if (newSize < option.Min)
            return $"It can't be smaller than {DiskInfo.FormatSize(option.Min)}: its files (and space Windows can't move) need that much.";
        if (newSize > max)
            return option.Blocked is not null && !moveRecovery && option.CanMoveRecovery
                ? $"It can only be {DiskInfo.FormatSize(option.Max)} as things are. {option.Blocked} Move the recovery partition to the end to make it up to {DiskInfo.FormatSize(option.MaxAfterMovingRecovery)}."
                : $"It can be at most {DiskInfo.FormatSize(max)}.";
        if (Math.Abs(AlignDown(newSize) - option.Partition.Length) < Mb && !moveRecovery)
            return "That's the size it already is.";
        return null;
    }

    public static string Label(PartitionInfo p) =>
        p.IsEfi ? "Boot (EFI)" : p.IsMsr ? "Reserved" : p.IsRecovery ? "Recovery" : p.DriveLetter is { } d ? $"{d}: {p.FileSystem}" : p.FileSystem ?? "Partition " + p.Number;

    /// <summary>Partitions that can be resized from here: ones with a file system Windows can resize.</summary>
    public static bool Resizable(PartitionInfo p) =>
        !p.IsEfi && !p.IsMsr && !p.IsRecovery && (p.IsNtfs || string.Equals(p.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase));
}
