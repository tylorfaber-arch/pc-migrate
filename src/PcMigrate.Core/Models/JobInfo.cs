namespace PcMigrate.Core.Models;

/// <summary>Technician mode: who the job is for. Saved in the backup and printed on the client report.</summary>
public sealed class JobInfo
{
    public required string Client { get; init; }
    public string? JobNumber { get; init; }
    public string? Notes { get; init; }
}

/// <summary>The technician's own details, set once per PC the tool runs from (technician.json).</summary>
public sealed class TechnicianSettings
{
    public const string FileName = "technician.json";

    public bool Enabled { get; set; }
    /// <summary>The business name at the top of reports.</summary>
    public string? Company { get; set; }
    public string? Technician { get; set; }
    /// <summary>Phone, email or website, printed under the business name.</summary>
    public string? Contact { get; set; }
    /// <summary>Print license keys in full on client reports (otherwise just how many were saved).</summary>
    public bool ShowKeys { get; set; }
}
