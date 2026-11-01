using System.Buffers;
using System.Net.Sockets;
using System.Security.Cryptography;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Core.Transfer;

/// <summary>Streams a capture straight to pc-migrate running in receive mode on the new PC.</summary>
public sealed class NetworkSender : IMigrationSink
{
    private readonly TcpClient _client;
    private readonly Stream _stream;

    private NetworkSender(TcpClient client, Stream stream)
    {
        _client = client;
        _stream = stream;
    }

    public static async Task<NetworkSender> ConnectAsync(string host, int port, string pairingCode, CancellationToken ct = default, long estimatedBytes = 0)
    {
        var client = new TcpClient { NoDelay = true, SendBufferSize = 4 * 1024 * 1024 };
        try
        {
            await client.ConnectAsync(host, port, ct);
            var stream = new BufferedStream(client.GetStream(), Wire.MaxChunk + 64);

            await stream.WriteAsync(Wire.Magic, ct);
            await Wire.WriteByteAsync(stream, Wire.Version, ct);
            await Wire.WriteStringAsync(stream, Json.Serialize(new Hello
            {
                PairingCode = pairingCode.Trim(),
                ComputerName = Environment.MachineName,
                ToolVersion = ToolInfo.Version,
                EstimatedBytes = estimatedBytes,
            }), ct);
            await stream.FlushAsync(ct);

            var status = await Wire.ReadByteAsync(stream, ct);
            var message = await Wire.ReadStringAsync(stream, ct);
            if (status != 1) throw new InvalidOperationException($"The new PC refused the connection: {message}");
            return new NetworkSender(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<FileWriteResult> AddFileAsync(string storePath, Stream content, DateTime lastWriteUtc, CancellationToken ct)
    {
        await Send(async () =>
        {
            await Wire.WriteByteAsync(_stream, Wire.FrameFile, ct);
            await Wire.WriteStringAsync(_stream, storePath, ct);
            await Wire.WriteInt64Async(_stream, lastWriteUtc.Ticks, ct);
        });

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(Wire.MaxChunk);
        long total = 0;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await content.ReadAsync(buffer.AsMemory(0, Wire.MaxChunk), ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Tell the receiver to drop the half-sent file, then report the read error as usual.
                    await Send(() => Wire.WriteInt32Async(_stream, Wire.AbortedFile, ct));
                    throw;
                }
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                await Send(async () =>
                {
                    await Wire.WriteInt32Async(_stream, read, ct);
                    await _stream.WriteAsync(buffer.AsMemory(0, read), ct);
                });
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var digest = hash.GetHashAndReset();
        await Send(async () =>
        {
            await Wire.WriteInt32Async(_stream, Wire.EndOfFile, ct);
            await _stream.WriteAsync(digest, ct);
        });
        return new FileWriteResult(total, Convert.ToHexString(digest).ToLowerInvariant());
    }

    public async Task CompleteAsync(Manifest manifest, CancellationToken ct)
    {
        await Send(async () =>
        {
            await Wire.WriteByteAsync(_stream, Wire.FrameComplete, ct);
            await Wire.WriteStringAsync(_stream, Json.Serialize(manifest), ct);
            await _stream.FlushAsync(ct);
        });

        byte status;
        string message;
        try
        {
            status = await Wire.ReadByteAsync(_stream, ct);
            message = await Wire.ReadStringAsync(_stream, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw new SinkWriteException($"Lost connection to the new PC: {ex.Message}", ex);
        }
        if (status != 1) throw new SinkWriteException($"The new PC reported a problem: {message}", new InvalidDataException(message));
    }

    private static async Task Send(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new SinkWriteException($"Lost connection to the new PC: {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.DisposeAsync(); } catch (IOException) { }
        _client.Dispose();
    }
}
