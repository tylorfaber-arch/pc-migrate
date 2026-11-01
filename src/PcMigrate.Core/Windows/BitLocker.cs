using System.Management;
using System.Runtime.Versioning;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Drives encrypted with BitLocker (or Windows "device encryption") show up locked when
/// plugged into another PC. The 48-digit recovery key unlocks them; it's at
/// aka.ms/myrecoverykey for the Microsoft account the old PC was signed in with.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BitLocker
{
    private const string Namespace = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

    public static bool IsLocked(string root)
    {
        try
        {
            using var volume = Find(root);
            if (volume is null) return false;
            using var result = volume.InvokeMethod("GetLockStatus", null, null);
            return result?["LockStatus"] is uint status && status == 1;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>Returns null on success, or what went wrong.</summary>
    public static string? Unlock(string root, string recoveryKey)
    {
        var key = new string(recoveryKey.Where(c => char.IsDigit(c) || c == '-').ToArray());
        var digits = key.Count(char.IsDigit);
        if (digits != 48) return "A recovery key is 48 digits, in 8 groups of 6. Check it and try again.";
        if (!key.Contains('-')) key = string.Join("-", Enumerable.Range(0, 8).Select(i => key.Substring(i * 6, 6)));
        try
        {
            using var volume = Find(root);
            if (volume is null) return "Windows can't find that drive any more. Is it still plugged in?";
            using var args = volume.GetMethodParameters("UnlockWithNumericalPassword");
            args["NumericalPassword"] = key;
            using var result = volume.InvokeMethod("UnlockWithNumericalPassword", args, null);
            return result?["ReturnValue"] is uint code && code == 0
                ? null
                : "That recovery key didn't unlock the drive. Make sure it's the key for this drive (the Key ID shown when it asks should match).";
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return $"Couldn't unlock the drive: {ex.Message}";
        }
    }

    private static ManagementObject? Find(string root)
    {
        var letter = root.TrimEnd('\\')[..2].ToUpperInvariant();
        using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{letter}'");
        return searcher.Get().OfType<ManagementObject>().FirstOrDefault();
    }
}
