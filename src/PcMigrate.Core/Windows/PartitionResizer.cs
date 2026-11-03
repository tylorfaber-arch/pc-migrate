using System.Runtime.Versioning;
using System.Text.Json;
using PcMigrate.Core.Clone;

namespace PcMigrate.Core.Windows;

/// <summary>
/// Grows and shrinks partitions with Windows' own resize (files are kept; when shrinking, Windows
/// moves them out of the way first). Can also move the recovery partition to the end of the drive,
/// following Microsoft's documented steps, when it's what stops the main partition growing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PartitionResizer(IMigrationLog log)
{
    public static ResizeOption Options(DiskInfo disk, PartitionInfo part)
    {
        var (min, max) = SupportedSize(disk.Number, part.Number);
        return ResizePlanner.Plan(disk, part, min, max);
    }

    public static (long Min, long Max) SupportedSize(int disk, int partition)
    {
        var json = PowerShell($"Get-PartitionSupportedSize -DiskNumber {disk} -PartitionNumber {partition} | Select-Object SizeMin,SizeMax | ConvertTo-Json -Compress");
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("SizeMin").GetInt64(), doc.RootElement.GetProperty("SizeMax").GetInt64());
    }

    /// <summary>Why the recovery partition can't be moved on this drive, or null if it can.</summary>
    public static string? CheckMoveRecovery(DiskInfo disk, PartitionInfo part)
    {
        var windows = Path.GetPathRoot(Environment.SystemDirectory)?[..1];
        if (!disk.IsSystem || !string.Equals(part.DriveLetter, windows, StringComparison.OrdinalIgnoreCase))
            return "The recovery partition can only be moved on the drive this PC's Windows is running from.";
        return null;
    }

    public async Task<long> ResizeAsync(int diskNumber, int partitionNumber, long newSize, bool moveRecovery, CancellationToken ct)
    {
        var disk = Disks.List().FirstOrDefault(d => d.Number == diskNumber) ?? throw new InvalidOperationException("That drive isn't there any more.");
        var part = disk.Partitions.FirstOrDefault(p => p.Number == partitionNumber) ?? throw new InvalidOperationException("That partition isn't there any more.");
        if (!ResizePlanner.Resizable(part)) throw new InvalidOperationException($"{ResizePlanner.Label(part)} can't be resized here.");
        var option = Options(disk, part);
        newSize = ResizePlanner.AlignDown(newSize);
        if (moveRecovery && !option.CanMoveRecovery) moveRecovery = false;
        if (ResizePlanner.Check(option, newSize, moveRecovery) is { } problem) throw new InvalidOperationException(problem);

        if (!moveRecovery)
        {
            log.Info($"{(newSize > part.Length ? "Growing" : "Shrinking")} {ResizePlanner.Label(part)} from {DiskInfo.FormatSize(part.Length)} to {DiskInfo.FormatSize(newSize)}...");
            await Task.Run(() => PowerShell($"Resize-Partition -DiskNumber {diskNumber} -PartitionNumber {partitionNumber} -Size {newSize}"), ct);
            return newSize;
        }

        if (CheckMoveRecovery(disk, part) is { } why) throw new InvalidOperationException(why);
        var recovery = option.Recovery!;
        var recoveryWasOn = ReagentcEnabled();
        log.Info("Turning off the recovery environment for a moment (its files are kept in Windows)...");
        if (recoveryWasOn) Reagentc("/disable");
        var deleted = false;
        try
        {
            log.Info($"Removing the old recovery partition ({DiskInfo.FormatSize(recovery.Length)})...");
            Diskpart.Run($"select disk {diskNumber}", $"select partition {recovery.Number}", "delete partition override");
            deleted = true;
            log.Info($"Growing {ResizePlanner.Label(part)} to {DiskInfo.FormatSize(newSize)}...");
            await Task.Run(() => PowerShell($"Resize-Partition -DiskNumber {diskNumber} -PartitionNumber {partitionNumber} -Size {newSize}"), ct);
        }
        finally
        {
            if (deleted)
            {
                // Whatever happened above, the recovery partition is put back at the end.
                log.Info("Putting the recovery partition back at the end of the drive...");
                CreateRecovery(disk, option.RecoveryNewOffset, recovery.Length);
            }
            if (recoveryWasOn)
            {
                try { Reagentc("/enable"); log.Info("Recovery environment turned back on."); }
                catch (IOException ex) { log.Warn($"Couldn't turn the recovery environment back on ({ex.Message}). Run 'reagentc /enable' as administrator."); }
            }
        }
        return newSize;
    }

    private static void CreateRecovery(DiskInfo disk, long offset, long length)
    {
        var commands = new List<string>
        {
            $"select disk {disk.Number}",
            $"create partition primary offset={offset / 1024} size={length / ResizePlanner.Mb}",
            "format quick fs=ntfs label=\"Windows RE tools\"",
        };
        if (disk.IsGpt) commands.AddRange([$"set id={KnownPartitions.Recovery}", "gpt attributes=0x8000000000000001"]);
        else commands.Add("set id=27");
        Diskpart.Run([.. commands]);
    }

    private static bool ReagentcEnabled()
    {
        var info = ProcessRunner.Run("reagentc.exe", "/info");
        return info.Output.Contains("Enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void Reagentc(string arg)
    {
        var r = ProcessRunner.Run("reagentc.exe", arg);
        if (r.ExitCode != 0) throw new IOException(r.Output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"reagentc {arg} failed");
    }

    private static string PowerShell(string command)
    {
        var r = ProcessRunner.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; " + command);
        if (r.ExitCode != 0)
            throw new IOException("Windows couldn't do it: " + string.Join(" ", r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("At line", StringComparison.Ordinal) && !l.StartsWith("+", StringComparison.Ordinal)).Take(2)));
        return r.Output;
    }
}
