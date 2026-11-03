using PcMigrate.Core.Cleanup;
using Xunit;

namespace PcMigrate.Tests;

public class DuplicateTests
{
    private static string Write(string root, string relative, byte[] data, DateTime? modified = null)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        if (modified is { } m) File.SetLastWriteTime(path, m);
        return path;
    }

    private static byte[] Bytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void Finds_identical_files_whatever_their_names()
    {
        using var dir = new TempDir();
        var photo = Bytes(300_000, 1);
        var a = Write(dir.Path, "Pictures/beach.jpg", photo, new DateTime(2020, 1, 1));
        var b = Write(dir.Path, "Desktop/IMG_0001.jpg", photo, new DateTime(2023, 1, 1));
        var c = Write(dir.Path, "Downloads/beach (1).jpg", photo, new DateTime(2019, 1, 1));
        Write(dir.Path, "Pictures/other.jpg", Bytes(300_000, 2));

        var groups = DuplicateFinder.Find([dir.Path], new DuplicateFinder.Options { MinSize = 1 });

        var g = Assert.Single(groups);
        Assert.Equal(3, g.Copies.Count);
        Assert.Equal(600_000, g.Wasted);
        // The oldest copy that isn't in Downloads or named like a copy is the one to keep.
        Assert.Equal(Path.GetFullPath(a), g.Keep);
        Assert.Equal(Path.GetFullPath(c), g.Copies[^1].Path);
        Assert.Contains(g.Copies, x => x.Path == Path.GetFullPath(b));
    }

    [Fact]
    public void Same_start_but_different_end_is_not_a_duplicate()
    {
        using var dir = new TempDir();
        var one = Bytes(200_000, 3);
        var two = (byte[])one.Clone();
        two[^1] ^= 0xFF; // same size, same first 64 KB, different last byte
        Write(dir.Path, "a.bin", one);
        Write(dir.Path, "b.bin", two);

        Assert.Empty(DuplicateFinder.Find([dir.Path], new DuplicateFinder.Options { MinSize = 1 }));
    }

    [Fact]
    public void Small_files_and_the_same_folder_twice_are_handled()
    {
        using var dir = new TempDir();
        Write(dir.Path, "x/one.txt", "hello"u8.ToArray());
        Write(dir.Path, "y/two.txt", "hello"u8.ToArray());

        Assert.Empty(DuplicateFinder.Find([dir.Path])); // under the 1 MB default
        var groups = DuplicateFinder.Find([dir.Path, dir.Path, Path.Combine(dir.Path, "x")], new DuplicateFinder.Options { MinSize = 1 });
        Assert.Equal(2, Assert.Single(groups).Copies.Count);
    }

    [Fact]
    public void Remove_never_takes_the_last_copy_and_skips_changed_files()
    {
        using var dir = new TempDir();
        var data = Bytes(100_000, 4);
        var a = Path.GetFullPath(Write(dir.Path, "a.dat", data, new DateTime(2020, 1, 1)));
        var b = Path.GetFullPath(Write(dir.Path, "b.dat", data, new DateTime(2021, 1, 1)));
        var c = Path.GetFullPath(Write(dir.Path, "c.dat", data, new DateTime(2022, 1, 1)));
        var groups = DuplicateFinder.Find([dir.Path], new DuplicateFinder.Options { MinSize = 1 });

        // c changes after the search, so it must be left.
        File.WriteAllBytes(c, Bytes(100_000, 5));
        var result = DuplicateFinder.Remove(groups, new HashSet<string> { a, b, c }, File.Delete);

        Assert.Equal(1, result.Removed);
        Assert.Equal(100_000, result.Freed);
        Assert.True(File.Exists(a), "one copy has to stay");
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(c));
        Assert.Equal(2, result.Problems.Count);
    }

    [Theory]
    [InlineData(@"C:\Users\Sam\Downloads\setup.exe", true)]
    [InlineData(@"C:\Users\Sam\Pictures\beach - Copy.jpg", true)]
    [InlineData(@"C:\Users\Sam\Pictures\beach (2).jpg", true)]
    [InlineData(@"C:\Users\Sam\Pictures\beach.jpg", false)]
    [InlineData(@"C:\Users\Sam\Documents\Report 2024.docx", false)]
    public void Spots_names_that_look_like_copies(string path, bool copy) =>
        Assert.Equal(copy, DuplicateFinder.LooksLikeACopy(path));

    [Theory]
    [InlineData(@"C:\Windows\System32\a.dll", true)]
    [InlineData(@"C:\Program Files\App\a.dll", true)]
    [InlineData(@"C:\Users\Sam\AppData\Local\x.db", true)]
    [InlineData(@"C:\Users\Sam\Music\song.mp3", false)]
    public void Never_offers_windows_or_program_files(string path, bool safe) =>
        Assert.Equal(safe, DuplicateFinder.IsProtected(path));
}
