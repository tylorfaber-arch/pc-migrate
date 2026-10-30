using System.Diagnostics;
using PcMigrate.Core;

namespace PcMigrate.Cli;

/// <summary>Opens the receive port in Windows Firewall for as long as pc-migrate is waiting.</summary>
internal sealed class FirewallRule : IDisposable
{
    private const string RuleName = "pc-migrate (receive)";

    private FirewallRule() { }

    public static FirewallRule? Open(int port, IMigrationLog log)
    {
        Netsh("advfirewall", "firewall", "delete", "rule", $"name={RuleName}");
        var ok = Netsh("advfirewall", "firewall", "add", "rule", $"name={RuleName}", "dir=in", "action=allow",
            "protocol=TCP", $"localport={port}", "profile=private,domain,public");
        if (!ok)
        {
            log.Warn($"Could not open port {port} in Windows Firewall. If the old PC cannot connect, allow pc-migrate through the firewall.");
            return null;
        }
        return new FirewallRule();
    }

    public void Dispose() => Netsh("advfirewall", "firewall", "delete", "rule", $"name={RuleName}");

    private static bool Netsh(params string[] args)
    {
        var psi = new ProcessStartInfo("netsh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
