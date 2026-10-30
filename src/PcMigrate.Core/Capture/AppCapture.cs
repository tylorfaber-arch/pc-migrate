using PcMigrate.Core.Apps;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Core.Capture;

/// <summary>
/// The program part of a capture: registry settings for every moving program, and for
/// programs that are copied rather than reinstalled, their folder and shortcuts too.
/// (Settings folders in AppData are copied with each person's files.)
/// </summary>
internal sealed class AppCapture(IAppPlatform? platform, IMigrationLog log, Action<string> warn)
{
    public delegate Task<bool> AddFileFn(string fullPath, string storePath, DateTime lastWriteUtc);

    // Program folders are copied whole; only obvious junk is left out.
    private static readonly CaptureRules ProgramRules = new() { ExcludedFolderNames = [], ExcludedFilePatterns = ["*.tmp", "*.partial"] };

    public async Task RunAsync(IReadOnlyList<AppEntry> apps, List<UserProfileInfo> users, Manifest manifest,
        Func<string, string> mapPath, AddFileFn addFile, IMigrationSink sink, CancellationToken ct)
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pcmigrate-apps-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (var app in apps)
            {
                ct.ThrowIfCancellationRequested();
                if (app.Method == AppMethod.Manual) { manifest.Apps.Add(app); continue; }
                log.Info($"Saving {app.Name}...");
                try
                {
                    await CaptureRegistryAsync(app, users, temp, addFile);
                    if (app.Method == AppMethod.Copy) await CaptureProgramAsync(app, temp, mapPath, addFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    warn($"Could not fully save {app.Name}: {ex.Message}");
                }
                manifest.Apps.Add(app);
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    private async Task CaptureRegistryAsync(AppEntry app, List<UserProfileInfo> users, string temp, AddFileFn addFile)
    {
        if (platform is null) return;
        var n = 0;
        var owners = app.Scope == "Machine" ? users : users.Where(u => u.UserName.Equals(app.Scope, StringComparison.OrdinalIgnoreCase));
        foreach (var user in owners)
        {
            foreach (var key in app.UserRegistryKeys)
            {
                var file = Path.Combine(temp, $"{app.Key}-{++n}.reg");
                var root = platform.ExportUserKey(user, key, file);
                if (root is null) continue;
                var storePath = StorePath.Combine(StorePath.SettingsRoot, "apps", app.Key, $"user-{user.UserName}-{n}.reg");
                if (await addFile(file, storePath, DateTime.UtcNow))
                    app.RegistryFiles.Add(new RegistryFile { StorePath = storePath, UserName = user.UserName, ExportedRoot = root });
            }
        }

        if (app.Method != AppMethod.Copy) return;
        // The Uninstall entry makes it show up in Apps & features on the new PC.
        IEnumerable<string> machineKeys = app.Scope == "Machine"
            ? app.MachineRegistryKeys.SelectMany(k => new[] { $@"HKLM\SOFTWARE\{k}", $@"HKLM\SOFTWARE\WOW6432Node\{k}" })
            : [];
        foreach (var key in machineKeys.Prepend(app.UninstallKey))
        {
            if (key.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase)) continue; // per-user entries are restored for the user below
            var file = Path.Combine(temp, $"{app.Key}-{++n}.reg");
            if (!platform.ExportKey(key, file)) continue;
            var storePath = StorePath.Combine(StorePath.SettingsRoot, "apps", app.Key, $"machine-{n}.reg");
            if (await addFile(file, storePath, DateTime.UtcNow))
                app.RegistryFiles.Add(new RegistryFile { StorePath = storePath });
        }
    }

    private async Task CaptureProgramAsync(AppEntry app, string temp, Func<string, string> mapPath, AddFileFn addFile)
    {
        var location = app.InstallLocation!.Trim().Trim('"').TrimEnd('\\', '/');
        var root = StorePath.Combine("files", "apps", app.Key, "program");
        var copied = 0;
        foreach (var file in FileCollector.Enumerate(mapPath(location), "", ProgramRules, warn))
        {
            if (await addFile(file.FullPath, StorePath.Combine(root, file.RelativePath), file.LastWriteUtc)) copied++;
        }
        if (copied == 0) throw new InvalidOperationException("its program folder was empty or unreadable");
        app.ProgramStorePath = root;

        if (platform is null) return;
        var n = 0;
        foreach (var folder in platform.SharedShortcutFolders())
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var lnk in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
            {
                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(lnk); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (!Shortcuts.PointsInto(bytes, location)) continue;
                var storePath = StorePath.Combine(StorePath.SettingsRoot, "apps", app.Key, "shortcuts", $"{++n}.lnk");
                if (await addFile(lnk, storePath, File.GetLastWriteTimeUtc(lnk)))
                    app.Shortcuts.Add(new ShortcutFile { StorePath = storePath, TargetPath = lnk });
            }
        }
    }
}
