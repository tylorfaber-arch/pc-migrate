namespace PcMigrate.Core.Update;

/// <summary>Finds pcmigrate-cli.exe for jobs that run without the app (rescue USB, scheduled backups).</summary>
public static class CliTool
{
    public const string FileName = "pcmigrate-cli.exe";

    /// <summary>The running exe if it is the command-line tool, the one beside it, or else a fresh download of the newest.</summary>
    public static async Task<string> FindOrDownloadAsync(string runningExe, string downloadFolder, IMigrationLog log, CancellationToken ct)
    {
        if (Path.GetFileName(runningExe).Equals(FileName, StringComparison.OrdinalIgnoreCase)) return runningExe;
        var beside = Path.Combine(Path.GetDirectoryName(runningExe)!, FileName);
        if (File.Exists(beside)) return beside;

        log.Info($"Downloading {FileName} from the newest release...");
        var release = await Updater.CheckAsync(FileName, new Version(0, 0, 0, 0), ct)
            ?? throw new InvalidOperationException($"Couldn't download {FileName} (no internet?). Put it in the same folder as pc-migrate.exe and try again.");
        Directory.CreateDirectory(downloadFolder);
        var target = Path.Combine(downloadFolder, FileName);
        var downloaded = await Updater.DownloadAsync(release, target, null, ct);
        File.Move(downloaded, target, overwrite: true);
        return target;
    }
}
