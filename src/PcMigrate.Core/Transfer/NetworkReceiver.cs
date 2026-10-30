using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Core.Transfer;

/// <summary>
/// Runs on the new PC. Waits for the old PC to connect with the right pairing code
/// and writes everything it sends into a migration store folder.
/// </summary>
public sealed class NetworkReceiver(string storeRoot, string pairingCode, IMigrationLog log) : IDisposable
{
    private const int MaxBadAttempts = 5;
    private TcpListener? _listener;

    /// <summary>Raised once the old PC has connected with the right code: its name and estimated size.</summary>
    public event Action<string, long>? Connected;

    public static string NewPairingCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Starts listening and returns the port (useful when 0 was asked for).</summary>
    public const int DefaultPort = Wire.DefaultPort;

    public int Start(int port = DefaultPort)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Waits for one complete transfer and returns its manifest.</summary>
    public async Task<Manifest> ReceiveAsync(CancellationToken ct = default)
    {
        var listener = _listener ?? throw new InvalidOperationException("Call Start first.");
        var badAttempts = 0;

        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            client.ReceiveBufferSize = 4 * 1024 * 1024;
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            await using var stream = new BufferedStream(client.GetStream(), Wire.MaxChunk + 64);

            Hello hello;
            try
            {
                hello = await ReadHelloAsync(stream, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                log.Warn($"Ignored a connection from {remote} that is not pc-migrate: {ex.Message}");
                continue;
            }

            if (!CodesMatch(hello.PairingCode, pairingCode))
            {
                await ReplyAsync(stream, false, "Wrong pairing code.", ct);
                log.Warn($"{hello.ComputerName} ({remote}) tried to connect with the wrong pairing code.");
                if (++badAttempts >= MaxBadAttempts)
                    throw new InvalidOperationException("Too many wrong pairing codes. Start receive again to get a new code.");
                continue;
            }

            await ReplyAsync(stream, true, $"Connected to {Environment.MachineName}.", ct);
            log.Info($"Connected to {hello.ComputerName} ({remote}). Receiving...");
            Connected?.Invoke(hello.ComputerName, hello.EstimatedBytes);
            return await ReceiveFramesAsync(stream, ct);
        }
    }

    private static async Task<Hello> ReadHelloAsync(Stream stream, CancellationToken ct)
    {
        var magic = new byte[Wire.Magic.Length];
        await stream.ReadExactlyAsync(magic, ct);
        if (!magic.AsSpan().SequenceEqual(Wire.Magic)) throw new InvalidDataException("Bad greeting.");
        var version = await Wire.ReadByteAsync(stream, ct);
        if (version != Wire.Version)
            throw new InvalidDataException($"The other PC runs a different pc-migrate version (protocol {version}, this PC uses {Wire.Version}).");
        return Json.Deserialize<Hello>(await Wire.ReadStringAsync(stream, ct));
    }

    private async Task<Manifest> ReceiveFramesAsync(Stream stream, CancellationToken ct)
    {
        var sink = new FolderSink(storeRoot);
        var received = new Dictionary<string, string>(StringComparer.Ordinal);
        long files = 0, bytes = 0;

        while (true)
        {
            var frame = await Wire.ReadByteAsync(stream, ct);
            switch (frame)
            {
                case Wire.FrameFile:
                {
                    var path = await Wire.ReadStringAsync(stream, ct);
                    // Reject anything that would land outside the store before touching the disk.
                    if (!StorePath.IsSafe(path)) throw new InvalidDataException($"The other PC sent an unsafe path: '{path}'");
                    var lastWrite = new DateTime(await Wire.ReadInt64Async(stream, ct), DateTimeKind.Utc);

                    var chunks = new ChunkedReadStream(stream);
                    FileWriteResult written;
                    try
                    {
                        written = await sink.AddFileAsync(path, chunks, lastWrite, ct);
                    }
                    catch (FileTransferAbortedException)
                    {
                        continue;
                    }

                    var expected = new byte[32];
                    await stream.ReadExactlyAsync(expected, ct);
                    if (!string.Equals(Convert.ToHexString(expected), written.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"'{path}' was damaged in transit (checksum mismatch).");

                    received[path] = written.Sha256;
                    files++;
                    bytes += written.Length;
                    log.FileProgress(files, bytes, path);
                    break;
                }
                case Wire.FrameComplete:
                {
                    var manifest = Json.Deserialize<Manifest>(await Wire.ReadStringAsync(stream, ct));
                    var missing = manifest.Files.Where(f => !received.ContainsKey(f.StorePath)).Select(f => f.StorePath).ToList();
                    if (missing.Count > 0)
                    {
                        await ReplyAsync(stream, false, $"{missing.Count} file(s) listed in the manifest never arrived.", ct);
                        throw new InvalidDataException($"{missing.Count} file(s) listed in the manifest never arrived, e.g. {missing[0]}");
                    }
                    await sink.CompleteAsync(manifest, ct);
                    await ReplyAsync(stream, true, $"Received {files:N0} files.", ct);
                    return manifest;
                }
                default:
                    throw new InvalidDataException($"Unexpected data from the other PC (frame {frame}).");
            }
        }
    }

    private static async Task ReplyAsync(Stream stream, bool ok, string message, CancellationToken ct)
    {
        await Wire.WriteByteAsync(stream, ok ? (byte)1 : (byte)0, ct);
        await Wire.WriteStringAsync(stream, message, ct);
        await stream.FlushAsync(ct);
    }

    private static bool CodesMatch(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a.Trim()), Encoding.UTF8.GetBytes(b.Trim()));

    /// <summary>IPv4 addresses the old PC can use to reach this one.</summary>
    public static IReadOnlyList<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();

    public void Dispose() => _listener?.Stop();
}
