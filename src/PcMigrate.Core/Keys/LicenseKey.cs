namespace PcMigrate.Core.Keys;

/// <summary>A license or product key found on the old PC.</summary>
/// <param name="Product">What it unlocks, e.g. "Windows 11 Pro" or "Adobe Acrobat 2020".</param>
/// <param name="Key">The key as it would be typed in.</param>
/// <param name="Note">Anything worth knowing, e.g. that a generic key means Windows was activated another way.</param>
/// <param name="Source">Where it was read from (a registry path or "firmware").</param>
public sealed record LicenseKey(string Product, string Key, string? Note, string Source);

/// <summary>Turns the scrambled product keys Windows and Office store into the 25-character keys people type.</summary>
public static class ProductKeyDecoder
{
    private const string Digits = "BCDFGHJKMPQRTVWXY2346789";

    /// <summary>
    /// Decodes the 15 key bytes at <paramref name="offset"/> of a DigitalProductId value
    /// (52 for Windows, 808 for Office 2010 and later). Null if there's no key there.
    /// </summary>
    public static string? Decode(byte[] digitalProductId, int offset = 52)
    {
        if (digitalProductId.Length < offset + 15) return null;
        var bytes = digitalProductId.AsSpan(offset, 15).ToArray();
        if (bytes.All(b => b == 0)) return null;

        // Windows 8 and later mark their keys with a flag in the top byte, and put an "N" somewhere in the key.
        var newStyle = (bytes[14] >> 3) & 1;
        bytes[14] &= 0xF7;

        var chars = new char[25];
        var last = 0;
        for (var i = 24; i >= 0; i--)
        {
            var current = 0;
            for (var j = 14; j >= 0; j--)
            {
                current = current * 256 + bytes[j];
                bytes[j] = (byte)(current / 24);
                current %= 24;
            }
            chars[i] = Digits[current];
            last = current;
        }

        var key = new string(chars);
        if (newStyle == 1)
        {
            // The first character says where the N goes.
            var rest = key[1..];
            key = rest[..last] + "N" + rest[last..];
        }
        return string.Join('-', Enumerable.Range(0, 5).Select(i => key.Substring(i * 5, 5)));
    }

    /// <summary>Install keys Microsoft publishes for everyone. Finding one means Windows was activated some other way.</summary>
    private static readonly Dictionary<string, string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["YTMG3-N6DKC-DKB77-7M9GH-8HVX7"] = "Home",
        ["BT79Q-G7N6G-PGBYW-4YWX6-6F4BT"] = "Home Single Language",
        ["VK7JG-NPHTM-C97JM-9MPGT-3V66T"] = "Pro",
        ["8PTT6-RNW4C-6V7J2-C2D3X-MHBPB"] = "Pro Education",
        ["DXG7C-N36C4-C4HTG-X4T3X-2YV77"] = "Pro for Workstations",
        ["YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY"] = "Education",
        ["XGVPP-NMH47-7TTHJ-W3FW7-8HV2C"] = "Enterprise",
        // Volume licence (KMS) client keys: activated by a company server.
        ["TX9XD-98N7V-6WMQ6-BX7FG-H8Q99"] = "Home (KMS)",
        ["W269N-WFGWX-YVC9B-4J6C9-T83GX"] = "Pro (KMS)",
        ["NW6C2-QMPVW-D7KKK-3GKT6-VCFB2"] = "Education (KMS)",
        ["NPPR9-FWDCX-D2C8J-H872K-2YT43"] = "Enterprise (KMS)",
    };

    /// <summary>What to tell the person about a Windows key, if it isn't a real one of their own.</summary>
    public static string? WindowsNote(string key)
    {
        if (!Generic.TryGetValue(key, out var edition)) return null;
        return edition.EndsWith("(KMS)", StringComparison.Ordinal)
            ? "Generic volume licence key: Windows was activated by the company's licence server, not by this key."
            : "Generic install key: Windows was activated by a digital licence tied to the old PC's hardware (or a key in its firmware), not by this key.";
    }
}

/// <summary>The plain-text list of keys saved with each backup.</summary>
public static class LicenseKeyReport
{
    /// <summary>At the top of the backup folder, next to manifest.json, so it's easy to find.</summary>
    public const string StorePath = "license-keys.txt";

    public static string Format(string computerName, IEnumerable<LicenseKey> keys)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"License keys found on {computerName}, {DateTime.Now:g}");
        sb.AppendLine("Keep this file private: anyone with it can use these licences.");
        sb.AppendLine();
        foreach (var k in keys)
        {
            sb.AppendLine(k.Product);
            sb.AppendLine("  " + k.Key);
            if (k.Note is not null) sb.AppendLine("  " + k.Note);
            sb.AppendLine();
        }
        sb.AppendLine("Many programs today are licensed through an account instead of a key (Microsoft 365, Adobe Creative Cloud,");
        sb.AppendLine("most antivirus). Those aren't listed here; the person signs in again on the new PC.");
        return sb.ToString();
    }
}

/// <summary>Judging registry values found while looking for keys.</summary>
public static class KeyText
{
    /// <summary>"Adobe\Acrobat\2020\Registration" → "Adobe Acrobat 2020".</summary>
    public static string ProductName(string path)
    {
        var parts = path.Split('\\').Where(p => !p.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)).ToList();
        var noise = new[] { "Registration", "License", "Licence", "Licensing", "Settings", "Install", "Setup", "Config", "Options", "Activation" };
        while (parts.Count > 1 && noise.Contains(parts[^1], StringComparer.OrdinalIgnoreCase)) parts.RemoveAt(parts.Count - 1);
        return string.Join(' ', parts.Take(3));
    }

    /// <summary>Product keys are 8 to 80 characters of letters, digits and dashes, not paths, GUIDs or placeholders.</summary>
    public static bool LooksLikeKey(string text)
    {
        text = text.Trim();
        if (text.Length is < 8 or > 80) return false;
        if (text.StartsWith('{') || text.Contains('\\') || text.Contains(':') || text.Contains('@') || text.Contains(' ') && text.Split(' ').Length > 3) return false;
        if (!text.All(c => char.IsLetterOrDigit(c) || c is '-' or ' ' or '_' or '.')) return false;
        if (text.Distinct().Count() <= 2) return false; // 00000000, XXXX-XXXX
        if (text.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Default", StringComparison.OrdinalIgnoreCase)) return false;
        // Must have a digit, and either dashes or a decent length.
        return text.Any(char.IsDigit) && (text.Contains('-') || text.Length >= 12);
    }

}
