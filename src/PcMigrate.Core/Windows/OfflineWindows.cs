using System.Runtime.Versioning;
using Microsoft.Win32;
using PcMigrate.Core.Offline;

namespace PcMigrate.Core.Windows;

/// <summary>A drive, plugged in here, that holds another PC's Windows.</summary>
public sealed record PulledDrive(string Root, string? Label, long TotalBytes, long FreeBytes, bool Locked, string? ComputerName, IReadOnlyList<string> Users);

/// <summary>
/// The Windows on a drive pulled from a dead PC. Its two main registry files (SOFTWARE for
/// programs and accounts, SYSTEM for the PC name and services) are copied off the drive and
/// the copies loaded, so the drive is only ever read. Unloaded and deleted on Dispose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfflineWindows : IDisposable
{
    private readonly string _softwareMount;
    private readonly string _systemMount;
    private readonly string _temp;
    private readonly Dictionary<string, UserHive?> _users = new(StringComparer.OrdinalIgnoreCase);

    private OfflineWindows(string root, string softwareMount, string systemMount, string temp)
    {
        _softwareMount = softwareMount;
        _systemMount = systemMount;
        _temp = temp;
        Software = Registry.LocalMachine.OpenSubKey(softwareMount) ?? throw new IOException("The drive's registry didn't open.");
        SystemHive = Registry.LocalMachine.OpenSubKey(systemMount) ?? throw new IOException("The drive's registry didn't open.");

        using var nt = Software.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion");
        var systemRoot = nt?.GetValue("SystemRoot") as string ?? @"C:\Windows";
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cv = Software.OpenSubKey(@"Microsoft\Windows\CurrentVersion"))
        {
            if (cv?.GetValue("ProgramFilesDir") is string pf) vars["ProgramFiles"] = vars["ProgramW6432"] = pf;
            if (cv?.GetValue("ProgramFilesDir (x86)") is string pf86) vars["ProgramFiles(x86)"] = pf86;
            if (cv?.GetValue("CommonFilesDir") is string cf) vars["CommonProgramFiles"] = cf;
        }
        using (var profiles = Software.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion\ProfileList"))
        {
            if (profiles?.GetValue("ProgramData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string pd) vars["ProgramData"] = vars["ALLUSERSPROFILE"] = pd;
            if (profiles?.GetValue("Public", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string pub) vars["PUBLIC"] = pub;
        }
        Paths = new OfflinePaths(root, systemRoot[..2], vars);

        var build = nt?.GetValue("CurrentBuild") as string;
        var product = nt?.GetValue("ProductName") as string ?? "Windows";
        // Windows 11 still calls itself "Windows 10" here; the build number tells them apart.
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        OsDescription = $"{product} {nt?.GetValue("DisplayVersion") as string} (build {build})".Replace("  ", " ");

        using var select = SystemHive.OpenSubKey("Select");
        var current = select?.GetValue("Current") is int c ? c : 1;
        ControlSet = $"ControlSet{current:000}";
        using var name = SystemHive.OpenSubKey($@"{ControlSet}\Control\ComputerName\ComputerName");
        ComputerName = name?.GetValue("ComputerName") as string ?? "Old PC";
    }

    public OfflinePaths Paths { get; }
    /// <summary>The old PC's HKLM\SOFTWARE.</summary>
    public RegistryKey Software { get; }
    /// <summary>The old PC's HKLM\SYSTEM.</summary>
    public RegistryKey SystemHive { get; }
    /// <summary>"ControlSet001": what HKLM\SYSTEM\CurrentControlSet pointed to on the old PC.</summary>
    public string ControlSet { get; }
    public string ComputerName { get; }
    public string OsDescription { get; }
    /// <summary>Where the old HKLM\SOFTWARE is loaded right now, as reg.exe writes it.</summary>
    public string SoftwareRegPath => $@"HKLM\{_softwareMount}";

    /// <summary>
    /// A person's registry from the drive, loaded once and kept until Dispose: finding and
    /// exporting program settings reads it many times, and each load copies the whole file.
    /// </summary>
    internal UserHive? User(Models.UserProfileInfo user, IMigrationLog log)
    {
        lock (_users)
        {
            if (!_users.TryGetValue(user.Sid, out var hive))
                _users[user.Sid] = hive = UserHive.Open(user, writable: false, log, Paths);
            return hive;
        }
    }

    public static bool HasWindows(string root) => File.Exists(Path.Combine(root, "Windows", "System32", "config", "SOFTWARE"));

    /// <summary>Every plugged-in drive (other than this PC's own) that has Windows on it, or is locked.</summary>
    public static List<PulledDrive> Find()
    {
        var own = Path.GetPathRoot(Environment.SystemDirectory);
        var result = new List<PulledDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.Name;
            if (string.Equals(root, own, StringComparison.OrdinalIgnoreCase)) continue;
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            if (!drive.IsReady)
            {
                if (BitLocker.IsLocked(root)) result.Add(new PulledDrive(root, null, 0, 0, Locked: true, null, []));
                continue;
            }
            if (!HasWindows(root)) continue;
            string? label = null;
            long total = 0, free = 0;
            try { label = drive.VolumeLabel; total = drive.TotalSize; free = drive.TotalFreeSpace; } catch (IOException) { }
            result.Add(new PulledDrive(root, string.IsNullOrWhiteSpace(label) ? null : label, total, free, Locked: false, ReadComputerName(root), ProfileFolders(root)));
        }
        return result;
    }

    public static OfflineWindows Open(string root)
    {
        if (!HasWindows(root)) throw new InvalidOperationException($"{root} doesn't have Windows on it (no Windows\\System32\\config folder).");
        BackupFile.Enable();
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pcmigrate-drive-" + Guid.NewGuid().ToString("N"))).FullName;
        string? software = null, system = null;
        try
        {
            var config = Path.Combine(root, "Windows", "System32", "config");
            software = Load(HiveFiles.CopyTo(Path.Combine(config, "SOFTWARE"), temp));
            system = Load(HiveFiles.CopyTo(Path.Combine(config, "SYSTEM"), temp));
            return new OfflineWindows(root, software, system, temp);
        }
        catch
        {
            if (software is not null) UserHive.Unload($@"HKLM\{software}");
            if (system is not null) UserHive.Unload($@"HKLM\{system}");
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    private static string Load(string file)
    {
        var mount = "PCMIGRATE_" + Path.GetFileName(file) + "_" + Guid.NewGuid().ToString("N")[..8];
        var result = ProcessRunner.Run("reg.exe", "load", $@"HKLM\{mount}", file);
        if (result.ExitCode != 0)
            throw new IOException($"Couldn't read the drive's registry ({Path.GetFileName(file)}). The drive may be damaged. {result.Output}");
        return mount;
    }

    /// <summary>The PC's name, from a copy of just its SYSTEM file (SOFTWARE is much bigger and not needed for a list).</summary>
    private static string? ReadComputerName(string root)
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pcmigrate-name-" + Guid.NewGuid().ToString("N"))).FullName;
        string? mount = null;
        try
        {
            BackupFile.Enable();
            mount = Load(HiveFiles.CopyTo(Path.Combine(root, "Windows", "System32", "config", "SYSTEM"), temp));
            using var system = Registry.LocalMachine.OpenSubKey(mount);
            using var select = system?.OpenSubKey("Select");
            var current = select?.GetValue("Current") is int c ? c : 1;
            using var name = system?.OpenSubKey($@"ControlSet{current:000}\Control\ComputerName\ComputerName");
            return name?.GetValue("ComputerName") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (mount is not null) UserHive.Unload($@"HKLM\{mount}");
            try { Directory.Delete(temp, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static List<string> ProfileFolders(string root)
    {
        string[] skip = ["Public", "Default", "Default User", "All Users", "defaultuser0", "WDAGUtilityAccount"];
        try
        {
            return Directory.GetDirectories(Path.Combine(root, "Users"))
                .Where(d => File.Exists(Path.Combine(d, "NTUSER.DAT")))
                .Select(Path.GetFileName)
                .Where(n => n is not null && !skip.Contains(n, StringComparer.OrdinalIgnoreCase))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        lock (_users)
        {
            foreach (var hive in _users.Values) hive?.Dispose();
            _users.Clear();
        }
        Software.Dispose();
        SystemHive.Dispose();
        UserHive.Unload($@"HKLM\{_softwareMount}");
        UserHive.Unload($@"HKLM\{_systemMount}");
        try { Directory.Delete(_temp, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
