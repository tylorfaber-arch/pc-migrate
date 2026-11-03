using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PcMigrate.Core.Models;
using PcMigrate.Core;

namespace PcMigrate.Core.Storage;

/// <summary>What's needed to check a password and rebuild the key. Saved as encryption.json; holds nothing secret.</summary>
public sealed class EncryptionInfo
{
    public const string FileName = "encryption.json";
    public const string CurrentFormat = "pc-migrate-aes256gcm-v1";

    public string Format { get; init; } = CurrentFormat;
    public required string Salt { get; init; }
    public int Iterations { get; init; }
    /// <summary>A known text encrypted with the key: decrypting it proves the password is right.</summary>
    public required string Check { get; init; }
}

/// <summary>The backup is protected with a password and none (or the wrong one) was given.</summary>
public sealed class StoreLockedException(string message) : Exception(message);

/// <summary>
/// Password protection for a backup folder. Each file is encrypted with AES-256-GCM using its own key
/// (derived from the backup's key and a random salt), in 1 MB chunks that each carry a tamper check.
/// The password itself is never stored. File and folder names stay readable; contents don't.
/// </summary>
public sealed class StoreKey
{
    private const int Iterations = 600_000;
    private const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "PCMENC01"u8.ToArray();
    private static readonly byte[] CheckText = "pc-migrate password check"u8.ToArray();
    public const string Extension = ".enc";

    private readonly byte[] _key;
    private StoreKey(byte[] key) => _key = key;

    /// <summary>A new key for a new backup, and the info to save alongside it.</summary>
    public static (StoreKey Key, EncryptionInfo Info) Create(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("The password can't be empty.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = new StoreKey(Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32));
        var check = key.Seal(CheckText, "check");
        return (key, new EncryptionInfo { Salt = Convert.ToBase64String(salt), Iterations = Iterations, Check = Convert.ToBase64String(check) });
    }

    /// <summary>The key for an existing backup, or a <see cref="StoreLockedException"/> if the password is wrong.</summary>
    public static StoreKey Unlock(EncryptionInfo info, string? password)
    {
        if (info.Format != EncryptionInfo.CurrentFormat)
            throw new InvalidDataException("This backup was protected by a newer version of pc-migrate. Update pc-migrate on this PC.");
        if (string.IsNullOrEmpty(password)) throw new StoreLockedException("This backup is protected with a password.");
        var key = new StoreKey(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(info.Salt), info.Iterations, HashAlgorithmName.SHA256, 32));
        try
        {
            if (!key.Open(Convert.FromBase64String(info.Check), "check").AsSpan().SequenceEqual(CheckText)) throw new CryptographicException();
        }
        catch (CryptographicException)
        {
            throw new StoreLockedException("That password is wrong.");
        }
        return key;
    }

    /// <summary>Reads encryption.json from a backup folder, or null if the backup isn't protected.</summary>
    public static EncryptionInfo? ReadInfo(string root)
    {
        var path = Path.Combine(root, EncryptionInfo.FileName);
        return File.Exists(path) ? Json.Deserialize<EncryptionInfo>(File.ReadAllText(path)) : null;
    }

