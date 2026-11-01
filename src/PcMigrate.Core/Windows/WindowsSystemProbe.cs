using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;

namespace PcMigrate.Core.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemProbe(IMigrationLog log) : ISystemProbe
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public string? CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value;

    public Inventory GetInventory()
    {
        var users = ReadProfiles();
        return new Inventory
        {
            Machine = new MachineInfo
            {
                ComputerName = Environment.MachineName,
                OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            },
            Users = users,
            Apps = ReadApps(users),
        };
    }

    private static List<UserProfileInfo> ReadProfiles()
    {
        var result = new List<UserProfileInfo>();
        using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        if (list is null) return result;

        foreach (var sid in list.GetSubKeyNames())
        {
            // S-1-5-21-... are real people. Others are SYSTEM, LocalService and so on.
            // A ".bak" suffix means a broken duplicate profile.
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;

            using var key = list.OpenSubKey(sid);
            if (key?.GetValue("ProfileImagePath") is not string raw) continue;
            var path = Environment.ExpandEnvironmentVariables(raw);
            if (!Directory.Exists(path)) continue;

            result.Add(new UserProfileInfo { UserName = Path.GetFileName(path), Sid = sid, ProfilePath = path });
        }
        return result.OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<InstalledApp> ReadApps(List<UserProfileInfo> users)
    {
        var apps = new List<InstalledApp>();

        using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            ReadUninstall(hklm64, $@"HKLM\{UninstallKey}", "Machine", Environment.Is64BitOperatingSystem, apps);
        if (Environment.Is64BitOperatingSystem)
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            ReadUninstall(hklm32, $@"HKLM\{UninstallKey.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\")}", "Machine", false, apps);
        }

        foreach (var user in users)
        {
            using var hive = UserHive.Open(user, writable: false, log);
            if (hive is not null) ReadUninstall(hive.Root, $@"HKU\{user.Sid}\{UninstallKey}", user.UserName, true, apps);
        }

        // The same program can show up in more than one place; keep one row each.
        return apps
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version, a.Scope))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadUninstall(RegistryKey root, string keyPath, string scope, bool is64, List<InstalledApp> into)
    {
        using var uninstall = root.OpenSubKey(UninstallKey);
        if (uninstall is null) return;

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(name);
            if (key is null) continue;
            if (key.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName)) continue;
            // Hidden components, and updates that belong to another program.
            if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
            if (key.GetValue("ParentKeyName") is not null) continue;
            if (key.GetValue("ReleaseType") is string rt &&
                (rt.Contains("Update", StringComparison.OrdinalIgnoreCase) || rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                continue;

            into.Add(new InstalledApp
            {
                Name = displayName.Trim(),
                Version = key.GetValue("DisplayVersion") as string,
                Publisher = key.GetValue("Publisher") as string,
                InstallLocation = key.GetValue("InstallLocation") as string,
                UninstallString = key.GetValue("UninstallString") as string,
                EstimatedSizeKb = key.GetValue("EstimatedSize") is int size ? size : null,
                Scope = scope,
                Is64Bit = is64,
                RegistryKey = $@"{keyPath}\{name}",
                IsMsi = key.GetValue("WindowsInstaller") is int msi && msi == 1,
            });
        }
    }
}
