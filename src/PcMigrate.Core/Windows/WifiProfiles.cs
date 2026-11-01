using System.Runtime.Versioning;

namespace PcMigrate.Core.Windows;

/// <summary>Saved Wi-Fi networks, moved with Windows' own netsh tool.</summary>
[SupportedOSPlatform("windows")]
public static class WifiProfiles
{
    /// <summary>Exports every saved network, including its password, as XML files into <paramref name="folder"/>.</summary>
    public static IReadOnlyList<string> Export(string folder, IMigrationLog log)
    {
        Directory.CreateDirectory(folder);
        var result = ProcessRunner.Run("netsh.exe", "wlan", "export", "profile", "key=clear", $"folder={folder}");
        if (result.ExitCode != 0)
        {
            log.Warn($"Wi-Fi networks not exported (this PC may have no Wi-Fi): {result.Output}");
            return [];
        }
        return Directory.GetFiles(folder, "*.xml");
    }

    public static bool Import(string xmlPath, IMigrationLog log)
    {
        var result = ProcessRunner.Run("netsh.exe", "wlan", "add", "profile", $"filename={xmlPath}", "user=all");
        if (result.ExitCode != 0)
        {
            log.Warn($"Could not add Wi-Fi network from {Path.GetFileName(xmlPath)}: {result.Output}");
            return false;
        }
        return true;
    }
}
