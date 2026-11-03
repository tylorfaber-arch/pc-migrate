using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PcMigrate.Core.Models;
using PcMigrate.Core.Schedule;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Sets up and keeps track of the scheduled backup: a Task Scheduler entry that runs
/// "pcmigrate-cli backup --scheduled" as SYSTEM, a copy of pcmigrate-cli.exe that stays put,
/// and schedule.json with the settings and the last result. Only SYSTEM and administrators
/// can read the settings, and the password in them is locked to this PC.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class BackupScheduler
{
    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pc-migrate");
    public static string SettingsPath => Path.Combine(Home, ScheduleSettings.FileName);
    public static string CliPath => Path.Combine(Home, Update.CliTool.FileName);
    public static string LogPath => Path.Combine(Home, "backup.log");

    public static ScheduleSettings? Load()
    {
        try { return File.Exists(SettingsPath) ? Json.Deserialize<ScheduleSettings>(File.ReadAllText(SettingsPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { return null; }
    }

    public static void SaveSettings(ScheduleSettings settings)
    {
        Directory.CreateDirectory(Home);
        var temp = SettingsPath + ".partial";
        File.WriteAllText(temp, Json.Serialize(settings));
        LockDown(temp);
        File.Move(temp, SettingsPath, overwrite: true);
    }

    /// <summary>Turns the schedule on (or changes it). <paramref name="cliSource"/> is copied to a fixed place for the task to run.</summary>
    public static void Enable(ScheduleSettings settings, string? password, string cliSource)
    {
        if (settings.Problem() is { } problem) throw new InvalidOperationException(problem);
        CheckExistingBackup(settings.Folder, password);
        Directory.CreateDirectory(Home);
        if (!string.Equals(Path.GetFullPath(cliSource), CliPath, StringComparison.OrdinalIgnoreCase))
            File.Copy(cliSource, CliPath, overwrite: true);
        settings.ProtectedPassword = password is null ? null : Protect(password);
        SaveSettings(settings);

        var xml = Path.Combine(Path.GetTempPath(), "pcmigrate-task-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(xml, ScheduleTask.Xml(settings, CliPath, LogPath, DateTime.Today), System.Text.Encoding.Unicode);
        try
        {
            var result = ProcessRunner.Run("schtasks.exe", "/Create", "/TN", ScheduleSettings.TaskName, "/XML", xml, "/F");
            if (result.ExitCode != 0) throw new IOException($"Windows' Task Scheduler refused the backup: {result.Output}");
        }
        finally
        {
            File.Delete(xml);
        }
    }

    /// <summary>A folder that already holds a backup has to be opened the same way each time.</summary>
    private static void CheckExistingBackup(string folder, string? password)
    {
        if (!Directory.Exists(folder) || !Storage.FolderSink.HasStore(folder)) return;
        var isProtected = Storage.FolderSink.IsProtected(folder);
        if (!isProtected && password is not null)
            throw new InvalidOperationException("That folder already has a backup without a password. Pick an empty folder for a password-protected one.");
        try { Storage.FolderSink.ReadManifest(folder, password); }
        catch (Storage.StoreLockedException)
        {
            throw new InvalidOperationException("That folder already has a password-protected backup, and this isn't its password. Use its password, or pick an empty folder.");
        }
    }

    public static void Disable()
    {
        ProcessRunner.Run("schtasks.exe", "/Delete", "/TN", ScheduleSettings.TaskName, "/F");
        try { File.Delete(SettingsPath); } catch (IOException) { }
    }

    /// <summary>Starts the scheduled backup now, in the background.</summary>
    public static void RunNow()
    {
        var result = ProcessRunner.Run("schtasks.exe", "/Run", "/TN", ScheduleSettings.TaskName);
        if (result.ExitCode != 0) throw new IOException($"Couldn't start the backup: {result.Output}");
    }

    public static bool IsRunning() =>
        ProcessRunner.Run("schtasks.exe", "/Query", "/TN", ScheduleSettings.TaskName, "/FO", "CSV", "/NH").Output.Contains("\"Running\"", StringComparison.OrdinalIgnoreCase);

    public static string? Password(ScheduleSettings settings) => settings.ProtectedPassword is { } p ? Unprotect(p) : null;

    /// <summary>Only SYSTEM and administrators may read the settings file.</summary>
    private static void LockDown(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    // ----- Windows DPAPI, machine scope: only this PC can unlock it -----

    private const int LocalMachine = 0x4, NoUi = 0x1;

    private static string Protect(string text)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(Crypt(plain, protect: true));
    }

    private static string Unprotect(string base64) =>
        System.Text.Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(base64), protect: false));

    private static unsafe byte[] Crypt(byte[] data, bool protect)
    {
        fixed (byte* p = data)
        {
            var input = new DataBlob { Size = data.Length, Data = (IntPtr)p };
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, LocalMachine | NoUi, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoUi, out output);
            if (!ok) throw new IOException(protect ? "Couldn't lock the backup password." : "Couldn't unlock the saved backup password. Set the schedule up again.");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr handle);
}
