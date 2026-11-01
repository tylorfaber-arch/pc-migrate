using PcMigrate.Core.Apps;
using PcMigrate.Core.Models;
using PcMigrate.Core.Offline;
using PcMigrate.Core.Storage;
using PcMigrate.Core.Windows;

namespace PcMigrate.Core.Capture;

public sealed class CaptureOptions
{
    /// <summary>Profile folder names to capture. Null or empty captures everyone.</summary>
    public IReadOnlyCollection<string>? Users { get; init; }
    public bool UseShadowCopy { get; init; } = true;
    public bool IncludeWifi { get; init; } = true;
    public bool IncludeUserSettings { get; init; } = true;
    public CaptureRules Rules { get; init; } = new();
    /// <summary>Programs to bring along, as planned by <see cref="AppPlanner"/>. Manual ones are only recorded.</summary>
    public IReadOnlyList<AppEntry> Apps { get; init; } = [];
}

/// <summary>Collects everything from the old PC and sends it to a sink (folder or network).</summary>
public sealed class CaptureEngine(ISystemProbe probe, IMigrationLog log, IAppPlatform? apps = null)
{
    private const int MaxWarningsInManifest = 2000;

    public async Task<Manifest> RunAsync(IMigrationSink sink, CaptureOptions options, CancellationToken ct = default)
    {
        var offline = probe.Offline;
        log.Info(offline is null ? "Scanning this PC..." : $"Reading the drive at {offline.Root}...");
        var inventory = probe.GetInventory();
        var users = SelectUsers(inventory, options);
        var manifest = new Manifest { Inventory = inventory };
        void Warn(string message)
        {
            log.Warn(message);
            if (manifest.Warnings.Count < MaxWarningsInManifest) manifest.Warnings.Add(message);
        }

        log.Info($"Found {inventory.Users.Count} user(s) and {inventory.Apps.Count} program(s). Capturing: {string.Join(", ", users.Select(u => u.UserName))}");

        var snapshots = new Dictionary<string, IDisposable>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Func<string, string> mapPath = p => p;
            // A pulled drive has no running Windows holding files open, so it needs no snapshot.
            if (offline is not null) mapPath = offline.ToReadable;
            else if (options.UseShadowCopy && OperatingSystem.IsWindows())
            {
                var volumes = users.Select(u => u.ProfilePath)
                    .Concat(options.Apps.Where(a => a.Method == AppMethod.Copy && a.InstallLocation is not null).Select(a => a.InstallLocation!))
                    .Select(p => Path.GetPathRoot(p)!)
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                mapPath = CreateSnapshots(volumes, snapshots, Warn);
                manifest.UsedShadowCopy = snapshots.Count > 0;
            }

            long files = 0, bytes = 0;
            // Problems reading one file are warnings; a SinkWriteException (drive full,
            // network dropped) is not caught here and stops the capture.
            async Task<bool> AddFile(string fullPath, string storePath, DateTime lastWriteUtc)
            {
                ct.ThrowIfCancellationRequested();
                if (!StorePath.IsSafe(storePath))
                {
                    Warn($"Skipped file with an unusual name: {fullPath}");
                    return false;
                }
                try
                {
                    FileWriteResult written;
                    await using (var input = HashingCopy.OpenForRead(fullPath))
                        written = await sink.AddFileAsync(storePath, input, lastWriteUtc, ct);
                    manifest.Files.Add(new ManifestFile { StorePath = storePath, Length = written.Length, LastWriteUtc = lastWriteUtc, Sha256 = written.Sha256 });
                    files++;
                    bytes += written.Length;
                    log.FileProgress(files, bytes, storePath);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warn($"Could not copy {fullPath}: {ex.Message}");
                    return false;
                }
            }

            // Program settings folders ride along with each person's files.
            var appFolders = options.Apps.Where(a => a.Method != AppMethod.Manual).SelectMany(a => a.SettingsFolders);
            var folders = options.Rules.ProfileFolders.Concat(appFolders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var user in users)
            {
                log.Info($"Copying files for {user.UserName}...");
                foreach (var folder in folders)
                {
                    var source = mapPath(Path.Combine(user.ProfilePath, folder.Replace('/', Path.DirectorySeparatorChar)));
                    // A few programs keep their settings in a single file (e.g. .gitconfig).
                    if (File.Exists(source))
                    {
                        await AddFile(source, StorePath.Combine(StorePath.UsersRoot, user.UserName, folder), File.GetLastWriteTimeUtc(source));
                        continue;
                    }
                    foreach (var file in FileCollector.Enumerate(source, folder, options.Rules, Warn))
                        await AddFile(file.FullPath, StorePath.Combine(StorePath.UsersRoot, user.UserName, file.RelativePath), file.LastWriteUtc);
                }
            }

            if (options.Apps.Count > 0)
                await new AppCapture(apps, log, Warn).RunAsync(options.Apps, users, manifest, mapPath, AddFile, sink, ct);

            if (options.IncludeUserSettings && OperatingSystem.IsWindows())
                await CaptureUserSettingsAsync(users, sink, manifest, Warn, ct);
            if (options.IncludeWifi && offline is not null)
                await CaptureOfflineWifiAsync(offline, AddFile, manifest, Warn);
            else if (options.IncludeWifi && OperatingSystem.IsWindows())
                await CaptureWifiAsync(sink, manifest, Warn, ct);

            log.Info($"Captured {files:N0} files ({FormatBytes(bytes)}). Writing manifest...");
            await sink.CompleteAsync(manifest, ct);
            var manual = manifest.Apps.Count(a => a.Method == AppMethod.Manual);
            log.Info($"Done: {files:N0} files ({FormatBytes(bytes)}), {manifest.Apps.Count - manual} program(s) saved, " +
                     $"{manual} to install by hand, {manifest.Warnings.Count} warning(s).");
            return manifest;
        }
        finally
        {
            foreach (var snapshot in snapshots.Values) snapshot.Dispose();
        }
    }

    private static List<UserProfileInfo> SelectUsers(Inventory inventory, CaptureOptions options)
    {
        if (options.Users is not { Count: > 0 }) return inventory.Users;

        var selected = new List<UserProfileInfo>();
        foreach (var name in options.Users)
        {
            var match = inventory.Users.FirstOrDefault(u => string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"No user called '{name}' on this PC. Users found: {string.Join(", ", inventory.Users.Select(u => u.UserName))}");
            selected.Add(match);
        }
        return selected;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private Func<string, string> CreateSnapshots(IEnumerable<string> volumes, Dictionary<string, IDisposable> snapshots, Action<string> warn)
    {
        foreach (var volume in volumes)
        {
            try
            {
                log.Info($"Taking a snapshot of {volume} so open files can be copied...");
                snapshots[volume] = ShadowCopy.Create(volume);
            }
            catch (Exception ex)
            {
                warn($"Snapshot of {volume} failed, copying live files instead (files that are open may be skipped): {ex.Message}");
            }
        }

        return livePath =>
        {
            var root = Path.GetPathRoot(livePath)!;
            if (!snapshots.TryGetValue(root, out var s)) return livePath;
            var mapped = ((ShadowCopy)s).MapPath(livePath);
            return Directory.Exists(mapped) || !Directory.Exists(livePath) ? mapped : livePath;
        };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task CaptureUserSettingsAsync(List<UserProfileInfo> users, IMigrationSink sink, Manifest manifest, Action<string> warn, CancellationToken ct)
    {
        foreach (var user in users)
        {
            var captured = UserSettingsIO.Read(user, log, probe.Offline);
            if (captured is null) continue;

            if (captured.WallpaperSourcePath is { } wallpaper)
            {
                var storePath = StorePath.Combine(StorePath.SettingsRoot, "users", user.UserName, "wallpaper", Path.GetFileName(wallpaper));
                try
                {
                    await using var input = HashingCopy.OpenForRead(wallpaper);
                    var written = await sink.AddFileAsync(storePath, input, File.GetLastWriteTimeUtc(wallpaper), ct);
                    manifest.Files.Add(new ManifestFile { StorePath = storePath, Length = written.Length, LastWriteUtc = File.GetLastWriteTimeUtc(wallpaper), Sha256 = written.Sha256 });
                    captured.Settings.WallpaperStorePath = storePath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warn($"Could not copy wallpaper for {user.UserName}: {ex.Message}");
                }
            }
            manifest.UserSettings.Add(captured.Settings);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task CaptureWifiAsync(IMigrationSink sink, Manifest manifest, Action<string> warn, CancellationToken ct)
    {
        log.Info("Exporting saved Wi-Fi networks...");
        var temp = Path.Combine(Path.GetTempPath(), "pcmigrate-wifi-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var xml in WifiProfiles.Export(temp, log))
            {
                var storePath = StorePath.Combine(StorePath.SettingsRoot, "wifi", Path.GetFileName(xml));
                await using var input = File.OpenRead(xml);
                var written = await sink.AddFileAsync(storePath, input, DateTime.UtcNow, ct);
                manifest.Files.Add(new ManifestFile { StorePath = storePath, Length = written.Length, LastWriteUtc = DateTime.UtcNow, Sha256 = written.Sha256 });
                manifest.WifiProfiles.Add(storePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"Wi-Fi export failed: {ex.Message}");
        }
        finally
        {
            // These files hold Wi-Fi passwords in plain text.
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Saved networks from a pulled drive, without their passwords (only the old PC
    /// could decrypt those). The new PC adds them and asks for each password once.
    /// </summary>
    private async Task CaptureOfflineWifiAsync(OfflinePaths offline, Func<string, string, DateTime, Task<bool>> addFile, Manifest manifest, Action<string> warn)
    {
        var folder = OfflineWifi.ProfilesFolder(offline);
        if (!Directory.Exists(folder)) return;
        log.Info("Reading saved Wi-Fi networks...");
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pcmigrate-wifi-" + Guid.NewGuid().ToString("N"))).FullName;
        var names = new List<string>();
        try
        {
            var n = 0;
            foreach (var xml in Directory.EnumerateFiles(folder, "*.xml", SearchOption.AllDirectories))
            {
                try
                {
                    var (content, name) = OfflineWifi.RemovePassword(await File.ReadAllTextAsync(xml));
                    var file = Path.Combine(temp, $"{++n}.xml");
                    await File.WriteAllTextAsync(file, content);
                    var storePath = StorePath.Combine(StorePath.SettingsRoot, "wifi", $"{n}.xml");
                    if (!await addFile(file, storePath, DateTime.UtcNow)) continue;
                    manifest.WifiProfiles.Add(storePath);
                    if (name is not null) names.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    warn($"Couldn't read Wi-Fi network {Path.GetFileName(xml)}: {ex.Message}");
                }
            }
            if (names.Count > 0)
                warn($"Wi-Fi passwords can't be read from a pulled drive. These networks will ask for their password once on the new PC: {string.Join(", ", names.Distinct())}");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>How many files and bytes a capture of this user would copy.</summary>
    public static (long Files, long Bytes) Estimate(UserProfileInfo user, CaptureRules rules, CancellationToken ct = default, OfflinePaths? offline = null)
    {
        long files = 0, bytes = 0;
        foreach (var folder in rules.ProfileFolders)
        {
            var path = Path.Combine(user.ProfilePath, folder.Replace('/', Path.DirectorySeparatorChar));
            if (offline is not null) path = offline.ToReadable(path);
            foreach (var f in FileCollector.Enumerate(path, folder, rules, _ => { }))
            {
                ct.ThrowIfCancellationRequested();
                files++;
                bytes += f.Length;
            }
        }
        return (files, bytes);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
