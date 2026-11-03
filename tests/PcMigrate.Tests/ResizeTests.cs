using PcMigrate.Core.Clone;
using Xunit;

namespace PcMigrate.Tests;

public class ResizeTests
{
    private const long Mb = 1024 * 1024, Gb = 1024 * Mb;

    private static PartitionInfo Efi => new() { Number = 1, Offset = 1 * Mb, Length = 100 * Mb, GptType = KnownPartitions.Efi };
    private static PartitionInfo Msr => new() { Number = 2, Offset = 101 * Mb, Length = 16 * Mb, GptType = KnownPartitions.MicrosoftReserved };
    private static PartitionInfo DataPart(long length) => new() { Number = 3, Offset = 117 * Mb, Length = length, GptType = KnownPartitions.BasicData, FileSystem = "NTFS", DriveLetter = "C", UsedBytes = 180 * Gb };
    private static PartitionInfo Recovery(long offset) => new() { Number = 4, Offset = offset, Length = 750 * Mb, GptType = KnownPartitions.Recovery, FileSystem = "NTFS" };

    /// <summary>The usual case: a 500 GB layout cloned onto a 2 TB drive, recovery right after C:.</summary>
    private static (DiskInfo Disk, PartitionInfo C) ClonedToBigger()
    {
        var c = DataPart(475 * Gb);
        var disk = new DiskInfo { Number = 1, Model = "Crucial 2TB", Size = 2000 * Gb, IsGpt = true, IsSystem = true, Partitions = [Efi, Msr, c, Recovery(c.End)] };
        return (disk, c);
    }

    [Fact]
    public void Spots_the_recovery_partition_blocking_growth_and_how_much_moving_it_frees()
    {
        var (disk, c) = ClonedToBigger();
        var option = ResizePlanner.Plan(disk, c, supportedMin: 190 * Gb, supportedMax: c.Length);

        Assert.Contains("recovery partition", option.Blocked);
        Assert.True(option.CanMoveRecovery);
        // Recovery goes to the very end; C: can grow up to just before it.
        Assert.Equal(ResizePlanner.UsableEnd(disk), option.RecoveryNewOffset + 750 * Mb);
        Assert.Equal(option.RecoveryNewOffset - c.Offset, option.MaxAfterMovingRecovery);
        Assert.True(option.MaxAfterMovingRecovery > 1990 * Gb);
        Assert.Equal(0, option.RecoveryNewOffset % Mb);
    }

    [Fact]
    public void Sizes_are_checked_against_what_is_possible()
    {
        var (disk, c) = ClonedToBigger();
        var option = ResizePlanner.Plan(disk, c, 190 * Gb, c.Length);

        Assert.Contains("can't be smaller", ResizePlanner.Check(option, 100 * Gb, false));
        Assert.Contains("Move the recovery partition", ResizePlanner.Check(option, 1000 * Gb, false));
        Assert.Null(ResizePlanner.Check(option, 1000 * Gb, true));
        Assert.Contains("at most", ResizePlanner.Check(option, 3000 * Gb, true));
        Assert.Null(ResizePlanner.Check(option, 300 * Gb, false)); // shrinking needs no move
        Assert.Contains("already", ResizePlanner.Check(option, c.Length, false));
    }

    [Fact]
    public void Free_space_right_after_means_nothing_is_blocked()
    {
        var c = DataPart(400 * Gb);
        var disk = new DiskInfo { Number = 0, Model = "SSD", Size = 1000 * Gb, IsGpt = true, Partitions = [Efi, Msr, c] };
        var option = ResizePlanner.Plan(disk, c, 190 * Gb, 999 * Gb);
        Assert.Null(option.Blocked);
        Assert.False(option.CanMoveRecovery);
        Assert.Null(ResizePlanner.Check(option, 900 * Gb, false));
    }

    [Fact]
    public void Recovery_already_at_the_end_offers_no_move()
    {
        var c = DataPart(400 * Gb);
        var disk = new DiskInfo { Number = 0, Model = "SSD", Size = 1000 * Gb, IsGpt = true, Partitions = [Efi, Msr, c] };
        var end = ResizePlanner.AlignDown(ResizePlanner.UsableEnd(disk) - 750 * Mb);
        disk = disk with { Partitions = [Efi, Msr, c, Recovery(end)] };
        var option = ResizePlanner.Plan(disk, c, 190 * Gb, end - c.Offset);
        Assert.Null(option.Blocked);
        Assert.False(option.CanMoveRecovery);
    }

    [Fact]
    public void Another_data_partition_in_the_way_is_named()
    {
        var c = DataPart(400 * Gb);
        var d = new PartitionInfo { Number = 4, Offset = c.End, Length = 500 * Gb, GptType = KnownPartitions.BasicData, FileSystem = "NTFS", DriveLetter = "D" };
        var disk = new DiskInfo { Number = 0, Model = "HDD", Size = 1000 * Gb, IsGpt = true, Partitions = [Efi, Msr, c, d] };
        var option = ResizePlanner.Plan(disk, c, 190 * Gb, c.Length);
        Assert.Contains("D: NTFS", option.Blocked);
        Assert.False(option.CanMoveRecovery);
    }

    [Fact]
    public void Only_ntfs_and_refs_data_partitions_are_resizable()
    {
        Assert.True(ResizePlanner.Resizable(DataPart(1 * Gb)));
        Assert.False(ResizePlanner.Resizable(Efi));
        Assert.False(ResizePlanner.Resizable(Recovery(0)));
        Assert.False(ResizePlanner.Resizable(new PartitionInfo { Number = 5, Offset = 0, Length = Gb, FileSystem = "FAT32" }));
    }
}
