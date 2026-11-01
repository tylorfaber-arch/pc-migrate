using PcMigrate.Core.Models;
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
}

/// <summary>Collects everything from the old PC and sends it to a sink (folder or network).</summary>
public sealed class CaptureEngine(ISystemProbe probe, IMigrationLog log)
{
    private const int MaxWarningsInManifest = 2000;

    public async Task<Manifest> RunAsync(IMigrationSink sink, CaptureOptions options, CancellationToken ct = default)
    {
        log.Info("Scanning this PC...");
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
            if (options.UseShadowCopy && OperatingSystem.IsWindows())
            {
                mapPath = CreateSnapshots(users, snapshots, Warn);
                manifest.UsedShadowCopy = snapshots.Count > 0;
            }

            long files = 0, bytes = 0;
            foreach (var user in users)
            {
                log.Info($"Copying files for {user.UserName}...");
                foreach (var folder in options.Rules.ProfileFolders)
                {
                    var source = mapPath(Path.Combine(user.ProfilePath, folder.Replace('/', Path.DirectorySeparatorChar)));
                    foreach (var file in FileCollector.Enumerate(source, folder, options.Rules, Warn))
                    {
                        ct.ThrowIfCancellationRequested();
                        var storePath = StorePath.Combine(StorePath.UsersRoot, user.UserName, file.RelativePath);
                        if (!StorePath.IsSafe(storePath))
                        {
                            Warn($"Skipped file with an unusual name: {file.FullPath}");
                            continue;
                        }
                        // Problems reading one file are warnings; a SinkWriteException (drive full,
                        // network dropped) is not caught here and stops the capture.
                        try
                        {
                            FileWriteResult written;
                            await using (var input = HashingCopy.OpenForRead(file.FullPath))
                                written = await sink.AddFileAsync(storePath, input, file.LastWriteUtc, ct);
                            manifest.Files.Add(new ManifestFile
                            {
                                StorePath = storePath,
                                Length = written.Length,
                                LastWriteUtc = file.LastWriteUtc,
                                Sha256 = written.Sha256,
                            });
                            files++;
                            bytes += written.Length;
                            log.FileProgress(files, bytes, storePath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Warn($"Could not copy {file.FullPath}: {ex.Message}");
                        }
                    }
                }
            }

            if (options.IncludeUserSettings && OperatingSystem.IsWindows())
                await CaptureUserSettingsAsync(users, sink, manifest, Warn, ct);
            if (options.IncludeWifi && OperatingSystem.IsWindows())
                await CaptureWifiAsync(sink, manifest, Warn, ct);

            log.Info($"Captured {files:N0} files ({FormatBytes(bytes)}). Writing manifest...");
            await sink.CompleteAsync(manifest, ct);
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
    private Func<string, string> CreateSnapshots(List<UserProfileInfo> users, Dictionary<string, IDisposable> snapshots, Action<string> warn)
    {
        foreach (var volume in users.Select(u => Path.GetPathRoot(u.ProfilePath)!).Distinct(StringComparer.OrdinalIgnoreCase))
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
            var captured = UserSettingsIO.Read(user, log);
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

    /// <summary>How many files and bytes a capture of this user would copy.</summary>
    public static (long Files, long Bytes) Estimate(UserProfileInfo user, CaptureRules rules, CancellationToken ct = default)
    {
        long files = 0, bytes = 0;
        foreach (var folder in rules.ProfileFolders)
        {
            var path = Path.Combine(user.ProfilePath, folder.Replace('/', Path.DirectorySeparatorChar));
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
