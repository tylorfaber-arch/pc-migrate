using PcMigrate.Core.Apps;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Core.Apply;

/// <summary>Puts programs onto the new PC: winget installs, copied program folders, and their registry settings.</summary>
internal sealed class AppRestore(IAppPlatform? platform, IMigrationLog log, Action<string> warn)
{
    public async Task<List<AppResult>> RunAsync(string storeRoot, Manifest manifest, Inventory target,
        Func<string, UserProfileInfo?> findTarget, ApplyOptions options, CancellationToken ct)
    {
        var results = new List<AppResult>();
        var installed = target.Apps.Select(a => AppNames.Normalize(a.Name)).ToHashSet();
        var chosen = manifest.Apps
            .Where(a => a.Method != AppMethod.Manual && (options.AppKeys is null || options.AppKeys.Contains(a.Key)))
            .ToList();

        var i = 0;
        foreach (var app in chosen)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            var already = installed.Contains(AppNames.Normalize(app.Name));
            log.Info(already ? $"{app.Name} is already installed; copying its settings ({i} of {chosen.Count})..."
                : app.Method == AppMethod.Reinstall ? $"Installing {app.Name} ({i} of {chosen.Count})..."
                : $"Copying {app.Name} ({i} of {chosen.Count})...");

            if (options.DryRun)
            {
                results.Add(new AppResult { Name = app.Name, Outcome = already ? AppOutcome.AlreadyInstalled : app.Method == AppMethod.Reinstall ? AppOutcome.Installed : AppOutcome.Copied, Message = "Dry run" });
                continue;
            }

            AppResult result;
            if (already)
                result = new AppResult { Name = app.Name, Outcome = AppOutcome.AlreadyInstalled, Message = "Already on this PC, so only its settings were copied." };
            else if (app.Method == AppMethod.Reinstall)
                result = await InstallAsync(app, ct);
            else
                result = await CopyProgramAsync(storeRoot, manifest, app, findTarget, ct);

            if (result.Outcome != AppOutcome.Failed) ImportUserSettings(storeRoot, app, findTarget);
            if (result.Outcome == AppOutcome.Failed) warn($"{app.Name}: {result.Message}");
            results.Add(result);
        }

        foreach (var app in manifest.Apps.Except(chosen))
        {
            results.Add(app.Method == AppMethod.Manual
                ? new AppResult { Name = app.Name, Outcome = AppOutcome.Manual, Message = app.Reason }
                : new AppResult { Name = app.Name, Outcome = AppOutcome.NotSelected, Message = "Not selected." });
        }
        return results;
    }

    private async Task<AppResult> InstallAsync(AppEntry app, CancellationToken ct)
    {
        if (platform is null || app.WingetId is null)
            return new AppResult { Name = app.Name, Outcome = AppOutcome.Failed, Message = "Can't install programs on this PC." };
        var (ok, message) = await platform.WingetInstallAsync(app.WingetId, ct);
        return new AppResult { Name = app.Name, Outcome = ok ? AppOutcome.Installed : AppOutcome.Failed, Message = ok ? "Installed fresh, with your settings." : message };
    }

    private async Task<AppResult> CopyProgramAsync(string storeRoot, Manifest manifest, AppEntry app,
        Func<string, UserProfileInfo?> findTarget, CancellationToken ct)
    {
        if (app.ProgramStorePath is null || app.InstallLocation is null)
            return new AppResult { Name = app.Name, Outcome = AppOutcome.Failed, Message = "Its program folder wasn't saved on the old PC." };

        var destinationRoot = Rebase(app.InstallLocation.Trim().Trim('"').TrimEnd('\\', '/'), manifest, findTarget);
        if (destinationRoot is null)
            return new AppResult { Name = app.Name, Outcome = AppOutcome.Failed, Message = "It was installed for a person who isn't on this PC." };

        var prefix = app.ProgramStorePath + "/";
        int copied = 0, failed = 0;
        foreach (var file in manifest.Files.Where(f => f.StorePath.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var destination = StorePath.ToLocal(destinationRoot, file.StorePath[prefix.Length..]);
            if (await Apply.ApplyEngine.CopyFromStoreAsync(storeRoot, file, destination, warn, ct)) copied++;
            else failed++;
        }
        if (copied == 0 || failed > 0)
            return new AppResult { Name = app.Name, Outcome = AppOutcome.Failed, Message = $"{failed} of its files couldn't be copied." };

        if (platform is not null)
        {
            foreach (var reg in app.RegistryFiles.Where(r => r.UserName is null))
                if (!platform.ImportFile(StorePath.ToLocal(storeRoot, reg.StorePath))) warn($"{app.Name}: some registry settings couldn't be restored.");
        }

        foreach (var shortcut in app.Shortcuts)
        {
            try
            {
                if (File.Exists(shortcut.TargetPath)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(shortcut.TargetPath)!);
                File.Copy(StorePath.ToLocal(storeRoot, shortcut.StorePath), shortcut.TargetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn($"{app.Name}: couldn't add shortcut {Path.GetFileName(shortcut.TargetPath)}: {ex.Message}");
            }
        }
        return new AppResult { Name = app.Name, Outcome = AppOutcome.Copied, Message = $"Copied {copied:N0} files." };
    }

    /// <summary>Programs installed inside a person's profile move to that person's profile on this PC.</summary>
    private static string? Rebase(string location, Manifest manifest, Func<string, UserProfileInfo?> findTarget)
    {
        foreach (var user in manifest.Inventory.Users)
        {
            var profile = Path.TrimEndingDirectorySeparator(user.ProfilePath);
            if (!location.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase) &&
                !location.StartsWith(profile + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = findTarget(user.UserName);
            return target is null ? null : Path.Combine(target.ProfilePath, location[(profile.Length + 1)..].Replace('\\', Path.DirectorySeparatorChar));
        }
        return location;
    }

    private void ImportUserSettings(string storeRoot, AppEntry app, Func<string, UserProfileInfo?> findTarget)
    {
        if (platform is null) return;
        foreach (var reg in app.RegistryFiles.Where(r => r.UserName is not null && r.ExportedRoot is not null))
        {
            if (findTarget(reg.UserName!) is not { } user) continue;
            if (!platform.ImportUserFile(user, StorePath.ToLocal(storeRoot, reg.StorePath), reg.ExportedRoot!))
                warn($"{app.Name}: some settings for {user.UserName} couldn't be restored.");
        }
    }
}
