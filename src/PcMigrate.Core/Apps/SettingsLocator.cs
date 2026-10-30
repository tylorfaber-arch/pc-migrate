using PcMigrate.Core.Capture;

namespace PcMigrate.Core.Apps;

/// <summary>
/// Guesses where a program keeps its settings. Most Windows programs use a folder
/// named after themselves (or their maker) in AppData, and a registry key with the
/// same name under HKEY_CURRENT_USER\Software.
/// </summary>
public static class SettingsLocator
{
    private static readonly string[] AppDataRoots = ["AppData/Roaming", "AppData/Local", "AppData/LocalLow"];

    // Folders and keys shared by many programs, never claimed by one.
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Programs", "CrashDumps", "D3DSCache", "Comms", "ConnectedDevicesPlatform",
        "Publishers", "PeerDistRepub", "VirtualStore", "Package Cache", "Classes", "Policies", "Wow6432Node",
        "AppDataLow", "RegisteredApplications", "Clients", "IM Providers", "Intel", "NVIDIA Corporation", "Google",
        "Mozilla", "Apple Computer, Inc.", "SquirrelTemp", "pip", "npm-cache", "Windows",
    };

    // Words too common to identify a program on their own.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "google", "adobe", "the", "apple", "intel", "nvidia", "amd", "oracle", "mozilla",
        "update", "setup", "tools", "tool", "app", "application", "software", "corporation", "inc", "llc", "ltd",
        "free", "pro", "for", "and", "player", "desktop", "client", "launcher", "studio", "media",
    };

    // Programs whose settings folder has nothing to do with their name.
    private static readonly Dictionary<string, string[]> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.VisualStudioCode"] = ["AppData/Roaming/Code", ".vscode"],
        ["Microsoft.VisualStudioCode.Insiders"] = ["AppData/Roaming/Code - Insiders", ".vscode-insiders"],
        ["OBSProject.OBSStudio"] = ["AppData/Roaming/obs-studio"],
        ["Discord.Discord"] = ["AppData/Roaming/discord"],
        ["Git.Git"] = [".gitconfig"],
        ["PuTTY.PuTTY"] = [],
        ["WinSCP.WinSCP"] = ["AppData/Roaming/WinSCP.ini"],
        ["Valve.Steam"] = [],
        ["Zoom.Zoom"] = ["AppData/Roaming/Zoom"],
    };

    private static readonly Dictionary<string, string[]> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PuTTY.PuTTY"] = [@"SimonTatham\PuTTY"],
        ["Valve.Steam"] = [],
    };

    /// <summary>Names a program's own settings folder or key is likely to have.</summary>
    public static HashSet<string> Candidates(string name, string? publisher = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = AppNames.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return result;

        if (words.Length == 1 || !Generic.Contains(words[0])) result.Add(words[0]);
        result.Add(string.Concat(words));
        result.Add(string.Join('-', words));
        result.Add(string.Join(' ', words));
        if (words.Length >= 2 && Generic.Contains(words[0])) result.Add(words[1]);

        result.RemoveWhere(c => c.Length < 3 || Generic.Contains(c));
        return result;
    }

    /// <summary>
    /// Names of the maker's folder, which may hold the program's folder ("Valve\Steam").
    /// Never matched on their own: the maker's folder can hold several programs.
    /// </summary>
    private static HashSet<string> PublisherCandidates(string? publisher)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (publisher is null) return result;
        var words = AppNames.Normalize(publisher).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("inc" or "llc" or "ltd" or "corporation" or "corp" or "gmbh" or "ab" or "co")).ToArray();
        if (words.Length == 0) return result;
        result.Add(words[0]);
        result.Add(string.Concat(words));
        result.Add(string.Join(' ', words));
        result.RemoveWhere(c => c.Length < 3);
        return result;
    }

    /// <summary>Settings folders (profile-relative, '/' separated) that exist in any of the given profiles.</summary>
    public static List<string> FindFolders(AppEntry app, IEnumerable<string> profilePaths, CaptureRules rules)
    {
        if (app.WingetId is not null && KnownFolders.TryGetValue(app.WingetId, out var known))
            return known.Where(k => profilePaths.Any(p => Exists(Path.Combine(p, k)))).ToList();

        var candidates = Candidates(app.Name);
        var makers = PublisherCandidates(app.Publisher);
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profilePaths)
        {
            foreach (var root in AppDataRoots)
            {
                var dir = Path.Combine(profile, root.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(dir).Select(Path.GetFileName).ToList()!; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                var matches = new List<string>();
                foreach (var child in children)
                {
                    if (Shared.Contains(child)) continue;
                    if (candidates.Contains(child)) matches.Add(child);
                    else if (makers.Contains(child))
                    {
                        try
                        {
                            matches.AddRange(Directory.EnumerateDirectories(Path.Combine(dir, child))
                                .Select(Path.GetFileName).Where(n => candidates.Contains(n!)).Select(n => child + "/" + n));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                }

                foreach (var match in matches)
                {
                    var relative = root + "/" + match;
                    // Already copied for everyone (browsers and so on).
                    if (rules.ProfileFolders.Any(f => relative.StartsWith(f, StringComparison.OrdinalIgnoreCase) || f.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    found.Add(relative);
                }
            }
        }
        return found.ToList();
    }

    /// <summary>Keys under HKCU\Software (or HKLM\SOFTWARE) that look like they belong to the program.</summary>
    public static List<string> FindKeys(AppEntry app, IEnumerable<string> softwareKeys)
    {
        if (app.WingetId is not null && KnownKeys.TryGetValue(app.WingetId, out var known))
            return known.Where(k => softwareKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();

        var candidates = Candidates(app.Name);
        var result = new List<string>();
        foreach (var key in softwareKeys)
        {
            var parts = key.Split('\\');
            if (Shared.Contains(parts[0])) continue;
            // "Spotify", or "Maker\Program" where the program part matches.
            if (parts.Length == 1 && candidates.Contains(parts[0])) result.Add(key);
            else if (parts.Length == 2 && candidates.Contains(parts[1])) result.Add(key);
        }
        // "Spotify\Spotify" is already inside "Spotify".
        return result.Where(k => !result.Any(o => k.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);
}
