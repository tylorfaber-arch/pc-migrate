using System.IO.Enumeration;

namespace PcMigrate.Core.Capture;

/// <summary>
/// Decides which parts of a user profile get copied. Paths are relative to the
/// profile folder (C:\Users\name) and use '/'.
/// </summary>
public sealed class CaptureRules
{
    public List<string> ProfileFolders { get; init; } =
    [
        // The usual personal folders.
        "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos",
        "Favorites", "Contacts", "Links", "Saved Games",

        // Browser profiles: bookmarks, history, extensions, open tabs.
        // Saved passwords in Chrome and Edge are locked to the old PC by Windows (DPAPI) and will not decrypt.
        "AppData/Local/Google/Chrome/User Data",
        "AppData/Local/Microsoft/Edge/User Data",
        "AppData/Local/BraveSoftware/Brave-Browser/User Data",
        "AppData/Roaming/Mozilla/Firefox",
        "AppData/Roaming/Opera Software",

        // Small Windows and Office bits people notice when missing.
        "AppData/Roaming/Microsoft/Signatures",
        "AppData/Roaming/Microsoft/Templates",
        "AppData/Roaming/Microsoft/Windows/Themes",
        "AppData/Roaming/Microsoft/Windows/Start Menu/Programs",
        "AppData/Roaming/Microsoft/Internet Explorer/Quick Launch/User Pinned",
    ];

    /// <summary>Folder names skipped anywhere in the tree: caches that rebuild themselves.</summary>
    public List<string> ExcludedFolderNames { get; init; } =
    [
        "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnCache",
        "CacheStorage", "ScriptCache", "Crashpad", "Crash Reports", "cache2", "startupCache",
        "Safe Browsing", "OptimizationGuidePredictionModels", "component_crx_cache",
        "$RECYCLE.BIN", "System Volume Information",
    ];

    /// <summary>File name patterns skipped anywhere (* and ? wildcards).</summary>
    public List<string> ExcludedFilePatterns { get; init; } =
    [
        "*.tmp", "~$*", "Thumbs.db", "*.partial", "parent.lock", "lockfile", "LOCK", "SingletonLock",
        "SingletonCookie", "SingletonSocket", "*.crdownload", "*.part",
        // Folder display settings. The new PC has its own, and Google Drive refuses to store them.
        "desktop.ini",
    ];

    public bool IsFolderExcluded(string folderName) =>
        ExcludedFolderNames.Any(n => string.Equals(n, folderName, StringComparison.OrdinalIgnoreCase));

    public bool IsFileExcluded(string fileName) =>
        ExcludedFilePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));
}
