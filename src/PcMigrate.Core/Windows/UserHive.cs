using System.Runtime.Versioning;
using Microsoft.Win32;
using PcMigrate.Core.Models;
using PcMigrate.Core.Offline;

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
    private readonly string? _tempFolder;

    private UserHive(RegistryKey root, string? mountName, string sid, string? tempFolder = null)
    {
        Root = root;
        _mountName = mountName;
        _tempFolder = tempFolder;
        RootPath = $@"HKU\{mountName ?? sid}";
    }

    public RegistryKey Root { get; }

    /// <summary>Where this hive is in the registry right now, as reg.exe writes it (HKU\...).</summary>
    public string RootPath { get; }

    public static UserHive? Open(UserProfileInfo user, bool writable, IMigrationLog log, OfflinePaths? offline = null)
    {
        if (offline is not null) return OpenOffline(user, offline, log);

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

    /// <summary>
    /// A user on a pulled drive. Their NTUSER.DAT is copied off the drive and the copy is
    /// loaded, so nothing is written to the old disk (loading a hive can update it).
    /// </summary>
    private static UserHive? OpenOffline(UserProfileInfo user, OfflinePaths offline, IMigrationLog log)
    {
        var hivePath = Path.Combine(offline.ToReadable(user.ProfilePath), "NTUSER.DAT");
        if (!File.Exists(hivePath))
        {
            log.Warn($"No registry file for user '{user.UserName}' on the drive, skipping their settings.");
            return null;
        }
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pcmigrate-hive-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var copy = HiveFiles.CopyTo(hivePath, temp);
            var mount = "PCMIGRATE_" + Guid.NewGuid().ToString("N")[..8];
            var result = ProcessRunner.Run("reg.exe", "load", $@"HKU\{mount}", copy);
            if (result.ExitCode != 0)
            {
                log.Warn($"Could not open settings for user '{user.UserName}' on the drive: {result.Output}");
                Directory.Delete(temp, recursive: true);
                return null;
            }
            var key = Registry.Users.OpenSubKey(mount, writable: false);
            if (key is null)
            {
                Unload(mount);
                Directory.Delete(temp, recursive: true);
                return null;
            }
            return new UserHive(key, mount, user.Sid, temp);
        }
        catch (IOException ex)
        {
            log.Warn($"Could not copy the registry file for '{user.UserName}' off the drive: {ex.Message}");
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
            return null;
        }
    }

    public void Dispose()
    {
        Root.Dispose();
        if (_mountName is not null) Unload(_mountName);
        if (_tempFolder is not null)
        {
            try { Directory.Delete(_tempFolder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void Unload(string mount)
    {
        // Windows refuses to unload while any handle into the hive is still open,
        // and .NET may not have released them yet.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var path = mount.Contains('\\') ? mount : $@"HKU\{mount}";
            if (ProcessRunner.Run("reg.exe", "unload", path).ExitCode == 0) return;
            Thread.Sleep(500);
        }
    }
}

/// <summary>Copies a registry hive file and its transaction logs, so a hive left unsaved by a crash loads complete.</summary>
internal static class HiveFiles
{
    public static string CopyTo(string hivePath, string folder)
    {
        var name = Path.GetFileName(hivePath);
        var copy = Path.Combine(folder, name);
        CopyRaw(hivePath, copy);
        foreach (var suffix in new[] { ".LOG1", ".LOG2", ".LOG" })
        {
            var log = hivePath + suffix;
            if (File.Exists(log)) CopyRaw(log, copy + suffix);
        }
        return copy;
    }

    // Hive files are hidden and system; read them with backup rights in case permissions on the old disk get in the way.
    private static void CopyRaw(string from, string to)
    {
        using var input = Storage.HashingCopy.OpenForRead(from);
        using var output = File.Create(to);
        input.CopyTo(output);
    }
}
