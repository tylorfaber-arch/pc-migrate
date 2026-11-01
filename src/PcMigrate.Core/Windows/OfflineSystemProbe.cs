using System.Runtime.Versioning;
using Microsoft.Win32;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Offline;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Looks at the Windows on a pulled drive instead of the running one. Paths are reported
/// as the old PC knew them (C:\Users\tylor); <see cref="Offline"/> says where they are now.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfflineSystemProbe(OfflineWindows drive, IMigrationLog log) : ISystemProbe
{
    private const string UninstallKey = @"Microsoft\Windows\CurrentVersion\Uninstall";

    public string? CurrentUserSid => null;
    public OfflinePaths Offline => drive.Paths;
    OfflinePaths? ISystemProbe.Offline => drive.Paths;

    public Inventory GetInventory()
    {
        var users = ReadProfiles();
        return new Inventory
        {
            Machine = new MachineInfo { ComputerName = drive.ComputerName, OsDescription = drive.OsDescription },
            Users = users,
            Apps = ReadApps(users),
        };
    }

    private List<UserProfileInfo> ReadProfiles()
    {
        var result = new List<UserProfileInfo>();
        using var list = drive.Software.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (list is null) return result;
        foreach (var sid in list.GetSubKeyNames())
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
            using var key = list.OpenSubKey(sid);
            if (key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw) continue;
            var path = drive.Paths.Expand(raw);
            if (!Directory.Exists(drive.Paths.ToReadable(path))) continue;
            result.Add(new UserProfileInfo { UserName = Path.GetFileName(path.TrimEnd('\\')), Sid = sid, ProfilePath = path });
        }
        return result.OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<InstalledApp> ReadApps(List<UserProfileInfo> users)
    {
        var apps = new List<InstalledApp>();
        // A loaded hive isn't split into 32- and 64-bit views; the 32-bit programs are simply under WOW6432Node.
        using (var machine = drive.Software.OpenSubKey(UninstallKey))
            if (machine is not null) WindowsSystemProbe.ReadUninstallKey(machine, $@"HKLM\SOFTWARE\{UninstallKey}", "Machine", true, apps);
        using (var machine32 = drive.Software.OpenSubKey(@"WOW6432Node\" + UninstallKey))
            if (machine32 is not null) WindowsSystemProbe.ReadUninstallKey(machine32, $@"HKLM\SOFTWARE\WOW6432Node\{UninstallKey}", "Machine", false, apps);

        foreach (var user in users)
        {
            using var uninstall = drive.User(user, log)?.Root.OpenSubKey($@"Software\{UninstallKey}");
            if (uninstall is not null) WindowsSystemProbe.ReadUninstallKey(uninstall, $@"HKU\{user.Sid}\Software\{UninstallKey}", user.UserName, true, apps);
        }
        return WindowsSystemProbe.Dedupe(apps);
    }
}