    /// <summary>Encrypts <paramref name="plain"/> into <paramref name="output"/>; returns the plain length and SHA-256 (what the manifest records).</summary>
    public async Task<FileWriteResult> EncryptAsync(Stream plain, Stream output, string storePath, CancellationToken ct)
    {
        var fileSalt = RandomNumberGenerator.GetBytes(32);
        using var aes = new AesGcm(FileKey(fileSalt), TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await output.WriteAsync(Magic, ct);
        await output.WriteAsync(fileSalt, ct);

        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var currentLength = await ReadFull(plain, current, ct);
        long total = 0, index = 0;
        var cipher = new byte[ChunkSize];
        var header = new byte[4];
        var tag = new byte[TagSize];
        while (true)
        {
            // Read ahead so the last chunk can be marked as last (stops a cut-off file passing as whole).
            var nextLength = currentLength == ChunkSize ? await ReadFull(plain, next, ct) : 0;
            var last = nextLength == 0;
            hash.AppendData(current, 0, currentLength);
            aes.Encrypt(Nonce(index), current.AsSpan(0, currentLength), cipher.AsSpan(0, currentLength), tag, Aad(storePath, index, last));
            BinaryPrimitives.WriteInt32LittleEndian(header, currentLength);
            try
            {
                await output.WriteAsync(header, ct);
                await output.WriteAsync(cipher.AsMemory(0, currentLength), ct);
                await output.WriteAsync(tag, ct);
            }
            catch (IOException ex)
            {
                throw new SinkWriteException($"Writing failed: {ex.Message}", ex);
            }
            total += currentLength;
            index++;
            if (last) break;
            (current, next) = (next, current);
            currentLength = nextLength;
        }
        return new FileWriteResult(total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>Decrypts a file written by <see cref="EncryptAsync"/>. Throws <see cref="CryptographicException"/> if it was changed or cut short.</summary>
    public async Task DecryptAsync(Stream encrypted, Stream output, string storePath, CancellationToken ct)
    {
        if (!encrypted.CanSeek) throw new NotSupportedException("Encrypted files are read from disk or memory.");
        var magic = new byte[Magic.Length];
        var fileSalt = new byte[32];
        if (await ReadFull(encrypted, magic, ct) != magic.Length || !magic.AsSpan().SequenceEqual(Magic) || await ReadFull(encrypted, fileSalt, ct) != 32)
            throw new CryptographicException("Not a pc-migrate encrypted file.");
        using var aes = new AesGcm(FileKey(fileSalt), TagSize);
        var header = new byte[4];
        var cipher = new byte[ChunkSize];
        var plain = new byte[ChunkSize];
        var tag = new byte[TagSize];
        for (long index = 0; ; index++)
        {
            if (await ReadFull(encrypted, header, ct) != 4) throw new CryptographicException("The file is cut short.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 0 or > ChunkSize) throw new CryptographicException("The file is damaged.");
            if (await ReadFull(encrypted, cipher.AsMemory(0, length), ct) != length || await ReadFull(encrypted, tag, ct) != TagSize)
                throw new CryptographicException("The file is cut short.");
            // The last chunk is the one that ends the file. A file cut short at a chunk boundary
            // fails here, because that chunk was sealed as "not the last".
            var last = encrypted.Position == encrypted.Length;
            aes.Decrypt(Nonce(index), cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), Aad(storePath, index, last));
            await output.WriteAsync(plain.AsMemory(0, length), ct);
            if (last) break;
        }
    }

    internal byte[] Seal(byte[] data, string storePath)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        EncryptAsync(input, output, storePath, CancellationToken.None).GetAwaiter().GetResult();
        return output.ToArray();
    }

    internal byte[] Open(byte[] data, string storePath)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        DecryptAsync(input, output, storePath, CancellationToken.None).GetAwaiter().GetResult();
        return output.ToArray();
    }

    private byte[] FileKey(byte[] fileSalt) => HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, fileSalt, "pc-migrate file"u8.ToArray());

    private static byte[] Nonce(long index)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(nonce, index);
        return nonce;
    }

    /// <summary>Ties each chunk to its file's place in the backup, its position, and whether it's the last.</summary>
    private static byte[] Aad(string storePath, long index, bool last)
    {
        var path = Encoding.UTF8.GetBytes(storePath);
        var aad = new byte[path.Length + 9];
        path.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(path.Length), index);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }

    private static async Task<int> ReadFull(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[total..], ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static Task<int> ReadFull(Stream s, byte[] buffer, CancellationToken ct) => ReadFull(s, buffer.AsMemory(), ct);
}

/// <summary>Turns a password-protected backup back into a plain one, so it can be put in place as usual.</summary>
public static class StoreUnlocker
{
    /// <summary>
    /// Decrypts every file of the backup at <paramref name="root"/> into <paramref name="plainRoot"/>
    /// (a fresh folder), checking each against the manifest, and writes a plain manifest there.
    /// </summary>
    public static async Task UnlockAsync(string root, string password, string plainRoot, IMigrationLog log, CancellationToken ct)
    {
        var key = StoreKey.Unlock(StoreKey.ReadInfo(root) ?? throw new InvalidDataException("This backup isn't password-protected."), password);
        var manifest = FolderSink.ReadManifest(root, password);
        Directory.CreateDirectory(plainRoot);
        log.Info("Unlocking the backup...");
        long files = 0, bytes = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var source = StorePath.ToLocal(root, file.StorePath) + StoreKey.Extension;
            var target = StorePath.ToLocal(plainRoot, file.StorePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous))
                    await key.DecryptAsync(input, output, file.StorePath, ct);
                File.SetLastWriteTimeUtc(target, file.LastWriteUtc);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException)
            {
                // Left out here; the copy step then reports it as missing or damaged, like any other bad file.
                try { File.Delete(target); } catch (IOException) { }
                log.Warn($"Couldn't unlock {file.StorePath}: {ex.Message}");
                continue;
            }
            files++;
            bytes += file.Length;
            log.FileProgress(files, bytes, file.StorePath);
        }
        await File.WriteAllTextAsync(Path.Combine(plainRoot, Manifest.FileName), Json.Serialize(manifest), ct);
    }
}
