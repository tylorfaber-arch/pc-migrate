using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcMigrate.Core.Windows;

/// <summary>Sends files to the Recycle Bin, so removing them can be undone.</summary>
[SupportedOSPlatform("windows")]
public static class RecycleBin
{
    public static void Send(string path)
    {
        var op = new ShFileOp
        {
            Func = FoDelete,
            From = Path.GetFullPath(path) + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        var result = SHFileOperation(ref op);
        if (result != 0) throw new IOException($"Windows couldn't move it to the Recycle Bin (error {result}).");
        if (op.Aborted) throw new IOException("Moving it to the Recycle Bin was stopped.");
        if (File.Exists(path)) throw new IOException("It's still there after moving it to the Recycle Bin (it may be in use).");
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOp op);
}
