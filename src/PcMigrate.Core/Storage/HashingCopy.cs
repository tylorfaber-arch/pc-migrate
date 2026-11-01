using System.Buffers;
using System.Security.Cryptography;

namespace PcMigrate.Core.Storage;

internal static class HashingCopy
{
    public const int BufferSize = 1024 * 1024;

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>, hashing as it goes.</summary>
    public static async Task<FileWriteResult> CopyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                try
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                catch (IOException ex)
                {
                    throw new SinkWriteException($"Writing failed: {ex.Message}", ex);
                }
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new FileWriteResult(total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = OpenForRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, ct)).ToLowerInvariant();
    }

    /// <summary>
    /// Opens a file for reading while letting other programs keep it open. If its
    /// permissions shut us out, tries again with backup rights (see BackupFile).
    /// </summary>
    public static FileStream OpenForRead(string path)
    {
        try
        {
            return new(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 0,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            });
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            var stream = Windows.BackupFile.OpenRead(path);
            if (stream is null) throw;
            return stream;
        }
    }
}
