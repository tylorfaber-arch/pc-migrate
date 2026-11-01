namespace PcMigrate.Core.Models;

/// <summary>
/// The table of contents of a migration store. It is written last, so a store
/// without a manifest.json is an incomplete capture.
/// </summary>
public sealed class Manifest
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "manifest.json";

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required Inventory Inventory { get; init; }
    public List<ManifestFile> Files { get; init; } = [];
    public List<UserSettings> UserSettings { get; init; } = [];
    /// <summary>Store paths of exported Wi-Fi profiles (netsh XML).</summary>
    public List<string> WifiProfiles { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool UsedShadowCopy { get; set; }
}

public sealed class ManifestFile
{
    /// <summary>Path inside the store, always with forward slashes, e.g. files/users/tylor/Documents/a.txt</summary>
    public required string StorePath { get; init; }
    public long Length { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>Per-user Windows settings that live in the registry rather than in files.</summary>
public sealed class UserSettings
{
    public required string UserName { get; init; }
    /// <summary>Store path of the wallpaper image, if one was captured.</summary>
    public string? WallpaperStorePath { get; set; }
    public string? WallpaperStyle { get; set; }
    public string? TileWallpaper { get; set; }
    public List<MappedDrive> MappedDrives { get; init; } = [];
}

public sealed class MappedDrive
{
    public required string Letter { get; init; }
    public required string RemotePath { get; init; }
}
