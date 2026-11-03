using System.Management;
using System.Runtime.Versioning;
using PcMigrate.Core.Health;

namespace PcMigrate.Core.Windows;

/// <summary>Reads each disk's health from Windows' storage service (which gathers the drives' SMART data).</summary>
[SupportedOSPlatform("windows")]
public static class HealthReader
{
    private const string Storage = @"root\Microsoft\Windows\Storage";

    public static List<DriveHealthReport> All()
    {
        var counters = Counters();
        var result = new List<DriveHealthReport>();
        try
        {
            using var searcher = new ManagementObjectSearcher(Storage, "SELECT DeviceId, FriendlyName, HealthStatus, OperationalStatus, MediaType FROM MSFT_PhysicalDisk");
            foreach (var disk in searcher.Get().OfType<ManagementObject>())
            {
                using (disk)
                {
                    if (!int.TryParse(disk["DeviceId"] as string, out var number)) continue;
                    counters.TryGetValue(disk["DeviceId"] as string ?? "", out var c);
                    var readings = new HealthReadings
                    {
                        WindowsHealth = disk["HealthStatus"] is { } h ? Convert.ToInt32(h) : null,
                        OperationalStatus = (disk["OperationalStatus"] as ushort[] ?? []).Select(s => (int)s).ToList(),
                        IsSsd = disk["MediaType"] is { } m && Convert.ToInt32(m) == 4,
                        TemperatureC = c?.Temperature,
                        WearPercent = c?.Wear,
                        ReadErrorsUncorrected = c?.ReadUncorrected,
                        WriteErrorsUncorrected = c?.WriteUncorrected,
                        PowerOnHours = c?.Hours,
                    };
                    var (level, summary, details) = DriveHealth.Assess(readings);
                    result.Add(new DriveHealthReport(number, (disk["FriendlyName"] as string)?.Trim() ?? $"Disk {number}", level, summary, details));
                }
            }
        }
        catch (ManagementException) { }
        return result.OrderBy(r => r.DiskNumber).ToList();
    }

    /// <summary>The health of the disk a drive letter (like E:\) is on, or null if it can't be told.</summary>
    public static DriveHealthReport? ForLetter(string root)
    {
        var letter = root.TrimEnd('\\', '/', ':');
        var disk = Disks.List().FirstOrDefault(d => d.Partitions.Any(p => string.Equals(p.DriveLetter, letter, StringComparison.OrdinalIgnoreCase)));
        return disk is null ? null : All().FirstOrDefault(h => h.DiskNumber == disk.Number);
    }

    private sealed record Counter(int? Temperature, int? Wear, long? ReadUncorrected, long? WriteUncorrected, long? Hours);

    /// <summary>The reliability counters (temperature, wear, errors, hours), by disk. Many USB adapters don't pass them through.</summary>
    private static Dictionary<string, Counter> Counters()
    {
        var result = new Dictionary<string, Counter>();
        try
        {
            using var searcher = new ManagementObjectSearcher(Storage, "SELECT * FROM MSFT_PhysicalDisk");
            foreach (var disk in searcher.Get().OfType<ManagementObject>())
            {
                using (disk)
                {
                    try
                    {
                        foreach (var rc in disk.GetRelated("MSFT_StorageReliabilityCounter").OfType<ManagementObject>())
                        {
                            using (rc)
                            {
                                static long? L(object? o) => o is null ? null : Convert.ToInt64(o);
                                static int? I(object? o) => o is null ? null : Convert.ToInt32(o);
                                var temp = I(rc["Temperature"]);
                                result[disk["DeviceId"] as string ?? ""] = new Counter(
                                    temp is > 0 and < 150 ? temp : null, I(rc["Wear"]) is { } w && w > 0 ? w : null,
                                    L(rc["ReadErrorsUncorrected"]), L(rc["WriteErrorsUncorrected"]), L(rc["PowerOnHours"]));
                            }
                        }
                    }
                    catch (ManagementException) { }
                }
            }
        }
        catch (ManagementException) { }
        return result;
    }
}
