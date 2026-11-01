using System.Runtime.Versioning;
using PcMigrate.Core.Transfer;

namespace PcMigrate.Core.Windows;

/// <summary>Opens the receive port (and the discovery port) in Windows Firewall for as long as pc-migrate is waiting.</summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallRule : IDisposable
{
    private const string TcpRule = "pc-migrate (receive)";
    private const string UdpRule = "pc-migrate (discovery)";

    private FirewallRule() { }

    public static FirewallRule? Open(int port, IMigrationLog log)
    {
        Remove();
        var ok = Add(TcpRule, "TCP", port);
        Add(UdpRule, "UDP", Discovery.Port);
        if (!ok)
        {
            log.Warn($"Could not open port {port} in Windows Firewall. If the old PC cannot connect, allow pc-migrate through the firewall.");
            return null;
        }
        return new FirewallRule();
    }

    public void Dispose() => Remove();

    private static bool Add(string name, string protocol, int port) =>
        ProcessRunner.Run("netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={name}", "dir=in", "action=allow",
            $"protocol={protocol}", $"localport={port}", "profile=private,domain,public").ExitCode == 0;

    private static void Remove()
    {
        ProcessRunner.Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={TcpRule}");
        ProcessRunner.Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={UdpRule}");
    }
}
