using System.Text;
using System.Text.RegularExpressions;

namespace PcMigrate.Core.Apps;

public static partial class AppNames
{
    /// <summary>"Google Chrome 118.0.1 (64-bit)" and "Google Chrome" should count as the same program.</summary>
    public static string Normalize(string name)
    {
        var s = name.ToLowerInvariant();
        s = Arch().Replace(s, " ");
        s = Version().Replace(s, " ");
        s = NonWord().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    /// <summary>A short id that is safe in paths and stays the same for the same program.</summary>
    public static string Key(string name, string uninstallKey)
    {
        var slug = Regex.Replace(Normalize(name), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(uninstallKey)))[..8].ToLowerInvariant();
        return (slug.Length == 0 ? "app" : slug) + "-" + hash;
    }

    [GeneratedRegex(@"\((x64|x86|64-bit|32-bit|64 bit|32 bit)\)|\b(x64|x86|amd64)\b")]
    private static partial Regex Arch();
    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b|\(\s*\)")]
    private static partial Regex Version();
    [GeneratedRegex(@"[^\p{L}\p{N}+#]+")]
    private static partial Regex NonWord();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
