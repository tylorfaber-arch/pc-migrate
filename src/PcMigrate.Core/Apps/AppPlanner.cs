using System.Text.RegularExpressions;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;

namespace PcMigrate.Core.Apps;

/// <summary>Decides, for each installed program, whether it can be reinstalled, copied, or has to be done by hand.</summary>
public static partial class AppPlanner
{
    public static List<AppEntry> Plan(Inventory inventory, IAppPlatform platform, IReadOnlyCollection<UserProfileInfo> users, CaptureRules rules)
    {
        var winget = platform.ListWinget();
        var byName = new Dictionary<string, WingetRow>();
        foreach (var row in winget ?? [])
        {
            if (row.Source is not ("winget" or "msstore")) continue;
            byName.TryAdd(AppNames.Normalize(row.Name), row);
        }

        var userKeys = users.ToDictionary(u => u.UserName, platform.UserSoftwareKeys);
        IReadOnlyList<string>? machineKeys = null;
        var plans = new List<AppEntry>();

        foreach (var app in inventory.Apps)
        {
            if (IsHidden(app)) continue;
            // Programs installed for just one person only move with that person.
            if (app.Scope != "Machine" && !users.Any(u => u.UserName.Equals(app.Scope, StringComparison.OrdinalIgnoreCase))) continue;

            var match = FindWinget(app, byName);
            var (method, reason) = Decide(app, match, winget is not null, platform);
            var entry = new AppEntry
            {
                Key = AppNames.Key(app.Name, app.RegistryKey),
                Name = app.Name,
                Version = app.Version,
                Publisher = app.Publisher,
                Method = method,
                Reason = reason,
                WingetId = match?.Id,
                InstallLocation = app.InstallLocation,
                UninstallKey = app.RegistryKey,
                Scope = app.Scope,
            };

            if (method != AppMethod.Manual)
            {
                var owners = app.Scope == "Machine" ? users : users.Where(u => u.UserName.Equals(app.Scope, StringComparison.OrdinalIgnoreCase)).ToList();
                entry.SettingsFolders.AddRange(SettingsLocator.FindFolders(entry, owners.Select(u => u.ProfilePath), rules));
                entry.UserRegistryKeys.AddRange(SettingsLocator.FindKeys(entry, owners.SelectMany(u => userKeys[u.UserName]).Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            if (method == AppMethod.Copy && app.Scope == "Machine")
            {
                machineKeys ??= platform.MachineSoftwareKeys();
                entry.MachineRegistryKeys.AddRange(SettingsLocator.FindKeys(entry, machineKeys));
            }
            plans.Add(entry);
        }

        // One row per program even if Windows lists it twice (e.g. 32- and 64-bit entries).
        return plans
            .GroupBy(p => AppNames.Normalize(p.Name))
            .Select(g => g.OrderBy(p => p.Method).First())
            .OrderBy(p => p.Method).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static WingetRow? FindWinget(InstalledApp app, Dictionary<string, WingetRow> byName)
    {
        var name = AppNames.Normalize(app.Name);
        if (byName.TryGetValue(name, out var exact)) return exact;
        // winget cuts long names short; accept a long enough prefix.
        return byName.Where(kv => kv.Key.Length >= 8 && name.StartsWith(kv.Key, StringComparison.Ordinal))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => kv.Value)
            .FirstOrDefault();
    }

    private static (AppMethod, string) Decide(InstalledApp app, WingetRow? winget, bool hasWinget, IAppPlatform platform)
    {
        var name = app.Name;
        if (app.UninstallString?.Contains("steam://", StringComparison.OrdinalIgnoreCase) == true)
            return (AppMethod.Manual, "A Steam game. Install it from your Steam library on the new PC; save files in your folders were copied.");
        if (Office().IsMatch(name))
            return (AppMethod.Manual, "Office is tied to your Microsoft account. Install it from office.com and sign in.");

        if (winget is not null)
            return (AppMethod.Reinstall, "Installed fresh on the new PC, then your settings are copied over.");

        if (Driver().IsMatch(name) || Driver().IsMatch(app.Publisher ?? ""))
            return (AppMethod.Manual, "Hardware software. The new PC needs the version made for its own hardware.");
        if (Security().IsMatch(name))
            return (AppMethod.Manual, "Security software has to be installed fresh.");
        if (app.IsMsi)
            return (AppMethod.Manual, "Installed with Windows Installer, so copying it would break updates and uninstalling. Get it from the maker's website.");

        var location = app.InstallLocation?.Trim().Trim('"').TrimEnd('\\');
        if (string.IsNullOrEmpty(location) || !Directory.Exists(location))
            return (AppMethod.Manual, hasWinget ? "Couldn't tell where it's installed." : "Couldn't tell where it's installed. (winget isn't on this PC, so nothing can be reinstalled automatically.)");
        if (IsTooBroad(location))
            return (AppMethod.Manual, "Its install folder is shared with other programs, so it can't be copied on its own.");
        if (platform.HasServiceUnder(location))
            return (AppMethod.Manual, "Runs a background Windows service, which needs a proper install.");

        return (AppMethod.Copy, "Not available to install automatically, so its program folder and settings are copied as they are. Works for most simple programs.");
    }

    private static bool IsTooBroad(string location)
    {
        var full = Path.TrimEndingDirectorySeparator(location);
        string?[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetPathRoot(full),
        ];
        if (roots.Any(r => !string.IsNullOrEmpty(r) && string.Equals(Path.TrimEndingDirectorySeparator(r), full, StringComparison.OrdinalIgnoreCase)))
            return true;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Things that come with Windows or get installed by other programs; listing them would only confuse.</summary>
    internal static bool IsHidden(InstalledApp app) => Hidden().IsMatch(app.Name);

    [GeneratedRegex(@"^(microsoft visual c\+\+|microsoft \.net|microsoft asp\.net|microsoft windows desktop runtime|windows software development kit|windows sdk|microsoft update health|microsoft edge|microsoft onedrive|windows pc health check|microsoft gameinput|vs_|microsoft visual studio installer|update for |security update|hotfix)|webview2|\bredistributable\b", RegexOptions.IgnoreCase)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"^(microsoft (office|365|project|visio)|office 16 click-to-run)", RegexOptions.IgnoreCase)]
    private static partial Regex Office();

    [GeneratedRegex(@"\bdriver\b|\bchipset\b|^(intel|nvidia|amd|advanced micro devices|realtek|synaptics|elan|qualcomm|dolby|conexant|broadcom)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Driver();

    [GeneratedRegex(@"\b(norton|mcafee|bitdefender|kaspersky|avast|avg|eset|sophos|crowdstrike|sentinelone|webroot|trend micro|antivirus|anti-virus)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Security();
}
