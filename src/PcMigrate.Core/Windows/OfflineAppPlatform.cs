using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Models;
using PcMigrate.Core.Offline;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Program moving for a pulled drive. Registry reads come from the drive's own registry
/// files, and winget, which only knows this PC's programs, is asked about each one by name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfflineAppPlatform(OfflineWindows drive, IMigrationLog log) : IAppPlatform
{
    private readonly WindowsAppPlatform _here = new(log);

    public OfflinePaths Offline => drive.Paths;
    OfflinePaths? IAppPlatform.Offline => drive.Paths;

    public IReadOnlyList<WingetRow>? ListWinget(IReadOnlyList<InstalledApp> apps)
    {
        var rows = new ConcurrentBag<WingetRow>();
        var missing = false;
        log.Info($"Looking up {apps.Count} programs in winget...");
        Parallel.ForEach(apps.DistinctBy(a => AppNames.Normalize(a.Name)), new ParallelOptions { MaxDegreeOfParallelism = 4 }, app =>
        {
            if (missing) return;
            try
            {
                var query = SearchName(app.Name);
                if (query.Length < 3) return;
                var result = ProcessRunner.Run("winget.exe", "search", "--name", query, "--source", "winget",
                    "--accept-source-agreements", "--disable-interactivity");
                // winget leaves out the Source column when it's told which source to use.
                foreach (var row in WingetList.Parse(result.Output)) rows.Add(row with { Source = "winget" });
            }
            catch (Win32Exception)
            {
                missing = true;
            }
        });
        if (missing)
        {
            log.Warn("winget isn't installed on this PC, so programs from the drive can't be matched for automatic reinstall.");
            return null;
        }
        return rows.ToList();
    }

    /// <summary>"Notepad++ 8.6.2 (64-bit)" → "Notepad++": what winget's catalogue would call it.</summary>
    internal static string SearchName(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(w => !char.IsDigit(w.TrimStart('v', 'V').FirstOrDefault()) && !w.StartsWith('(') && w != "-");
        return string.Join(' ', words);
    }

    public bool HasServiceUnder(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + "\\";
        using var services = drive.SystemHive.OpenSubKey($@"{drive.ControlSet}\Services");
        if (services is null) return false;
        foreach (var name in services.GetSubKeyNames())
        {
            using var service = services.OpenSubKey(name);
            if (service?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image) continue;
            if (drive.Paths.Expand(image).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public IReadOnlyList<string> UserSoftwareKeys(UserProfileInfo user)
    {
        using var software = drive.User(user, log)?.Root.OpenSubKey("Software");
        return software is null ? [] : WindowsAppPlatform.TwoLevels(software);
    }

    public IReadOnlyList<string> MachineSoftwareKeys()
    {
        var keys = WindowsAppPlatform.TwoLevels(drive.Software);
        using var wow = drive.Software.OpenSubKey("WOW6432Node");
        return wow is null ? keys : keys.Concat(WindowsAppPlatform.TwoLevels(wow)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? ExportUserKey(UserProfileInfo user, string subKey, string file)
    {
        var hive = drive.User(user, log);
        if (hive is null) return null;
        return _here.ExportKey($@"{hive.RootPath}\Software\{subKey}", file) ? hive.RootPath : null;
    }

    /// <summary>
    /// Exports HKLM\SOFTWARE\... from the drive's registry. The file is written as if it had come
    /// from HKLM\SOFTWARE itself, so the new PC imports it to the right place.
    /// </summary>
    public bool ExportKey(string keyPath, string file)
    {
        const string software = @"HKLM\SOFTWARE";
        if (!keyPath.StartsWith(software + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        var loaded = drive.SoftwareRegPath + keyPath[software.Length..];
        if (!_here.ExportKey(loaded, file)) return false;
        RegFile.Write(file, RegFile.RewriteRoot(RegFile.Read(file),
            drive.SoftwareRegPath.Replace("HKLM", "HKEY_LOCAL_MACHINE"), @"HKEY_LOCAL_MACHINE\SOFTWARE"));
        return true;
    }

    public bool ImportUserFile(UserProfileInfo user, string file, string exportedRoot) => _here.ImportUserFile(user, file, exportedRoot);
    public bool ImportFile(string file) => _here.ImportFile(file);

    public IReadOnlyList<string> SharedShortcutFolders() =>
    [
        drive.Paths.Expand(@"%ProgramData%\Microsoft\Windows\Start Menu\Programs"),
        drive.Paths.Expand(@"%PUBLIC%\Desktop"),
    ];

    public Task<(bool Ok, string Message)> WingetInstallAsync(string id, CancellationToken ct) => _here.WingetInstallAsync(id, ct);
}
