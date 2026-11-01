using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PcMigrate.Core.Models;

namespace PcMigrate.Core.Transfer;

public sealed record DiscoveredReceiver(string Name, string Address, int Port);

/// <summary>
/// Lets the old PC find the new one without typing an IP address. The old PC
/// broadcasts a short question on UDP port 47801; every PC waiting to receive
/// answers with its name and TCP port.
/// </summary>
public static class Discovery
{
    public const int Port = 47801;
    private static readonly byte[] Question = "PCMG?"u8.ToArray();

    private sealed class Answer
    {
        public string App { get; init; } = "";
        public required string Name { get; init; }
        public int Port { get; init; }
    }

    /// <summary>Runs on the new PC while it waits. Answers questions until cancelled.</summary>
    public static async Task RespondAsync(int tcpPort, CancellationToken ct, int udpPort = Port)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
        var answer = Encoding.UTF8.GetBytes(Json.Serialize(new Answer { App = "pc-migrate", Name = Environment.MachineName, Port = tcpPort }));

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try
            {
                request = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            if (request.Buffer.AsSpan().SequenceEqual(Question))
            {
                try { await udp.SendAsync(answer, request.RemoteEndPoint, ct); }
                catch (SocketException) { }
            }
        }
    }

    /// <summary>Runs on the old PC. Asks every couple of seconds and reports each new PC once.</summary>
    public static async Task ScanAsync(Action<DiscoveredReceiver> found, CancellationToken ct, int udpPort = Port, IEnumerable<IPAddress>? extraTargets = null)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var seen = new HashSet<string>();

        var listening = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; }
                try
                {
                    var answer = Json.Deserialize<Answer>(Encoding.UTF8.GetString(reply.Buffer));
                    var address = reply.RemoteEndPoint.Address.ToString();
                    if (answer.App == "pc-migrate" && seen.Add(address))
                        found(new DiscoveredReceiver(answer.Name, address, answer.Port));
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { }
            }
        }, CancellationToken.None);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var target in BroadcastTargets().Concat(extraTargets ?? []))
                {
                    try { await udp.SendAsync(Question, new IPEndPoint(target, udpPort), ct); }
                    catch (SocketException) { }
                }
                await Task.Delay(2000, ct);
            }
        }
        catch (OperationCanceledException) { }
        await listening;
    }

    /// <summary>255.255.255.255 plus each network's own broadcast address, which some routers need.</summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bcast);
            }
        }
    }
}
