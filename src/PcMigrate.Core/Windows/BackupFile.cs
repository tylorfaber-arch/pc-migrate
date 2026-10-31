using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Reads files the way backup programs do. With the "back up files" right that administrators
/// have, Windows lets a program read any file whatever its permissions say. That matters for a
/// drive pulled from another PC, whose folders may only allow that PC's own accounts in.
/// Nothing on the drive is changed: no owners or permissions are touched.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class BackupFile
{
    private static readonly Lazy<bool> Privilege = new(() => EnablePrivilege("SeBackupPrivilege"));

    /// <summary>Turns the right on for this process. Folder listing uses it from then on as well.</summary>
    public static bool Enable() => Privilege.Value;

    public static FileStream? OpenRead(string path)
    {
        if (!Privilege.Value) return null;
        var handle = CreateFile(path, GenericRead, FileShareAll, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagSequentialScan | FlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new FileStream(handle, FileAccess.Read, 0, isAsync: true);
    }

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return false;
        using (token)
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            // AdjustTokenPrivileges "succeeds" even when the account doesn't hold the right.
            return Marshal.GetLastPInvokeError() == 0;
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagSequentialScan = 0x08000000;
    private const uint FlagOverlapped = 0x40000000;
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);
}
