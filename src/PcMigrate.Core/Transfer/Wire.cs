using System.Buffers.Binary;
using System.Text;

namespace PcMigrate.Core.Transfer;

/// <summary>
/// The network format between two copies of pc-migrate.
///
///   sender  -> "PCMG" version(1 byte) hello(json string)
///   receiver-> status(1 byte: 1 ok, 0 rejected) message(string)
///   then frames from the sender:
///     1 FILE     path(string) lastWriteUtcTicks(int64) chunks... sha256(32 bytes)
///                each chunk is length(int32) + bytes; length 0 ends the file, -1 means the
///                sender could not finish reading it and the receiver throws it away.
///     2 COMPLETE manifest(json string)
///   receiver-> status(1 byte) message(string)
///
/// Strings are length(int32) + UTF-8. Integers are little-endian.
/// </summary>
internal static class Wire
{
    public static readonly byte[] Magic = "PCMG"u8.ToArray();
    public const byte Version = 1;
    public const int DefaultPort = 47800;

    public const byte FrameFile = 1;
    public const byte FrameComplete = 2;

    public const int EndOfFile = 0;
    public const int AbortedFile = -1;
    public const int MaxChunk = 1024 * 1024;
    private const int MaxString = 256 * 1024 * 1024;

    public static async Task WriteByteAsync(Stream s, byte value, CancellationToken ct) =>
        await s.WriteAsync(new[] { value }, ct);

    public static async Task<byte> ReadByteAsync(Stream s, CancellationToken ct)
    {
        var b = new byte[1];
        await s.ReadExactlyAsync(b, ct);
        return b[0];
    }

    public static async Task WriteInt32Async(Stream s, int value, CancellationToken ct)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        await s.WriteAsync(b, ct);
    }

    public static async Task<int> ReadInt32Async(Stream s, CancellationToken ct)
    {
        var b = new byte[4];
        await s.ReadExactlyAsync(b, ct);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    public static async Task WriteInt64Async(Stream s, long value, CancellationToken ct)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        await s.WriteAsync(b, ct);
    }

    public static async Task<long> ReadInt64Async(Stream s, CancellationToken ct)
    {
        var b = new byte[8];
        await s.ReadExactlyAsync(b, ct);
        return BinaryPrimitives.ReadInt64LittleEndian(b);
    }

    public static async Task WriteStringAsync(Stream s, string value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await WriteInt32Async(s, bytes.Length, ct);
        await s.WriteAsync(bytes, ct);
    }

    public static async Task<string> ReadStringAsync(Stream s, CancellationToken ct)
    {
        var length = await ReadInt32Async(s, ct);
        if (length < 0 || length > MaxString) throw new InvalidDataException($"Bad string length {length} from the other PC.");
        var bytes = new byte[length];
        await s.ReadExactlyAsync(bytes, ct);
        return Encoding.UTF8.GetString(bytes);
    }
}

internal sealed class Hello
{
    public required string PairingCode { get; init; }
    public required string ComputerName { get; init; }
    public required string ToolVersion { get; init; }
}

/// <summary>Reads one file's chunks off the wire as an ordinary stream.</summary>
internal sealed class ChunkedReadStream(Stream inner) : Stream
{
    private int _remaining;
    private bool _done;

    public bool Aborted { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_done) return 0;
        if (_remaining == 0)
        {
            var next = await Wire.ReadInt32Async(inner, ct);
            if (next == Wire.EndOfFile) { _done = true; return 0; }
            if (next == Wire.AbortedFile)
            {
                _done = true;
                Aborted = true;
                throw new FileTransferAbortedException();
            }
            if (next < 0 || next > Wire.MaxChunk) throw new InvalidDataException($"Bad chunk size {next} from the other PC.");
            _remaining = next;
        }
        var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], ct);
        if (read == 0) throw new EndOfStreamException("The other PC disconnected in the middle of a file.");
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>The sender gave up on a file part-way (usually it could not read it). Not an error for the transfer.</summary>
internal sealed class FileTransferAbortedException() : IOException("The sending PC could not read this file.");
