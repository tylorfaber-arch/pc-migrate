namespace PcMigrate.Core.Health;

public enum HealthLevel { Good, Warning, Failing, Unknown }

/// <summary>What a drive says about itself (SMART and Windows' reliability counters). Null means it didn't say.</summary>
public sealed record HealthReadings
{
    /// <summary>Windows' own verdict: 0 healthy, 1 warning, 2 unhealthy, 5 unknown.</summary>
    public int? WindowsHealth { get; init; }
    /// <summary>Windows' operational status codes; 5 means the drive predicts its own failure.</summary>
    public IReadOnlyList<int> OperationalStatus { get; init; } = [];
    public bool IsSsd { get; init; }
    public int? TemperatureC { get; init; }
    /// <summary>How much of an SSD's rated life is used, in percent.</summary>
    public int? WearPercent { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsUncorrected { get; init; }
    public long? PowerOnHours { get; init; }
}

public sealed record DriveHealthReport(int DiskNumber, string Model, HealthLevel Level, string Summary, IReadOnlyList<string> Details);

/// <summary>Turns a drive's readings into a plain verdict and advice.</summary>
public static class DriveHealth
{
    public static (HealthLevel Level, string Summary, List<string> Details) Assess(HealthReadings r)
    {
        var failing = new List<string>();
        var warnings = new List<string>();
        var info = new List<string>();

        if (r.OperationalStatus.Contains(5)) failing.Add("The drive's own self-test predicts it will fail soon.");
        if (r.WindowsHealth == 2) failing.Add("Windows reports the drive as unhealthy.");
        if (r.ReadErrorsUncorrected is > 0) failing.Add($"{r.ReadErrorsUncorrected:N0} read error(s) the drive couldn't correct: some data may already be unreadable.");
        if (r.WriteErrorsUncorrected is > 0) failing.Add($"{r.WriteErrorsUncorrected:N0} write error(s) the drive couldn't correct.");
        if (r.WearPercent is >= 100) failing.Add("The SSD has used up its rated life.");

        if (r.WindowsHealth == 1) warnings.Add("Windows reports a warning for this drive.");
        if (r.OperationalStatus.Contains(3)) warnings.Add("Windows reports the drive as degraded.");
        if (r.WearPercent is >= 90 and < 100) warnings.Add($"The SSD has used {r.WearPercent}% of its rated life.");
        var hot = r.IsSsd ? 70 : 55;
        if (r.TemperatureC is { } t && t >= hot) warnings.Add($"It's running hot ({t} °C). Give it air and avoid long copies in an enclosed caddy.");

        if (r.TemperatureC is { } temp && temp < hot) info.Add($"{temp} °C");
        if (r.WearPercent is { } wear && wear < 90) info.Add($"{wear}% of rated life used");
        if (r.PowerOnHours is { } hours)
        {
            var years = hours / 8760.0;
            info.Add($"{hours:N0} hours powered on (about {years:0.#} year{(years < 1.05 ? "" : "s")})");
        }

        var known = r.WindowsHealth is 0 or 1 or 2 || r.OperationalStatus.Count > 0 || r.TemperatureC is not null || r.WearPercent is not null || r.ReadErrorsUncorrected is not null;
        if (failing.Count > 0)
            return (HealthLevel.Failing, "This drive is failing. Copy what matters first and don't run anything else on it.", [.. failing, .. warnings, .. info]);
        if (warnings.Count > 0)
            return (HealthLevel.Warning, "This drive has warning signs. Copy from it soon.", [.. warnings, .. info]);
        if (!known)
            return (HealthLevel.Unknown, "The drive's health can't be read, usually because the USB adapter doesn't pass it through.", info);
        return (HealthLevel.Good, "No problems reported.", info);
    }
}
