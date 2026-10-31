using PcMigrate.Core.Models;

namespace PcMigrate.Core.Apps;

/// <summary>The Windows pieces program moving needs. Faked in tests.</summary>
public interface IAppPlatform
{
    /// <summary>
    /// winget's catalogue entries for these programs, or null when winget is not on this PC.
    /// On the running PC winget matches them itself; for a pulled drive each one is looked up.
    /// </summary>
    IReadOnlyList<WingetRow>? ListWinget(IReadOnlyList<InstalledApp> apps);

    /// <summary>Set when the programs are on a drive pulled from a dead PC.</summary>
    Offline.OfflinePaths? Offline => null;

    /// <summary>True if a Windows service runs a program from inside this folder.</summary>
    bool HasServiceUnder(string folder);

    /// <summary>Key names under the user's HKEY_CURRENT_USER\Software, one and two levels deep ("Spotify", "Valve\Steam").</summary>
    IReadOnlyList<string> UserSoftwareKeys(UserProfileInfo user);

    /// <summary>Key names under HKLM\SOFTWARE (and its 32-bit view), one and two levels deep.</summary>
    IReadOnlyList<string> MachineSoftwareKeys();

    /// <summary>Exports a user's HKCU\Software\{subKey} to a .reg file. Returns the HKEY_USERS\... root used in the file, or null if it failed.</summary>
    string? ExportUserKey(UserProfileInfo user, string subKey, string file);

    /// <summary>Exports a full key path (HKLM\...) to a .reg file.</summary>
    bool ExportKey(string keyPath, string file);

    /// <summary>Imports a .reg file for a user, swapping <paramref name="exportedRoot"/> for that user's own root.</summary>
    bool ImportUserFile(UserProfileInfo user, string file, string exportedRoot);

    bool ImportFile(string file);

    /// <summary>Folders that hold all-users shortcuts: the shared Start menu and the Public desktop.</summary>
    IReadOnlyList<string> SharedShortcutFolders();

    Task<(bool Ok, string Message)> WingetInstallAsync(string id, CancellationToken ct);
}
