using System.Text;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;
using PcMigrate.Core.Windows;

namespace PcMigrate.Core.Apply;

public enum ConflictMode
{
    /// <summary>Replace a file on the new PC only if the copy from the old PC is newer.</summary>
    NewerWins,
    Overwrite,
    Skip,
}

public sealed class ApplyOptions
{
    /// <summary>Old user name to new user name, for when the accounts are named differently.</summary>
    public IReadOnlyDictionary<string, string> UserMap { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Old users whose files should not be copied at all.</summary>
    public IReadOnlySet<string> SkipUsers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public ConflictMode Conflicts { get; init; } = ConflictMode.NewerWins;
    public bool IncludeWifi { get; init; } = true;
    public bool IncludeUserSettings { get; init; } = true;
    public bool DryRun { get; init; }
    public bool IncludeApps { get; init; } = true;
    /// <summary>
    /// Move files out of the store instead of copying them. Used when the store was only made
    /// on this PC as a stepping stone (from a pulled drive), so it doesn't take up the space twice.
    /// </summary>
    public bool MoveFromStore { get; init; }
    /// <summary>Keys of the programs to install or copy. Null means every program that can move.</summary>
    public IReadOnlySet<string>? AppKeys { get; init; }
}

public sealed class ApplyResult
{
    public long FilesCopied { get; set; }
    public long FilesSkipped { get; set; }
    public long FilesFailed { get; set; }
    public long BytesCopied { get; set; }
    public List<string> MissingUsers { get; } = [];
    /// <summary>What happened to each program the old PC had a plan for.</summary>
    public List<AppResult> Apps { get; } = [];
    /// <summary>Programs from the old PC that still aren't on this one.</summary>
    public List<InstalledApp> AppsToReinstall { get; } = [];
    public List<string> Warnings { get; } = [];
    public string? ReportPath { get; set; }
}

/// <summary>Puts a migration store onto this (new) PC.</summary>
public sealed class ApplyEngine(ISystemProbe probe, IMigrationLog log, IAppPlatform? apps = null)
{
    public async Task<ApplyResult> RunAsync(string storeRoot, ApplyOptions options, CancellationToken ct = default)
    {
        var manifest = FolderSink.ReadManifest(storeRoot);
        var source = manifest.Inventory;
        log.Info($"Store from {source.Machine.ComputerName}, captured {source.Machine.CapturedUtc.ToLocalTime():g}, {manifest.Files.Count:N0} files.");

        var target = probe.GetInventory();
        var result = new ApplyResult();
        void Warn(string message)
        {
            log.Warn(message);
            result.Warnings.Add(message);
        }

        UserProfileInfo? FindTarget(string sourceUser)
        {
            if (options.SkipUsers.Contains(sourceUser)) return null;
            var name = options.UserMap.TryGetValue(sourceUser, out var mapped) ? mapped : sourceUser;
            return target.Users.FirstOrDefault(u => string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var user in source.Users.Where(u => manifest.Files.Any(f => StorePath.TryParseUserFile(f.StorePath, out var n, out _) && n == u.UserName)))
        {
            if (FindTarget(user.UserName) is null && !options.SkipUsers.Contains(user.UserName))
            {
                result.MissingUsers.Add(user.UserName);
                var name = options.UserMap.TryGetValue(user.UserName, out var mapped) ? mapped : user.UserName;
                Warn($"No user '{name}' on this PC, so {user.UserName}'s files were skipped. " +
                     $"Create the account and sign in once, or use --map {user.UserName}=<existing user>.");
            }
        }

        await RestoreFilesAsync(storeRoot, manifest, FindTarget, options, result, Warn, ct);

        if (!options.DryRun && OperatingSystem.IsWindows())
        {
            if (options.IncludeUserSettings) RestoreUserSettings(storeRoot, manifest, FindTarget, Warn);
            if (options.IncludeWifi) RestoreWifi(storeRoot, manifest);
        }

        if (options.IncludeApps && manifest.Apps.Count > 0)
            result.Apps.AddRange(await new AppRestore(apps, log, Warn).RunAsync(storeRoot, manifest, target, FindTarget, options, ct));

        var handled = result.Apps
            .Where(a => a.Outcome is AppOutcome.Installed or AppOutcome.Copied or AppOutcome.AlreadyInstalled)
            .Select(a => AppNames.Normalize(a.Name))
            .ToHashSet();
        result.AppsToReinstall.AddRange(FindMissingApps(source.Apps.Where(a => !AppPlanner.IsHidden(a)), target.Apps)
            .Where(a => !handled.Contains(AppNames.Normalize(a.Name))));
        result.ReportPath = WriteReport(storeRoot, manifest, result, options);
        return result;
    }

    private async Task RestoreFilesAsync(string storeRoot, Manifest manifest, Func<string, UserProfileInfo?> findTarget,
        ApplyOptions options, ApplyResult result, Action<string> warn, CancellationToken ct)
    {
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!StorePath.TryParseUserFile(file.StorePath, out var userName, out var relative)) continue;
            if (findTarget(userName) is not { } user) { result.FilesSkipped++; continue; }

            var destination = StorePath.ToLocal(user.ProfilePath, relative);
            if (File.Exists(destination))
            {
                var skip = options.Conflicts switch
                {
                    ConflictMode.Skip => true,
                    ConflictMode.NewerWins => File.GetLastWriteTimeUtc(destination) >= file.LastWriteUtc,
                    _ => false,
                };
                if (skip) { result.FilesSkipped++; continue; }
            }

            if (options.DryRun)
            {
                result.FilesCopied++;
                result.BytesCopied += file.Length;
                continue;
            }

            if (options.MoveFromStore ? await MoveFromStoreAsync(storeRoot, file, destination, warn, ct) : await CopyFromStoreAsync(storeRoot, file, destination, warn, ct))
            {
                result.FilesCopied++;
                result.BytesCopied += file.Length;
                log.FileProgress(result.FilesCopied, result.BytesCopied, destination);
            }
            else
            {
                result.FilesFailed++;
            }
        }
    }

