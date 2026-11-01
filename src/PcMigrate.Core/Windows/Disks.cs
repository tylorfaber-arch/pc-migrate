using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PcMigrate.Core.Clone;

namespace PcMigrate.Core.Windows;

/// <summary>Lists physical disks with their partitions, from Windows' storage service and the disks' own partition tables.</summary>
[SupportedOSPlatform("windows")]
public static partial class Disks
{
    private const string Storage = @"root\Microsoft\Windows\Storage";

    public static List<DiskInfo> List()
    {
        var volumes = MountedVolumes();
        var result = new List<DiskInfo>();
        using var searcher = new ManagementObjectSearcher(Storage, "SELECT * FROM MSFT_Disk");
        foreach (var disk in searcher.Get().OfType<ManagementObject>())
        {
            using (disk)
            {
                var number = Convert.ToInt32(disk["Number"]);
                List<PartitionInfo> partitions = [];
                var gpt = Convert.ToInt32(disk["PartitionStyle"]) == 2;
                try
                {
                    using var handle = RawDisk.Open(RawDisk.PhysicalDrive(number), write: false);
                    var layout = RawDisk.ReadLayout(handle);
                    gpt = layout.IsGpt;
                    partitions = layout.Partitions
                        .Where(p => p.Length > 0)
                        .Select(p => Describe(number, p, volumes))
                        .ToList();
                }
                catch (IOException)
                {
                    // An empty or unreadable disk can still be a target.
                }
                result.Add(new DiskInfo
                {
                    Number = number,
                    Model = (disk["FriendlyName"] as string)?.Trim() ?? $"Disk {number}",
                    Serial = (disk["SerialNumber"] as string)?.Trim() is { Length: > 0 } s ? s : null,
                    Size = Convert.ToInt64(disk["Size"]),
                    Bus = BusName(Convert.ToInt32(disk["BusType"])),
                    IsSystem = disk["IsSystem"] is true || disk["IsBoot"] is true,
                    IsGpt = gpt,
                    SectorSize = Convert.ToInt32(disk["LogicalSectorSize"]),
                    Partitions = partitions,
                });
            }
        }
        return result.OrderBy(d => d.Number).ToList();
    }

    private sealed record Volume(int Disk, long Offset, string Path, string? Letter);

    /// <summary>Every mounted volume, and which disk and offset it sits on.</summary>
    private static List<Volume> MountedVolumes()
    {
        var result = new List<Volume>();
        using var searcher = new ManagementObjectSearcher(Storage, "SELECT DiskNumber, Offset, AccessPaths, DriveLetter FROM MSFT_Partition");
        foreach (var p in searcher.Get().OfType<ManagementObject>())
        {
            using (p)
            {
                var paths = p["AccessPaths"] as string[] ?? [];
                var volume = paths.FirstOrDefault(a => a.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
                if (volume is null) continue;
                var letter = p["DriveLetter"] is char c && c != '\0' ? c.ToString() : null;
                result.Add(new Volume(Convert.ToInt32(p["DiskNumber"]), Convert.ToInt64(p["Offset"]), volume.TrimEnd('\\'), letter));
            }
        }
        return result;
    }

    private static PartitionInfo Describe(int disk, PartitionInfo p, List<Volume> volumes)
    {
        var volume = volumes.FirstOrDefault(v => v.Disk == disk && v.Offset == p.Offset);
        if (volume is null) return p;
        string? fs = null;
        long? used = null;
        var fsName = new char[64];
        if (GetVolumeInformation(volume.Path + "\\", null, 0, out _, out _, out _, fsName, fsName.Length))
            fs = new string(fsName).TrimEnd('\0');
        if (GetDiskFreeSpaceEx(volume.Path + "\\", out _, out var total, out var free)) used = (long)(total - free);
        return p with { FileSystem = fs, UsedBytes = used, DriveLetter = volume.Letter, VolumePath = volume.Path };
    }

    private static string BusName(int bus) => bus switch
    {
        7 => "USB",
        11 => "SATA",
        17 => "NVMe",
        8 => "RAID",
        10 => "SAS",
        12 => "SD card",
        15 => "Virtual",
        _ => "",
    };

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(string root, char[]? name, int nameSize, out uint serial, out uint maxComponent, out uint flags, [Out] char[] fileSystem, int fileSystemSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong free);
}
