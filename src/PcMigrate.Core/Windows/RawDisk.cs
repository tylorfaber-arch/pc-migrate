using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Direct access to disks and volumes, below the file system: reading and writing sectors,
/// the partition table, and NTFS's map of which clusters are in use.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class RawDisk
{
    public const uint IoctlGetDriveLayoutEx = 0x00070050;
    public const uint IoctlSetDriveLayoutEx = 0x0007C054;
    public const uint IoctlCreateDisk = 0x0007C058;
    public const uint IoctlUpdateProperties = 0x00070140;
    public const uint IoctlGetLengthInfo = 0x0007405C;
    public const uint FsctlGetVolumeBitmap = 0x0009006F;

    public static SafeFileHandle Open(string path, bool write)
    {
        var handle = CreateFile(path, write ? GenericRead | GenericWrite : GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | (write ? FlagWriteThrough : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Couldn't open {path}: {new Win32Exception(error).Message}");
        }
        return handle;
    }

    public static string PhysicalDrive(int number) => $@"\\.\PhysicalDrive{number}";

    public static byte[] Ioctl(SafeFileHandle handle, uint code, byte[]? input, int outputSize)
    {
        while (true)
        {
            var output = new byte[outputSize];
            if (DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out var returned, IntPtr.Zero))
                return output.AsSpan(0, returned).ToArray();
            var error = Marshal.GetLastPInvokeError();
            // ERROR_INSUFFICIENT_BUFFER / ERROR_MORE_DATA: try again with more room.
            if (error is 122 or 234 && outputSize < 64 * 1024 * 1024) { outputSize *= 4; continue; }
            throw new IOException(new Win32Exception(error).Message);
        }
    }

    public static long Length(SafeFileHandle handle) =>
        BinaryPrimitives.ReadInt64LittleEndian(Ioctl(handle, IoctlGetLengthInfo, null, 8));

    public static Clone.LayoutCodec.Layout ReadLayout(SafeFileHandle disk) =>
        Clone.LayoutCodec.Decode(Ioctl(disk, IoctlGetDriveLayoutEx, null, 48 + 144 * 128));

    /// <summary>Starts a blank partition table (GPT or MBR) on a disk.</summary>
    public static void CreateDisk(SafeFileHandle disk, bool gpt, Guid diskId, uint mbrSignature)
    {
        var input = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(input, gpt ? 1 : 0);
        if (gpt)
        {
            diskId.TryWriteBytes(input.AsSpan(4, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(20), 128);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), mbrSignature);
        }
        Ioctl(disk, IoctlCreateDisk, input, 0);
    }

    /// <summary>
    /// NTFS's own record of which clusters hold data, one bit per cluster. Copying only those
    /// is what makes a clone take minutes rather than hours on a mostly empty drive.
    /// </summary>
    public static IEnumerable<(long FirstCluster, long Count)> UsedClusterRuns(SafeFileHandle volume)
    {
        long next = 0;
        while (true)
        {
            var input = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(input, next);
            var output = new byte[16 + 4 * 1024 * 1024];
            var ok = DeviceIoControl(volume, FsctlGetVolumeBitmap, input, 8, output, output.Length, out var returned, IntPtr.Zero);
            var error = ok ? 0 : Marshal.GetLastPInvokeError();
            if (!ok && error != 234) throw new IOException($"Couldn't read the drive's map of used space: {new Win32Exception(error).Message}");

            var start = BinaryPrimitives.ReadInt64LittleEndian(output);
            var total = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8)); // clusters from start to the end of the volume
            var bits = Math.Min(total, (long)(returned - 16) * 8);
            long runStart = -1;
            for (long i = 0; i < bits; i++)
            {
                var used = (output[16 + (i >> 3)] & (1 << (int)(i & 7))) != 0;
                if (used && runStart < 0) runStart = i;
                else if (!used && runStart >= 0) { yield return (start + runStart, i - runStart); runStart = -1; }
            }
            if (runStart >= 0) yield return (start + runStart, bits - runStart);
            if (ok) yield break;
            next = start + bits;
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagWriteThrough = 0x80000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
}
