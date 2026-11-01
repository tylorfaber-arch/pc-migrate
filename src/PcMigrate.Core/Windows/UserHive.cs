using System.Runtime.Versioning;
using Microsoft.Win32;
using PcMigrate.Core.Models;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Gives access to a user's part of the registry (HKEY_CURRENT_USER for that user).
/// If the user is signed in it is already under HKEY_USERS\{SID}. If not, their
/// NTUSER.DAT file is loaded temporarily and unloaded on Dispose.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UserHive : IDisposable
{
    private readonly string? _mountName;

    private UserHive(RegistryKey root, string? mountName, string sid)
    {
        Root = root;
        _mountName = mountName;
        RootPath = $@"HKU\{mountName ?? sid}";
    }

    public RegistryKey Root { get; }

    /// <summary>Where this hive is in the registry right now, as reg.exe writes it (HKU\...).</summary>
    public string RootPath { get; }

    public static UserHive? Open(UserProfileInfo user, bool writable, IMigrationLog log)
    {
        var loaded = Registry.Users.OpenSubKey(user.Sid, writable);
        if (loaded is not null) return new UserHive(loaded, null, user.Sid);

        var hivePath = Path.Combine(user.ProfilePath, "NTUSER.DAT");
        if (!File.Exists(hivePath))
        {
            log.Warn($"No registry file for user '{user.UserName}', skipping their settings.");
            return null;
        }

        var mount = "PCMIGRATE_" + Guid.NewGuid().ToString("N")[..8];
        var result = ProcessRunner.Run("reg.exe", "load", $@"HKU\{mount}", hivePath);
        if (result.ExitCode != 0)
        {
            log.Warn($"Could not open settings for user '{user.UserName}': {result.Output}");
            return null;
        }

        var key = Registry.Users.OpenSubKey(mount, writable);
        if (key is null)
        {
            Unload(mount);
            return null;
        }
        return new UserHive(key, mount, user.Sid);
    }

    public void Dispose()
    {
        Root.Dispose();
        if (_mountName is not null) Unload(_mountName);
    }

    private static void Unload(string mount)
    {
        // Windows refuses to unload while any handle into the hive is still open,
        // and .NET may not have released them yet.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (ProcessRunner.Run("reg.exe", "unload", $@"HKU\{mount}").ExitCode == 0) return;
            Thread.Sleep(500);
        }
    }
}
