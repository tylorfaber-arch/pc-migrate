using System.Globalization;
using System.Security;

namespace PcMigrate.Core.Schedule;

/// <summary>What a scheduled backup does and when; saved as schedule.json in ProgramData\pc-migrate.</summary>
public sealed class ScheduleSettings
{
    public const string FileName = "schedule.json";
    public const string TaskName = "pc-migrate backup";

    /// <summary>The backup folder, e.g. E:\Backups\DESKTOP-ABC.</summary>
    public required string Folder { get; set; }
    /// <summary>"daily" or "weekly".</summary>
    public string Frequency { get; set; } = "daily";
    /// <summary>24-hour time, "21:00".</summary>
    public string Time { get; set; } = "21:00";
    /// <summary>For weekly: Monday, Tuesday, ...</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    /// <summary>The backup password, locked so only this PC can read it (Windows DPAPI). Null for a plain backup.</summary>
    public string? ProtectedPassword { get; set; }

    public DateTime? LastRun { get; set; }
    public bool? LastOk { get; set; }
    public string? LastResult { get; set; }

    /// <summary>"Every day at 21:00" / "Every Monday at 21:00".</summary>
    public string Describe() => Frequency == "weekly" ? $"Every {Day} at {Time}" : $"Every day at {Time}";

    /// <summary>Why these settings won't work, or null.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Folder)) return "Pick a folder to back up to.";
        if (Folder.StartsWith(@"\\", StringComparison.Ordinal))
            return "Scheduled backups run in the background without your sign-in, so they can't reach network folders. Use a drive plugged into this PC.";
        if (Frequency is not ("daily" or "weekly")) return "Pick daily or weekly.";
        if (!TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "Type the time as HH:mm, for example 21:00.";
        return null;
    }
}

/// <summary>The Windows Task Scheduler entry, as the XML schtasks takes. Separate so it can be checked anywhere.</summary>
public static class ScheduleTask
{
    /// <summary>
    /// Runs as SYSTEM (so every profile and a shadow copy can be read, with nobody signed in),
    /// starts as soon as possible after a missed time (the PC was off), and works on battery.
    /// </summary>
    public static string Xml(ScheduleSettings settings, string cliPath, string logPath, DateTime today)
    {
        var time = TimeOnly.ParseExact(settings.Time, "HH:mm", CultureInfo.InvariantCulture);
        var start = today.Date.Add(time.ToTimeSpan()).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var trigger = settings.Frequency == "weekly"
            ? $"<ScheduleByWeek><DaysOfWeek><{settings.Day} /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>"
            : "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Brings the pc-migrate backup in {SecurityElement.Escape(settings.Folder)} up to date.</Description></RegistrationInfo>
              <Triggers><CalendarTrigger><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled>{trigger}</CalendarTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT12H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(cliPath)}</Command><Arguments>backup --scheduled --log "{SecurityElement.Escape(logPath)}"</Arguments></Exec></Actions>
            </Task>
            """;
    }
}
