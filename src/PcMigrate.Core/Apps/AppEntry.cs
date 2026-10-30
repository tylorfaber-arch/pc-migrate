namespace PcMigrate.Core.Apps;

/// <summary>How a program gets onto the new PC.</summary>
public enum AppMethod
{
    /// <summary>Fresh install with winget, then the old PC's settings are copied in. (Green.)</summary>
    Reinstall,
    /// <summary>Program folder, registry entries and shortcuts are copied as they are. (Yellow.)</summary>
    Copy,
    /// <summary>Has to be installed by hand; the reason says why. (Red.)</summary>
    Manual,
}

/// <summary>One program: how it will move, and (once captured) where its pieces are in the store.</summary>
public sealed class AppEntry
{
    /// <summary>Stable id inside one migration, safe to use in paths.</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public AppMethod Method { get; set; }
    /// <summary>One sentence for people: why this method.</summary>
    public required string Reason { get; set; }
    public string? WingetId { get; init; }
    public string? InstallLocation { get; init; }
    public required string UninstallKey { get; init; }
    /// <summary>"Machine" or the user it was installed for.</summary>
    public required string Scope { get; init; }

    /// <summary>Settings folders inside a user profile, e.g. AppData/Roaming/Spotify.</summary>
    public List<string> SettingsFolders { get; init; } = [];
    /// <summary>Keys under HKEY_CURRENT_USER\Software holding its settings, e.g. "Spotify" or "Valve\Steam".</summary>
    public List<string> UserRegistryKeys { get; init; } = [];
    /// <summary>Keys under HKLM\SOFTWARE copied for <see cref="AppMethod.Copy"/>.</summary>
    public List<string> MachineRegistryKeys { get; init; } = [];

    // Filled in by the capture.
    public List<RegistryFile> RegistryFiles { get; init; } = [];
    /// <summary>Store folder holding the program folder, for <see cref="AppMethod.Copy"/>.</summary>
    public string? ProgramStorePath { get; set; }
    public List<ShortcutFile> Shortcuts { get; init; } = [];
}

/// <summary>An exported .reg file. UserName is null for machine-wide keys.</summary>
public sealed class RegistryFile
{
    public required string StorePath { get; init; }
    public string? UserName { get; init; }
    /// <summary>The HKEY_USERS\{SID} prefix the file was exported with, swapped for the new user's on import.</summary>
    public string? ExportedRoot { get; init; }
}

public sealed class ShortcutFile
{
    public required string StorePath { get; init; }
    /// <summary>Where the .lnk goes on the new PC, e.g. C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Foo.lnk</summary>
    public required string TargetPath { get; init; }
}

public enum AppOutcome { Installed, Copied, AlreadyInstalled, Failed, Manual, NotSelected }

public sealed class AppResult
{
    public required string Name { get; init; }
    public AppOutcome Outcome { get; init; }
    public string? Message { get; init; }
}
