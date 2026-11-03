namespace PcMigrate.Core.Models;

/// <summary>Basic facts about the machine a migration was captured from.</summary>
public sealed class MachineInfo
{
    public required string ComputerName { get; init; }
    public required string OsDescription { get; init; }
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
    public string ToolVersion { get; init; } = ToolInfo.Version;
}

/// <summary>A Windows user profile (C:\Users\name) found on a machine.</summary>
public sealed partial class UserProfileInfo
{
    /// <summary>
    /// Accounts Windows makes for itself: defaultuser0 and defaultuser100000 (left over from first-time setup)
    /// and the Application Guard account. Nobody uses them.
    /// </summary>
    public static bool IsSetupAccount(string? name) =>
        name is not null && (SetupAccount().IsMatch(name) || name.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase));

    [System.Text.RegularExpressions.GeneratedRegex(@"^defaultuser\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SetupAccount();

    public required string UserName { get; init; }
    public required string Sid { get; init; }
    public required string ProfilePath { get; init; }
}

/// <summary>A program listed in "Apps &amp; features" (the Uninstall registry keys).</summary>
public sealed class InstalledApp
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public long? EstimatedSizeKb { get; init; }
    /// <summary>"Machine" for all-users installs, otherwise the user name it was installed for.</summary>
    public required string Scope { get; init; }
    public bool Is64Bit { get; init; }
    /// <summary>Full path of its Uninstall entry, e.g. HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{id}.</summary>
    public required string RegistryKey { get; init; }
    /// <summary>Installed by Windows Installer (an .msi). Those cannot safely be moved by copying.</summary>
    public bool IsMsi { get; init; }
    /// <summary>Where a "bundle" installer kept a full copy of itself (C:\ProgramData\Package Cache\...).</summary>
    public string? BundleCachePath { get; init; }
    /// <summary>The folder an .msi was installed from, if Windows recorded it.</summary>
    public string? InstallSource { get; init; }
}

/// <summary>Everything the scanner learned about a machine.</summary>
public sealed class Inventory
{
    public required MachineInfo Machine { get; init; }
    public List<UserProfileInfo> Users { get; init; } = [];
    public List<InstalledApp> Apps { get; init; } = [];
}

public static class ToolInfo
{
    /// <summary>
    /// Builds from GitHub carry the build number as the fourth part (0.1.0.57), which is also
    /// the release tag; the updater compares it. Local builds are 0.1.0.0.
    /// </summary>
    public static System.Version FullVersion { get; } = typeof(ToolInfo).Assembly.GetName().Version ?? new System.Version(0, 0, 0, 0);

    public static string Version { get; } = FullVersion.Revision > 0 ? FullVersion.ToString(4) : FullVersion.ToString(3);
}