    /// <summary>Copies one file out of the store, checking it against the manifest's checksum.</summary>
    internal static async Task<bool> CopyFromStoreAsync(string storeRoot, ManifestFile file, string destination, Action<string> warn, CancellationToken ct)
    {
        var temp = destination + ".pcmigrate-partial";
        try
        {
            var sourcePath = StorePath.ToLocal(storeRoot, file.StorePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            FileWriteResult written;
            await using (var input = HashingCopy.OpenForRead(sourcePath))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous))
            {
                written = await HashingCopy.CopyAsync(input, output, ct);
            }

            // Catches a damaged external drive or a store edited by hand.
            if (!string.Equals(written.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                warn($"Skipped damaged file in the store: {file.StorePath}");
                return false;
            }

            File.Move(temp, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, file.LastWriteUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SinkWriteException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            warn($"Could not write {destination}: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> MoveFromStoreAsync(string storeRoot, ManifestFile file, string destination, Action<string> warn, CancellationToken ct)
    {
        var sourcePath = StorePath.ToLocal(storeRoot, file.StorePath);
        try
        {
            if (!string.Equals(await HashingCopy.HashFileAsync(sourcePath, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                warn($"Skipped damaged file in the store: {file.StorePath}");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(sourcePath, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, file.LastWriteUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"Could not write {destination}: {ex.Message}");
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RestoreUserSettings(string storeRoot, Manifest manifest, Func<string, UserProfileInfo?> findTarget, Action<string> warn)
    {
        foreach (var settings in manifest.UserSettings)
        {
            if (findTarget(settings.UserName) is not { } user) continue;
            try
            {
                string? wallpaper = null;
                if (settings.WallpaperStorePath is { } wp)
                {
                    // Keep the image somewhere that will not be cleaned up.
                    var folder = Path.Combine(user.ProfilePath, "AppData", "Roaming", "pc-migrate");
                    Directory.CreateDirectory(folder);
                    wallpaper = Path.Combine(folder, Path.GetFileName(wp));
                    File.Copy(StorePath.ToLocal(storeRoot, wp), wallpaper, overwrite: true);
                }
                UserSettingsIO.Write(user, settings, wallpaper, user.Sid == probe.CurrentUserSid, log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                warn($"Could not restore settings for {user.UserName}: {ex.Message}");
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RestoreWifi(string storeRoot, Manifest manifest)
    {
        if (manifest.WifiProfiles.Count == 0) return;
        log.Info($"Adding {manifest.WifiProfiles.Count} Wi-Fi network(s)...");
        foreach (var storePath in manifest.WifiProfiles)
            WifiProfiles.Import(StorePath.ToLocal(storeRoot, storePath), log);
    }

    /// <summary>Programs that were on the old PC but are not installed here.</summary>
    internal static IEnumerable<InstalledApp> FindMissingApps(IEnumerable<InstalledApp> source, IEnumerable<InstalledApp> target)
    {
        var installed = target.Select(a => NormalizeAppName(a.Name)).ToHashSet();
        return source
            .Where(a => !installed.Contains(NormalizeAppName(a.Name)))
            .GroupBy(a => NormalizeAppName(a.Name))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
    }

    internal static string NormalizeAppName(string name) => AppNames.Normalize(name);

    private string? WriteReport(string storeRoot, Manifest manifest, ApplyResult result, ApplyOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"pc-migrate {ToolInfo.Version} apply report, {DateTime.Now:g}{(options.DryRun ? " (dry run, nothing was changed)" : "")}");
        sb.AppendLine($"From: {manifest.Inventory.Machine.ComputerName}   To: {Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine($"Files copied:  {result.FilesCopied:N0} ({CaptureEngine.FormatBytes(result.BytesCopied)})");
        sb.AppendLine($"Files skipped: {result.FilesSkipped:N0} (already up to date, or no matching user)");
        sb.AppendLine($"Files failed:  {result.FilesFailed:N0}");
        if (result.MissingUsers.Count > 0) sb.AppendLine($"Users not found on this PC: {string.Join(", ", result.MissingUsers)}");
        sb.AppendLine();
        if (result.Apps.Count > 0)
        {
            sb.AppendLine("Programs:");
            foreach (var app in result.Apps.Where(a => a.Outcome != AppOutcome.Manual))
                sb.AppendLine($"  {app.Outcome,-16} {app.Name}{(app.Message is null ? "" : "  (" + app.Message + ")")}");
            sb.AppendLine();
        }
        sb.AppendLine($"Programs on the old PC that are not installed here ({result.AppsToReinstall.Count}):");
        foreach (var app in result.AppsToReinstall)
            sb.AppendLine($"  - {app.Name}{(app.Version is null ? "" : " " + app.Version)}{(app.Publisher is null ? "" : "  [" + app.Publisher + "]")}");
        if (result.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var w in result.Warnings) sb.AppendLine("  - " + w);
        }

        var path = Path.Combine(storeRoot, $"apply-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not save the report: {ex.Message}");
            return null;
        }
    }
}
