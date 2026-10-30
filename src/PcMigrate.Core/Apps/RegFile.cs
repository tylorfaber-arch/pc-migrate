using System.Text;

namespace PcMigrate.Core.Apps;

/// <summary>Helpers for .reg files, which Windows writes as UTF-16.</summary>
public static class RegFile
{
    /// <summary>
    /// A key exported from user A's registry starts with [HKEY_USERS\S-1-5-21-...-1001\...].
    /// To import it for user B, every such header is rewritten to B's root.
    /// </summary>
    public static string RewriteRoot(string content, string fromRoot, string toRoot)
    {
        var from = "[" + ExpandRoot(fromRoot);
        var fromDelete = "[-" + ExpandRoot(fromRoot);
        var to = "[" + ExpandRoot(toRoot);
        var toDelete = "[-" + ExpandRoot(toRoot);
        var sb = new StringBuilder(content.Length);
        foreach (var line in content.Split('\n'))
        {
            if (line.StartsWith(from, StringComparison.OrdinalIgnoreCase)) sb.Append(to).Append(line.AsSpan(from.Length));
            else if (line.StartsWith(fromDelete, StringComparison.OrdinalIgnoreCase)) sb.Append(toDelete).Append(line.AsSpan(fromDelete.Length));
            else sb.Append(line);
            sb.Append('\n');
        }
        sb.Length--; // the loop adds one '\n' too many
        return sb.ToString();
    }

    private static string ExpandRoot(string root) =>
        root.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase) ? @"HKEY_USERS\" + root[4..] : root;

    public static string Read(string path) => File.ReadAllText(path); // detects the UTF-16 byte order mark

    public static void Write(string path, string content) => File.WriteAllText(path, content, Encoding.Unicode);
}
