using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using PcMigrate.Core.Recovery;

namespace PcMigrate.Core.Windows;

/// <summary>Reads a volume (\\.\E:) sector by sector, for finding deleted files. Opened read-only.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class VolumeReader : IVolumeReader, IDisposable
{
    private readonly SafeFileHandle _handle;

    public VolumeReader(string letter)
    {
        var l = letter.TrimEnd('\\', ':', '/')[..1].ToUpperInvariant();
        SectorSize = GetDiskFreeSpace($@"{l}:\", out _, out var bytesPerSector, out _, out _) && bytesPerSector >= 512 ? (int)bytesPerSector : 512;
        _handle = RawDisk.Open($@"\\.\{l}:", write: false);
    }

    public int SectorSize { get; }

    public void Read(long offset, Span<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var n = RandomAccess.Read(_handle, buffer[done..], offset + done);
            if (n <= 0) { buffer[done..].Clear(); return; }
            done += n;
        }
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
}
