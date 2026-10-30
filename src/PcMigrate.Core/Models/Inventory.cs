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
public sealed class UserProfileInfo
{
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
    public static string Version { get; } =
        typeof(ToolInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
