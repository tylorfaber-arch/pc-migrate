using PcMigrate.Core.Clone;

namespace PcMigrate.Tests;

public class ClonePlannerTests
{
    private const long MB = 1024 * 1024, GB = 1024 * MB;

    // A typical Windows 11 disk: EFI, reserved, Windows, recovery at the end.
    private static DiskInfo Source(long size = 512 * GB, long used = 100 * GB) => new()
    {
        Number = 0, Model = "Old SSD", Size = size, IsGpt = true, IsSystem = true,
        Partitions =
        [
            new() { Number = 1, Offset = 1 * MB, Length = 260 * MB, GptType = KnownPartitions.Efi, FileSystem = "FAT32" },
            new() { Number = 2, Offset = 261 * MB, Length = 16 * MB, GptType = KnownPartitions.MicrosoftReserved },
            new() { Number = 3, Offset = 277 * MB, Length = size - 277 * MB - 1024 * MB, GptType = KnownPartitions.BasicData, FileSystem = "NTFS", UsedBytes = used, DriveLetter = "C" },
            new() { Number = 4, Offset = size - 1024 * MB, Length = 1000 * MB, GptType = KnownPartitions.Recovery, FileSystem = "NTFS", UsedBytes = 600 * MB },
        ],
    };

    private static DiskInfo Target(long size, int number = 1) => new() { Number = number, Model = "New SSD", Size = size, IsGpt = true };

    [Fact]
    public void BiggerTargetGrowsWindowsAndMovesRecoveryToTheEnd()
    {
        var plan = ClonePlanner.Plan(Source(), Target(1024 * GB));
        Assert.True(plan.CanRun, plan.Problem);
        Assert.Equal(4, plan.Partitions.Count);
        Assert.Equal(1 * MB, plan.Partitions[0].Offset);
        var windows = plan.Partitions[2];
        Assert.True(windows.Grows);
        var recovery = plan.Partitions[3];
        Assert.Equal(windows.Offset + windows.Length, recovery.Offset);
        Assert.True(recovery.Offset + recovery.Length <= 1024 * GB - 33 * 512);
        Assert.All(plan.Partitions, p => Assert.Equal(0, p.Offset % MB));
    }

    [Fact]
    public void OnlyUsedSpaceIsCopiedForNtfs()
    {
        var plan = ClonePlanner.Plan(Source(), Target(1024 * GB));
        Assert.Equal(260 * MB + 16 * MB + 100 * GB + 600 * MB, plan.BytesToCopy);
    }

    [Fact]
    public void SmallerTargetThatTheDataFitsSuggestsAShrink()
    {
        var plan = ClonePlanner.Plan(Source(512 * GB, used: 100 * GB), Target(256 * GB));
        Assert.False(plan.CanRun);
        Assert.True(plan.ShrinkSourceBy > 256 * GB - 2 * GB && plan.ShrinkSourceBy < 256 * GB + 2 * GB);
        Assert.Contains("Shrink C:", plan.Problem);
    }

    [Fact]
    public void AfterShrinkingTheSourceItFits()
    {
        var first = ClonePlanner.Plan(Source(), Target(256 * GB));
        var src = Source();
        var shrunk = src with
        {
            Partitions = src.Partitions.Select(p => p.Number == 3 ? p with { Length = p.Length - first.ShrinkSourceBy } : p).ToList(),
        };
        Assert.True(ClonePlanner.Plan(shrunk, Target(256 * GB)).CanRun);
    }

    [Fact]
    public void TargetTooSmallForTheData()
    {
        var plan = ClonePlanner.Plan(Source(512 * GB, used: 300 * GB), Target(256 * GB));
        Assert.False(plan.CanRun);
        Assert.Equal(0, plan.ShrinkSourceBy);
        Assert.Contains("too small", plan.Problem);
    }

    [Fact]
    public void RefusesTheRunningSystemDriveAsTarget() =>
        Assert.False(ClonePlanner.Plan(Source() with { Number = 1, IsSystem = false }, Source()).CanRun);

    [Fact]
    public void RefusesDifferentSectorSizes() =>
        Assert.Contains("sector", ClonePlanner.Plan(Source(), Target(1024 * GB) with { SectorSize = 4096 }).Problem);
}

public class LayoutCodecTests
{
    [Fact]
    public void GptTableRoundTrips()
    {
        var efi = new PartitionInfo { Number = 1, Offset = 1 << 20, Length = 260 << 20, GptType = KnownPartitions.Efi, GptName = "EFI system partition", GptAttributes = 0x8000000000000001 };
        var data = new PartitionInfo { Number = 2, Offset = 261 << 20, Length = 100L << 30, GptType = KnownPartitions.BasicData, GptName = "Basic data partition" };
        var layout = new LayoutCodec.Layout(true, Guid.NewGuid(), 0, 17408, 1L << 40, 128, []);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var bytes = LayoutCodec.Encode(layout, [(efi, efi.Offset, efi.Length, ids[0]), (data, data.Offset, 200L << 30, ids[1])]);
        Assert.Equal(LayoutCodec.HeaderSize + 2 * LayoutCodec.EntrySize, bytes.Length);

        var back = LayoutCodec.Decode(bytes);
        Assert.True(back.IsGpt);
        Assert.Equal(layout.DiskId, back.DiskId);
        Assert.Equal(2, back.Partitions.Count);
        Assert.Equal(KnownPartitions.Efi, back.Partitions[0].GptType);
        Assert.Equal(ids[0], back.Partitions[0].GptId);
        Assert.Equal("EFI system partition", back.Partitions[0].GptName);
        Assert.Equal(0x8000000000000001, back.Partitions[0].GptAttributes);
        Assert.Equal(200L << 30, back.Partitions[1].Length);
    }

    [Fact]
    public void MbrTableIsPaddedToFourSlots()
    {
        var p = new PartitionInfo { Number = 1, Offset = 1 << 20, Length = 50L << 30, MbrType = 0x07, MbrBootable = true };
        var bytes = LayoutCodec.Encode(new LayoutCodec.Layout(false, Guid.Empty, 0xDEADBEEF, 0, 0, 0, []), [(p, p.Offset, p.Length, Guid.Empty)]);
        Assert.Equal(LayoutCodec.HeaderSize + 4 * LayoutCodec.EntrySize, bytes.Length);
        var back = LayoutCodec.Decode(bytes);
        Assert.False(back.IsGpt);
        Assert.Equal(0xDEADBEEFu, back.MbrSignature);
        var only = Assert.Single(back.Partitions);
        Assert.True(only.MbrBootable);
        Assert.Equal((byte)0x07, only.MbrType);
    }
}
