using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using PcMigrate.Core.Keys;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Finds license keys in a Windows registry: Windows itself, older Office, and the many programs that keep
/// a "ProductKey" or "SerialNumber" value. Works on the running Windows or on a pulled drive's SOFTWARE hive.
/// </summary>
[SupportedOSPlatform("windows")]
public static class KeyFinder
{
    /// <summary>Keys on the PC pc-migrate is running on, including one built into the firmware.</summary>
    public static List<LicenseKey> ForThisPc()
    {
        using var software = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey("SOFTWARE");
        var keys = software is null ? [] : Find(software, "HKLM\\SOFTWARE");
        if (FirmwareKey() is { } oem)
            keys.Insert(0, new LicenseKey("Windows (built into this PC)", oem,
                "Stored in this PC's firmware by its maker. It only activates Windows on this PC.", "firmware"));
        return keys;
    }

    /// <summary>Keys in a SOFTWARE hive, e.g. one loaded from a pulled drive.</summary>
    public static List<LicenseKey> Find(RegistryKey software, string label)
    {
        var found = new List<LicenseKey>();
        Windows(software, label, found);
        Office(software, label, found);
        Office(software.OpenSubKey("WOW6432Node"), label + "\\WOW6432Node", found);
        Scan(software, label, found);
        return found
            .GroupBy(k => (k.Product.ToUpperInvariant(), k.Key.ToUpperInvariant()))
            .Select(g => g.First())
            .ToList();
    }

    private static void Windows(RegistryKey software, string label, List<LicenseKey> found)
    {
        using var nt = software.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion");
        if (nt?.GetValue("DigitalProductId") is not byte[] id || ProductKeyDecoder.Decode(id) is not { } key) return;
        var product = nt.GetValue("ProductName") as string ?? "Windows";
        // Windows 11 still says "Windows 10" here; the build number tells them apart.
        if (int.TryParse(nt.GetValue("CurrentBuildNumber") as string, out var build) && build >= 22000)
            product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        found.Add(new LicenseKey(product, key, ProductKeyDecoder.WindowsNote(key), label + @"\Microsoft\Windows NT\CurrentVersion"));
    }

    private static void Office(RegistryKey? software, string label, List<LicenseKey> found)
    {
        if (software is null) return;
        using (software)
        {
            using var office = software.OpenSubKey(@"Microsoft\Office");
            if (office is null) return;
            foreach (var version in office.GetSubKeyNames().Where(v => v.EndsWith(".0", StringComparison.Ordinal)))
            {
                using var registration = office.OpenSubKey(version + @"\Registration");
                foreach (var guid in registration?.GetSubKeyNames() ?? [])
                {
                    using var entry = registration!.OpenSubKey(guid);
                    if (entry?.GetValue("DigitalProductId") is not byte[] id) continue;
                    // Office 2010 and later keep the key further in; Office 2007 and older where Windows does.
                    var key = ProductKeyDecoder.Decode(id, 808) ?? ProductKeyDecoder.Decode(id, 52);
                    if (key is null) continue;
                    var name = entry.GetValue("ProductName") as string ?? entry.GetValue("ConvertToEdition") as string ?? $"Microsoft Office {version}";
                    found.Add(new LicenseKey(name, key, null, $@"{label}\Microsoft\Office\{version}\Registration\{guid}"));
                }
            }

            // Microsoft 365 and Office 2019+ from the Microsoft Store or office.com: no key on the PC.
            using var c2r = office.OpenSubKey(@"ClickToRun\Configuration");
            if (c2r?.GetValue("ProductReleaseIds") is string releases && releases.Length > 0)
                found.Add(new LicenseKey($"Microsoft Office ({releases})", "(none on the PC)",
                    "This Office is licensed through a Microsoft account or the company's Microsoft 365. Sign in to it on the new PC.",
                    $@"{label}\Microsoft\Office\ClickToRun\Configuration"));
        }
    }

    private static readonly HashSet<string> KeyValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProductKey", "Product Key", "ProductKeys", "SerialNumber", "Serial Number", "Serial", "SerialNo", "CDKey", "CD Key",
        "LicenseKey", "License Key", "LicenceKey", "RegKey", "RegistrationKey", "Registration Key", "RegCode", "ActivationKey",
        "ActivationCode", "UnlockCode", "Key Code", "KeyCode",
    };

    /// <summary>Branches that are huge and hold no program keys, or only hardware serial numbers.</summary>
    private static readonly HashSet<string> SkipBranches = new(StringComparer.OrdinalIgnoreCase)
    {
        "Classes", "Microsoft", "Policies", "Clients", "RegisteredApplications", "WOW6432Node\\Classes", "WOW6432Node\\Microsoft",
        "Intel", "NVIDIA Corporation", "AMD", "ATI Technologies", "Realtek", "Synaptics", "Logitech", "Dolby", "Qualcomm", "Broadcom",
    };

    /// <summary>Looks through each program maker's key for values named like a key or serial number.</summary>
    private static void Scan(RegistryKey software, string label, List<LicenseKey> found)
    {
        var budget = 200_000; // keys looked at; stops a pathological registry from taking minutes
        void Walk(RegistryKey key, string path, int depth)
        {
            if (--budget < 0) return;
            foreach (var name in SafeValueNames(key))
            {
                if (!KeyValueNames.Contains(name)) continue;
                var text = key.GetValue(name) switch
                {
                    string s => s,
                    byte[] b when b.Length is > 8 and < 128 && b.All(c => c == 0 || (c >= 32 && c < 127)) => Encoding.ASCII.GetString(b).TrimEnd('\0'),
                    _ => null,
                };
                if (text is not null && KeyText.LooksLikeKey(text))
                    found.Add(new LicenseKey(KeyText.ProductName(path), text.Trim(), null, $@"{label}\{path}"));
            }
            if (depth >= 5) return;
            foreach (var child in SafeSubKeyNames(key))
            {
                var childPath = path.Length == 0 ? child : path + "\\" + child;
                if (SkipBranches.Contains(childPath)) continue;
                try
                {
                    using var sub = key.OpenSubKey(child);
                    if (sub is not null) Walk(sub, childPath, depth + 1);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        Walk(software, "", 0);
    }

    private static string[] SafeValueNames(RegistryKey key)
    {
        try { return key.GetValueNames(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string[] SafeSubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    /// <summary>The Windows key a PC maker stored in the firmware (the ACPI MSDM table), if there is one.</summary>
    private static string? FirmwareKey()
    {
        const uint Acpi = 0x41435049, Msdm = 0x4D44534D;
        try
        {
            var size = GetSystemFirmwareTable(Acpi, Msdm, null, 0);
            if (size < 56 + 29) return null;
            var buffer = new byte[size];
            if (GetSystemFirmwareTable(Acpi, Msdm, buffer, size) != size) return null;
            var key = Encoding.ASCII.GetString(buffer, 56, 29);
            return key.Length == 29 && key.Count(c => c == '-') == 4 ? key : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);
}
