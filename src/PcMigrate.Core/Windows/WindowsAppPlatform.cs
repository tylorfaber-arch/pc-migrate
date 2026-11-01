using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Models;

namespace PcMigrate.Core.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsAppPlatform(IMigrationLog log) : IAppPlatform
{
    // winget exit codes that mean "nothing to do": already installed, or no newer version.
    private static readonly HashSet<int> AlreadyInstalledCodes = [unchecked((int)0x8A150061), unchecked((int)0x8A15002B)];

    public IReadOnlyList<WingetRow>? ListWinget()
    {
        try
        {
            var result = ProcessRunner.Run("winget.exe", "list", "--accept-source-agreements", "--disable-interactivity");
            var rows = WingetList.Parse(result.Output);
            if (rows.Count == 0)
            {
                log.Warn($"winget didn't list any programs (exit {result.ExitCode}), so nothing can be reinstalled automatically.");
                return null;
            }
            return rows;
        }
        catch (Win32Exception)
        {
            log.Warn("winget isn't installed on this PC, so programs can't be matched for automatic reinstall.");
            return null;
        }
    }

    public bool HasServiceUnder(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + "\\";
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return false;
        foreach (var name in services.GetSubKeyNames())
        {
            using var service = services.OpenSubKey(name);
            if (service?.GetValue("ImagePath") is not string image) continue;
            var path = Environment.ExpandEnvironmentVariables(image).Trim().TrimStart('"');
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public IReadOnlyList<string> UserSoftwareKeys(UserProfileInfo user)
    {
        using var hive = UserHive.Open(user, writable: false, log);
        if (hive is null) return [];
        using var software = hive.Root.OpenSubKey("Software");
        return software is null ? [] : TwoLevels(software);
    }

    public IReadOnlyList<string> MachineSoftwareKeys()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var software = hklm.OpenSubKey("SOFTWARE");
        var keys = software is null ? [] : TwoLevels(software);
        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var software32 = hklm32.OpenSubKey("SOFTWARE");
        return software32 is null ? keys : keys.Concat(TwoLevels(software32)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> TwoLevels(RegistryKey software)
    {
        var result = new List<string>();
        foreach (var top in software.GetSubKeyNames())
        {
            result.Add(top);
            try
            {
                using var sub = software.OpenSubKey(top);
                if (sub is not null) result.AddRange(sub.GetSubKeyNames().Select(n => top + "\\" + n));
            }
            catch (System.Security.SecurityException) { }
        }
        return result;
    }

    public string? ExportUserKey(UserProfileInfo user, string subKey, string file)
    {
        using var hive = UserHive.Open(user, writable: false, log);
        if (hive is null) return null;
        return ExportKey($@"{hive.RootPath}\Software\{subKey}", file) ? hive.RootPath : null;
    }

    public bool ExportKey(string keyPath, string file) =>
        ProcessRunner.Run("reg.exe", "export", keyPath, file, "/y").ExitCode == 0;

    public bool ImportUserFile(UserProfileInfo user, string file, string exportedRoot)
    {
        using var hive = UserHive.Open(user, writable: true, log);
        if (hive is null) return false;
        var rewritten = Path.Combine(Path.GetTempPath(), "pcmigrate-" + Guid.NewGuid().ToString("N") + ".reg");
        try
        {
            RegFile.Write(rewritten, RegFile.RewriteRoot(RegFile.Read(file), exportedRoot, hive.RootPath));
            return ImportFile(rewritten);
        }
        finally
        {
            try { File.Delete(rewritten); } catch (IOException) { }
        }
    }

    public bool ImportFile(string file) => ProcessRunner.Run("reg.exe", "import", file).ExitCode == 0;

    public IReadOnlyList<string> SharedShortcutFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    ];

    public async Task<(bool Ok, string Message)> WingetInstallAsync(string id, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("winget.exe", ct, "install", "--id", id, "--exact", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity");
            if (result.ExitCode == 0 || AlreadyInstalledCodes.Contains(result.ExitCode)) return (true, "");
            return (false, Explain(result));
        }
        catch (Win32Exception)
        {
            return (false, "winget isn't on this PC. Install \"App Installer\" from the Microsoft Store, then run pc-migrate again.");
        }
    }

    private static string Explain(ProcessResult result)
    {
        var text = result.Output;
        if (text.Contains("administrator", StringComparison.OrdinalIgnoreCase) || text.Contains("elevated", StringComparison.OrdinalIgnoreCase))
            return "This program's installer refuses to run as administrator. Install it normally from its website.";
        // The last non-progress line usually says what went wrong.
        var last = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 3 && !l.StartsWith('█') && !l.StartsWith('▒'));
        return $"Install failed (winget code 0x{result.ExitCode:X8}){(last is null ? "" : ": " + last)}";
    }
}
