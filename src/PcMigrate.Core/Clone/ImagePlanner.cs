namespace PcMigrate.Core.Clone;

/// <summary>The sums behind drive images, kept apart from Windows so they can be tested anywhere.</summary>
public static class ImagePlanner
{
    // Room for the virtual disk's own bookkeeping, and for files that change while it runs.
    private const long Headroom = 2L * 1024 * 1024 * 1024;

    /// <summary>About how big an image of this drive will be: the space in use, plus some room.</summary>
    public static long NeededBytes(DiskInfo source) =>
        source.Partitions.Sum(p => p.IsNtfs && p.UsedBytes is { } used ? used : p.Length) + Headroom;

    /// <summary>How an image fits on a drive. Like a clone, except an image can't be shrunk first.</summary>
    public static ClonePlan PlanRestore(DiskInfo image, DiskInfo target)
    {
        var plan = ClonePlanner.Plan(image, target);
        return plan.ShrinkSourceBy > 0
            ? plan with { Problem = $"The drive is {DiskInfo.FormatSize(plan.ShrinkSourceBy)} too small for this image. Use a drive at least as big as the one the image was made from.", ShrinkSourceBy = 0 }
            : plan;
    }
}
