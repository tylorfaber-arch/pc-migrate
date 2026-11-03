using PcMigrate.Core.Cleanup;
using PcMigrate.Core.Clone;
using Xunit;

namespace PcMigrate.Tests;

public class WipeTests
{
    private sealed class MemoryDisk(int size) : IBlockDevice
    {
        public byte[] Data { get; } = Enumerable.Range(0, size).Select(i => (byte)(i % 251 + 1)).ToArray();
        public long Length => Data.Length;
        public int Writes { get; private set; }
        public bool DropWritesAfterFirstPass { get; init; }
        public void Write(ReadOnlySpan<byte> data, long offset)
        {
            Writes++;
            if (DropWritesAfterFirstPass && offset == 0 && Writes > 1) return; // a drive quietly ignoring writes at the start
            data.CopyTo(Data.AsSpan((int)offset));
        }
        public int Read(Span<byte> buffer, long offset)
        {
            var n = (int)Math.Min(buffer.Length, Length - offset);
            Data.AsSpan((int)offset, n).CopyTo(buffer);
            return n;
        }
    }

    [Theory]
    [InlineData(WipePasses.One)]
    [InlineData(WipePasses.Three)]
    public void Wiping_a_device_leaves_only_zeros(WipePasses passes)
    {
        var disk = new MemoryDisk(9 * 1024 * 1024 + 4096); // not a whole number of chunks
        long last = 0;
        Wipe.Device(disk, passes, new SyncProgress<long>(b => last = b), CancellationToken.None);
        Assert.True(disk.Data.All(b => b == 0));
        Assert.Equal(disk.Length * (int)passes, last);
    }

    [Fact]
    public void A_drive_that_ignores_writes_fails_the_check()
    {
        var disk = new MemoryDisk(8 * 1024 * 1024) { DropWritesAfterFirstPass = true };
        // One pass writes offset 0 once, so make it three: the zero pass at the start is dropped.
        Assert.Throws<IOException>(() => Wipe.Device(disk, WipePasses.Three, null, CancellationToken.None));
    }

    [Fact]
    public void Shredding_overwrites_then_removes_files_and_folders()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Taxes");
        Directory.CreateDirectory(Path.Combine(folder, "2024"));
        File.WriteAllText(Path.Combine(folder, "2024", "return.pdf"), new string('x', 50_000));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "secret");
        var single = Path.Combine(dir.Path, "passwords.txt");
        File.WriteAllText(single, "hunter2");
        File.SetAttributes(single, FileAttributes.ReadOnly);
        var keep = Path.Combine(dir.Path, "keep.txt");
        File.WriteAllText(keep, "stay");

        var result = Wipe.Files([folder, single], WipePasses.One, null, CancellationToken.None);

        Assert.Equal(3, result.Files);
        Assert.Equal(50_000 + 6 + 7, result.Bytes);
        Assert.Empty(result.Problems);
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(single));
        Assert.True(File.Exists(keep));
        Assert.Single(Directory.GetFileSystemEntries(dir.Path)); // no scrambled leftovers
    }

    [Fact]
    public void Shredding_a_file_overwrites_its_contents_before_deleting()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "a.bin");
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0xAB, 10_000).ToArray());
        Assert.Equal(10_000, Wipe.ShredFile(file, WipePasses.One, CancellationToken.None));
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32", true)]
    [InlineData(@"C:\Program Files\App", true)]
    [InlineData(@"C:\Users\Sam", true)]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"C:\Users\Sam\Documents\Old taxes", false)]
    [InlineData(@"E:\Client files", false)]
    public void Refuses_system_places_and_whole_profiles(string path, bool refused) =>
        Assert.Equal(refused, Wipe.ProtectedPlace(path) is not null);

    [Fact]
    public void The_running_windows_drive_is_refused_and_ssds_get_a_note()
    {
        var system = new DiskInfo { Number = 0, Model = "Samsung SSD 980", Size = 500_000_000_000, IsSystem = true, Bus = "NVMe" };
        var usb = new DiskInfo { Number = 2, Model = "WD Elements", Size = 1_000_000_000_000, Bus = "USB" };
        Assert.NotNull(Wipe.CheckDisk(system));
        Assert.Null(Wipe.CheckDisk(usb));
        Assert.NotNull(Wipe.Note(system));
        Assert.Null(Wipe.Note(usb));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
