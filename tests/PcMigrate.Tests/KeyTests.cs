using System.Numerics;
using PcMigrate.Core.Keys;

namespace PcMigrate.Tests;

public class KeyTests
{
    private const string Digits = "BCDFGHJKMPQRTVWXY2346789";

    /// <summary>The reverse of the decoder: packs a Windows 8+ style key (one N) into a DigitalProductId.</summary>
    private static byte[] Encode(string key, int offset = 52)
    {
        var plain = key.Replace("-", "");
        var n = plain.IndexOf('N');
        var digits = Digits[n] + plain.Remove(n, 1);
        var value = BigInteger.Zero;
        foreach (var c in digits) value = value * 24 + Digits.IndexOf(c);
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var id = new byte[offset + 16];
        Array.Copy(bytes, 0, id, offset, Math.Min(bytes.Length, 15));
        id[offset + 14] |= 0x08; // the "Windows 8 or later" flag
        return id;
    }

    [Theory]
    [InlineData("VK7JG-NPHTM-C97JM-9MPGT-3V66T")]
    [InlineData("YTMG3-N6DKC-DKB77-7M9GH-8HVX7")]
    [InlineData("W269N-WFGWX-YVC9B-4J6C9-T83GX")]
    [InlineData("NPPR9-FWDCX-D2C8J-H872K-2YT43")]
    public void DecodesWindowsKeys(string key) => Assert.Equal(key, ProductKeyDecoder.Decode(Encode(key)));

    [Fact]
    public void DecodesOfficeKeysFurtherIn() =>
        Assert.Equal("B9GN2-DXXQC-9DHKT-GGWCR-4X6XK", ProductKeyDecoder.Decode(Encode("B9GN2-DXXQC-9DHKT-GGWCR-4X6XK", 808), 808));

    [Fact]
    public void EmptyOrShortIdsHaveNoKey()
    {
        Assert.Null(ProductKeyDecoder.Decode(new byte[100]));
        Assert.Null(ProductKeyDecoder.Decode(new byte[10]));
    }

    [Fact]
    public void GenericKeysAreExplained()
    {
        Assert.Contains("digital licence", ProductKeyDecoder.WindowsNote("VK7JG-NPHTM-C97JM-9MPGT-3V66T"));
        Assert.Contains("licence server", ProductKeyDecoder.WindowsNote("W269N-WFGWX-YVC9B-4J6C9-T83GX"));
        Assert.Null(ProductKeyDecoder.WindowsNote("ABCDE-FGHJK-MNPQR-STVWX-YZ234"));
    }

    [Theory]
    [InlineData("1118-1234-5678-9012-3456-7890", true)]
    [InlineData("ABCD1234EFGH5678", true)]
    [InlineData("00000000-0000", false)]
    [InlineData("{6F1A2B3C-0000-4000-8000-123456789ABC}", false)]
    [InlineData(@"C:\Program Files\App", false)]
    [InlineData("To be filled by O.E.M.", false)]
    [InlineData("short", false)]
    [InlineData("NoDigitsAtAllHere", false)]
    public void KnowsWhatLooksLikeAKey(string text, bool key) => Assert.Equal(key, KeyText.LooksLikeKey(text));

    [Theory]
    [InlineData(@"Adobe\Acrobat\2020\Registration", "Adobe Acrobat 2020")]
    [InlineData(@"WOW6432Node\TechSmith\Snagit\License", "TechSmith Snagit")]
    [InlineData(@"Vendor", "Vendor")]
    public void NamesProductsFromTheirRegistryPath(string path, string name) => Assert.Equal(name, KeyText.ProductName(path));

    [Fact]
    public void ReportListsEveryKey()
    {
        var text = LicenseKeyReport.Format("OLD-PC", [new LicenseKey("Windows 11 Pro", "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", "note here", "x")]);
        Assert.Contains("OLD-PC", text);
        Assert.Contains("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", text);
        Assert.Contains("note here", text);
    }
}
