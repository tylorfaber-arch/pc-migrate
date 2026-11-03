using System.IO.Enumeration;

namespace PcMigrate.Core.Capture;

/// <summary>
/// Decides which parts of a user profile get copied. Paths are relative to the
/// profile folder (C:\Users\name) and use '/'.
/// </summary>
public sealed class CaptureRules
{
    /// <summary>
    /// Copy every folder at the top of the profile (OneDrive, Dropbox, a "Work" folder...), not just
    /// the ones listed in <see cref="ProfileFolders"/>. AppData is never copied whole; only the parts listed.
    /// </summary>
    public bool WholeProfile { get; init; } = true;

    /// <summary>Top-of-profile folders never copied whole: program data, rebuilt by Windows and programs.</summary>
    public List<string> SkippedProfileFolders { get; init; } = 
    [
        "AppData", "MicrosoftEdgeBackups", "IntelGraphicsProfiles", "ansel", "Tracing",
        // Developer tool caches: downloaded packages that come back on their own, often many GB.
        ".cache", ".nuget", ".gradle", ".m2", ".npm", ".cargo", ".rustup", ".android", ".dotnet", ".conda", ".pyenv", "go",
    ];

    public List<string> ProfileFolders { get; init; } =
    [
        // The usual personal folders.
        "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos",
        "Favorites", "Contacts", "Links", "Saved Games", "Searches",

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

        // Outlook data files (.pst) Outlook keeps in its own folder. The .ost caches are left out below.
        "AppData/Local/Microsoft/Outlook",
        "AppData/Roaming/Microsoft/Outlook",
        // Sticky Notes, new and old.
        "AppData/Local/Packages/Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe/LocalState",
        "AppData/Roaming/Microsoft/Sticky Notes",
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
        // Outlook's offline caches of a mailbox that's still on the mail server. They rebuild themselves and can be huge.
        "*.ost", "*.nst",
    ];

    /// <summary>
    /// The folders (relative to the profile, with '/') to copy for one person. <paramref name="readableProfile"/>
    /// is where the profile can be read right now (a snapshot, or the pulled drive).
    /// </summary>
    public List<string> FoldersFor(string readableProfile)
    {
        var result = new List<string>(ProfileFolders);
        if (!WholeProfile) return result;
        try
        {
            foreach (var dir in new DirectoryInfo(readableProfile).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                // Junctions like "My Documents" and "Application Data" only point at other folders.
                if (FileCollector.IsLink(dir)) continue;
                if (SkippedProfileFolders.Contains(dir.Name, StringComparer.OrdinalIgnoreCase) || IsFolderExcluded(dir.Name)) continue;
                if (!result.Contains(dir.Name, StringComparer.OrdinalIgnoreCase)) result.Add(dir.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    public bool IsFolderExcluded(string folderName) =>
        ExcludedFolderNames.Any(n => string.Equals(n, folderName, StringComparison.OrdinalIgnoreCase));

    public bool IsFileExcluded(string fileName) =>
        ExcludedFilePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));
}
