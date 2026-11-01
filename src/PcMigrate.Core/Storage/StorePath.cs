namespace PcMigrate.Core.Storage;

/// <summary>
/// Helpers for paths inside a migration store. Store paths always use '/' and are
/// always relative, so a store captured on one PC can be read on any other.
/// </summary>
public static class StorePath
{
    public const string UsersRoot = "files/users";
    public const string SettingsRoot = "settings";

    public static string Combine(params string[] parts) =>
        string.Join('/', parts
            .Select(p => p.Replace('\\', '/').Trim('/'))
            .Where(p => p.Length > 0));

    /// <summary>
    /// True when the path cannot escape the store root. Anything received over the
    /// network goes through this check, so a hostile sender cannot write to C:\Windows.
    /// </summary>
    public static bool IsSafe(string storePath)
    {
        if (string.IsNullOrWhiteSpace(storePath)) return false;
        if (storePath.Contains('\\') || storePath.Contains('\0') || storePath.Contains(':')) return false;
        if (storePath.StartsWith('/')) return false;
        foreach (var segment in storePath.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
        }
        return true;
    }

    /// <summary>Turns a store path into a full local path under <paramref name="root"/>.</summary>
    public static string ToLocal(string root, string storePath)
    {
        if (!IsSafe(storePath)) throw new InvalidDataException($"Unsafe path in migration store: '{storePath}'");

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, storePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes the migration store: '{storePath}'");
        return full;
    }

    /// <summary>Splits "files/users/{user}/{rest}" into its user and profile-relative parts.</summary>
    public static bool TryParseUserFile(string storePath, out string userName, out string relativePath)
    {
        userName = relativePath = "";
        var prefix = UsersRoot + "/";
        if (!storePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = storePath[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;
        userName = rest[..slash];
        relativePath = rest[(slash + 1)..];
        return true;
    }
}
