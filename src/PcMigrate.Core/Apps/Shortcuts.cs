using System.Text;

namespace PcMigrate.Core.Apps;

public static class Shortcuts
{
    /// <summary>
    /// True if a .lnk file points somewhere inside <paramref name="folder"/>. A shortcut stores
    /// its target path as plain text (ANSI and/or UTF-16), so searching the bytes is enough.
    /// </summary>
    public static bool PointsInto(byte[] lnk, string folder)
    {
        var needle = folder.TrimEnd('\\', '/');
        if (needle.Length < 4) return false;
        return Contains(lnk, Encoding.Unicode.GetBytes(needle)) || Contains(lnk, Encoding.Latin1.GetBytes(needle));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        // Case-insensitive for ASCII letters, which is what paths in practice differ by.
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (Lower(haystack[i + j]) != Lower(needle[j])) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
}
