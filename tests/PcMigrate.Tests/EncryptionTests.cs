using System.Security.Cryptography;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class EncryptionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1024 * 1024)]
    [InlineData(1024 * 1024 * 2 + 17)]
    public async Task RoundTripsAnySize(int size)
    {
        var (key, _) = StoreKey.Create("correct horse battery");
        var data = RandomNumberGenerator.GetBytes(size);
        using var encrypted = new MemoryStream();
        var written = await key.EncryptAsync(new MemoryStream(data), encrypted, "files/a.bin", default);
        Assert.Equal(size, written.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), written.Sha256);

        encrypted.Position = 0;
        using var plain = new MemoryStream();
        await key.DecryptAsync(encrypted, plain, "files/a.bin", default);
        Assert.Equal(data, plain.ToArray());
    }

    [Fact]
    public async Task TamperingTruncatingOrMovingIsCaught()
    {
        var (key, _) = StoreKey.Create("correct horse battery");
        using var encrypted = new MemoryStream();
        await key.EncryptAsync(new MemoryStream(RandomNumberGenerator.GetBytes(1024 * 1024 * 2)), encrypted, "files/a.bin", default);
        var bytes = encrypted.ToArray();

        async Task Decrypt(byte[] b, string path) => await key.DecryptAsync(new MemoryStream(b), new MemoryStream(), path, default);

        var flipped = (byte[])bytes.Clone();
        flipped[100] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(flipped, "files/a.bin"));
        // Cut after the first whole chunk: header 40 bytes, then 4 + 1 MB + 16.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(bytes[..(40 + 4 + 1024 * 1024 + 16)], "files/a.bin"));
        // The same file presented as a different one.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(bytes, "files/b.bin"));
    }

    [Fact]
    public void WrongOrMissingPasswordIsRefused()
    {
        var (_, info) = StoreKey.Create("correct horse battery");
        Assert.Throws<StoreLockedException>(() => StoreKey.Unlock(info, "wrong"));
        Assert.Throws<StoreLockedException>(() => StoreKey.Unlock(info, null));
        StoreKey.Unlock(info, "correct horse battery");
    }

    [Fact]
    public async Task ProtectedBackupRoundTripsAndHidesContents()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        oldPc.WriteFile("a", "Documents/secret.txt", "the secret plan");
        using var store = new TempDir();
        using var work = new TempDir();
        var log = new ListLog();
        await using (var sink = new FolderSink(store.Path, "correct horse battery"))
            await new CaptureEngine(oldPc, log).RunAsync(sink, Options.Capture("a"));

        Assert.True(FolderSink.IsProtected(store.Path));
        Assert.False(File.Exists(Path.Combine(store.Path, Manifest.FileName)));
        var stored = Directory.GetFiles(store.Path, "secret.txt*", SearchOption.AllDirectories).Single();
        Assert.EndsWith(".enc", stored);
        Assert.DoesNotContain("the secret plan", File.ReadAllText(stored));
        Assert.Throws<StoreLockedException>(() => FolderSink.ReadManifest(store.Path));
        Assert.Equal("old", FolderSink.ReadManifest(store.Path, "correct horse battery").Inventory.Machine.ComputerName);

        await Assert.ThrowsAsync<StoreLockedException>(() => new ApplyEngine(newPc, log).RunAsync(store.Path, new ApplyOptions { Password = "nope", WorkFolder = work.Path }));
        var result = await new ApplyEngine(newPc, log).RunAsync(store.Path, new ApplyOptions { Password = "correct horse battery", WorkFolder = work.Path });

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal("the secret plan", File.ReadAllText(Path.Combine(newPc.Profile("a"), "Documents", "secret.txt")));
        Assert.True(File.Exists(stored)); // the backup itself is left as it was
        Assert.Empty(Directory.GetDirectories(work.Path)); // the unlocked copy is gone
        Assert.StartsWith(work.Path, result.ReportPath);
        Assert.Empty(log.Warnings);
    }
}
