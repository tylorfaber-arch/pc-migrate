using System.Net.Sockets;
using System.Text;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;
using PcMigrate.Core.Transfer;

namespace PcMigrate.Tests;

public class NetworkTests
{
    [Fact]
    public async Task SendReceiveApply_EndToEnd()
    {
        using var oldPc = new FakePc("old", "a");
        using var newPc = new FakePc("new", "a");
        using var store = new TempDir();
        oldPc.WriteFile("a", "Documents/small.txt", "hello");
        var big = new byte[3 * 1024 * 1024 + 17]; // spans several network chunks
        Random.Shared.NextBytes(big);
        File.WriteAllBytes(oldPc.WriteFile("a", "Pictures/big.bin", ""), big);
        oldPc.WriteFile("a", "Documents/empty.txt", "");

        using var receiver = new NetworkReceiver(store.Path, "123456", new ListLog());
        var port = receiver.Start(0);
        var receiving = receiver.ReceiveAsync();

        await using (var sender = await NetworkSender.ConnectAsync("127.0.0.1", port, "123456"))
            await new CaptureEngine(oldPc, new ListLog()).RunAsync(sender, Options.Capture());
        var manifest = await receiving.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, manifest.Files.Count);

        var result = await new ApplyEngine(newPc, new ListLog()).RunAsync(store.Path, new ApplyOptions());
        Assert.Equal(3, result.FilesCopied);
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(newPc.Profile("a"), "Pictures", "big.bin")));
        Assert.Equal("", File.ReadAllText(Path.Combine(newPc.Profile("a"), "Documents", "empty.txt")));
    }

    [Fact]
    public async Task WrongPairingCode_IsRefused()
    {
        using var store = new TempDir();
        using var receiver = new NetworkReceiver(store.Path, "123456", new ListLog());
        var port = receiver.Start(0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var receiving = receiver.ReceiveAsync(cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NetworkSender.ConnectAsync("127.0.0.1", port, "999999"));
        Assert.Contains("Wrong pairing code", ex.Message);

        // The receiver keeps waiting for the right PC.
        Assert.False(receiving.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
    }

    [Fact]
    public async Task UnreadableFile_IsDroppedAndTransferContinues()
    {
        using var store = new TempDir();
        using var receiver = new NetworkReceiver(store.Path, "1", new ListLog());
        var port = receiver.Start(0);
        var receiving = receiver.ReceiveAsync();

        await using (var sender = await NetworkSender.ConnectAsync("127.0.0.1", port, "1"))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                sender.AddFileAsync("files/users/a/Documents/locked.pst", new FailingStream(), DateTime.UtcNow, default));
            var ok = await sender.AddFileAsync("files/users/a/Documents/ok.txt", new MemoryStream("fine"u8.ToArray()), DateTime.UtcNow, default);
            await sender.CompleteAsync(new Manifest
            {
                Inventory = new Inventory { Machine = new MachineInfo { ComputerName = "old", OsDescription = "x" } },
                Files = [new ManifestFile { StorePath = "files/users/a/Documents/ok.txt", Length = ok.Length, Sha256 = ok.Sha256 }],
            }, default);
        }

        await receiving.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(Path.Combine(store.Path, "files", "users", "a", "Documents", "ok.txt")));
        Assert.Empty(Directory.GetFiles(store.Path, "locked.pst*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PathTraversalFromSender_IsRejected()
    {
        using var store = new TempDir();
        using var receiver = new NetworkReceiver(store.Path, "1", new ListLog());
        var port = receiver.Start(0);
        var receiving = receiver.ReceiveAsync();

        // Hand-craft what a hostile sender would send.
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var s = client.GetStream();
        await s.WriteAsync(Wire.Magic);
        await Wire.WriteByteAsync(s, Wire.Version, default);
        await Wire.WriteStringAsync(s, Json.Serialize(new Hello { PairingCode = "1", ComputerName = "evil", ToolVersion = "0" }), default);
        Assert.Equal(1, await Wire.ReadByteAsync(s, default));
        await Wire.ReadStringAsync(s, default);
        await Wire.WriteByteAsync(s, Wire.FrameFile, default);
        await Wire.WriteStringAsync(s, "../../outside.txt", default);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => receiving.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("unsafe path", ex.Message);
        Assert.False(File.Exists(Path.Combine(store.Path, "..", "..", "outside.txt")));
    }

    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ > 0) throw new IOException("The process cannot access the file because it is being used by another process.");
            Encoding.UTF8.GetBytes("partial").CopyTo(buffer, offset);
            return 7;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

public class DiscoveryTests
{
    [Fact]
    public async Task OldPcFindsNewPc()
    {
        var udpPort = FreeUdpPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var responder = Discovery.RespondAsync(47800, cts.Token, udpPort);

        var found = new TaskCompletionSource<DiscoveredReceiver>();
        var scanning = Discovery.ScanAsync(r => found.TrySetResult(r), cts.Token, udpPort, [System.Net.IPAddress.Loopback]);

        var receiver = await found.Task.WaitAsync(cts.Token);
        Assert.Equal(Environment.MachineName, receiver.Name);
        Assert.Equal(47800, receiver.Port);
        cts.Cancel();
        await scanning;
        await responder;
    }

    private static int FreeUdpPort()
    {
        using var u = new System.Net.Sockets.UdpClient(0);
        return ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
    }
}
