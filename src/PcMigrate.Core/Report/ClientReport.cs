using System.Net;
using System.Text;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;

namespace PcMigrate.Core.Report;

/// <summary>
/// The report a technician hands the client: one self-contained web page that prints cleanly
/// (or saves as PDF from the browser's print dialog). It says what was saved or moved, what's
/// still to do, and anything that couldn't be copied, in plain words.
/// </summary>
public static class ClientReport
{
    public sealed record Input
    {
        public required Manifest Manifest { get; init; }
        /// <summary>Set for a finished move to a new PC; null for a backup or a rescue that only saved the files.</summary>
        public ApplyResult? Applied { get; init; }
        public TechnicianSettings? Technician { get; init; }
        /// <summary>The job, when it differs from (or wasn't saved in) the backup.</summary>
        public JobInfo? Job { get; init; }
        /// <summary>The new PC's name, for a move.</summary>
        public string? NewComputer { get; init; }
        /// <summary>Where the backup was saved, for a backup.</summary>
        public string? SavedTo { get; init; }
        public DateTime Now { get; init; } = DateTime.Now;
    }

    public static string FileName(Input input)
    {
        var client = (input.Job ?? input.Manifest.Job)?.Client;
        var safe = client is null ? "" : new string(client.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        return $"client-report{(safe.Length > 0 ? " " + safe : "")} {input.Now:yyyy-MM-dd HHmm}.html";
    }

    public static string Build(Input input)
    {
        var m = input.Manifest;
        var job = input.Job ?? m.Job;
        var tech = input.Technician;
        var applied = input.Applied;
        var sb = new StringBuilder();
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");

        var title = applied is null ? "Data backup report" : "PC move report";
        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(title)}}{{(job is null ? "" : " - " + E(job.Client))}}</title>
            <style>
            :root { --text:#16181d; --muted:#5d6472; --line:#e3e6ec; --soft:#f5f6f9; --ok:#1b7f4b; --warn:#9a5b00; --bad:#b42318; --accent:#3157d5; }
            * { box-sizing: border-box; }
            body { margin: 0; background: #fff; color: var(--text); font: 14px/1.5 "Segoe UI", system-ui, sans-serif; }
            main { max-width: 820px; margin: 0 auto; padding: 32px 24px 48px; }
            header { display: flex; justify-content: space-between; gap: 16px; align-items: flex-start; border-bottom: 2px solid var(--text); padding-bottom: 14px; }
            h1 { font-size: 24px; margin: 0 0 2px; }
            h2 { font-size: 15px; margin: 26px 0 8px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); }
            .co { text-align: right; font-size: 13px; color: var(--muted); }
            .co b { display: block; font-size: 16px; color: var(--text); }
            .meta { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 8px 18px; margin-top: 16px; }
            .meta div span { display: block; font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: .04em; }
            .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; }
            .tile { border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; }
            .tile b { display: block; font-size: 20px; }
            .tile span { font-size: 12px; color: var(--muted); }
            table { width: 100%; border-collapse: collapse; }
            th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
            th { font-size: 12px; color: var(--muted); font-weight: 600; }
            td.n { text-align: right; white-space: nowrap; }
            .tag { font-size: 11px; font-weight: 600; padding: 1px 7px; border-radius: 99px; white-space: nowrap; }
            .ok { background: #e3f5ea; color: var(--ok); } .warn { background: #fdf0d9; color: var(--warn); } .bad { background: #fde8e8; color: var(--bad); }
            .note { background: var(--soft); border-radius: 10px; padding: 10px 14px; }
            ul { margin: 6px 0; padding-left: 20px; }
            .key { font-family: Consolas, ui-monospace, monospace; }
            .sign { display: grid; grid-template-columns: 1fr 1fr; gap: 28px; margin-top: 40px; }
            .sign div { border-top: 1px solid var(--text); padding-top: 4px; font-size: 12px; color: var(--muted); }
            footer { margin-top: 28px; font-size: 11px; color: var(--muted); }
            @media print { main { padding: 0; } h2 { break-after: avoid; } tr, .tile { break-inside: avoid; } .noprint { display: none; } }
            </style></head><body><main>
            """);

        sb.Append("<header><div>");
        sb.Append($"<h1>{E(title)}</h1><div>{E(input.Now.ToString("dddd d MMMM yyyy, HH:mm"))}</div>");
        sb.Append("</div>");
        if (tech is not null && (tech.Company ?? tech.Technician) is not null)
            sb.Append($"<div class=\"co\"><b>{E(tech.Company ?? tech.Technician)}</b>{(tech.Company is not null && tech.Technician is not null ? E(tech.Technician) + "<br>" : "")}{E(tech.Contact)}</div>");
        sb.Append("</header>");

        sb.Append("<div class=\"meta\">");
        if (job is not null)
        {
            Meta(sb, "Client", job.Client);
            if (job.JobNumber is not null) Meta(sb, "Job number", job.JobNumber);
        }
        Meta(sb, applied is null ? "Computer" : "Old computer", $"{m.Inventory.Machine.ComputerName}");
        if (applied is not null) Meta(sb, "New computer", input.NewComputer ?? "");
        else if (input.SavedTo is not null) Meta(sb, "Saved to", input.SavedTo);
        if (m.Inventory.Machine.OsDescription is { Length: > 0 } os) Meta(sb, "Windows", os);
        sb.Append("</div>");
        if (job?.Notes is { Length: > 0 } notes) sb.Append($"<p class=\"note\">{E(notes)}</p>");

        // Summary
        var users = UserTotals(m);
        long files = applied?.FilesCopied ?? m.Files.Count;
        long bytes = applied?.BytesCopied ?? m.Files.Sum(f => f.Length);
        var installed = applied?.Apps.Count(a => a.Outcome is AppOutcome.Installed or AppOutcome.Copied or AppOutcome.AlreadyInstalled) ?? 0;
        var toDo = applied is null ? m.Apps.Count(a => a.Method == AppMethod.Manual) : applied.AppsToReinstall.Count;
        sb.Append("<h2>Summary</h2><div class=\"tiles\">");
        Tile(sb, files.ToString("N0"), applied is null ? "files saved" : "files moved");
        Tile(sb, CaptureEngine.FormatBytes(bytes), applied is null ? "of data saved" : "of data copied");
        if (applied is not null) Tile(sb, installed.ToString("N0"), "programs set up");
        Tile(sb, toDo.ToString("N0"), "programs to install by hand");
        sb.Append("</div>");

        // People
        if (users.Count > 0)
        {
            sb.Append("<h2>People's files</h2><table><tr><th>Account</th><th>Folders</th><th class=\"n\">Files</th><th class=\"n\">Size</th></tr>");
            foreach (var u in users)
                sb.Append($"<tr><td>{E(u.Name)}</td><td>{E(string.Join(", ", u.Folders.Take(8)))}{(u.Folders.Count > 8 ? ", …" : "")}</td><td class=\"n\">{u.Files:N0}</td><td class=\"n\">{CaptureEngine.FormatBytes(u.Bytes)}</td></tr>");
            sb.Append("</table>");
        }

        var extras = new List<string>();
        if (m.WifiProfiles.Count > 0) extras.Add($"{m.WifiProfiles.Count} saved Wi-Fi network(s)");
        if (m.UserSettings.Any(s => s.WallpaperStorePath is not null)) extras.Add("desktop wallpaper");
        if (m.UserSettings.Sum(s => s.MappedDrives.Count) is > 0 and var drives) extras.Add($"{drives} mapped network drive(s)");
        if (m.Files.Any(f => f.StorePath.Contains("/Google/Chrome/", StringComparison.OrdinalIgnoreCase) || f.StorePath.Contains("/Microsoft/Edge/", StringComparison.OrdinalIgnoreCase) || f.StorePath.Contains("/Mozilla/Firefox/", StringComparison.OrdinalIgnoreCase)))
            extras.Add("browser bookmarks and settings");
        if (m.Files.Any(f => f.StorePath.EndsWith(".pst", StringComparison.OrdinalIgnoreCase))) extras.Add("Outlook data files (.pst)");
        if (extras.Count > 0) sb.Append($"<p>Also {(applied is null ? "saved" : "moved")}: {E(string.Join(", ", extras))}.</p>");

        // Programs
        if (applied is not null && applied.Apps.Count > 0)
        {
            sb.Append("<h2>Programs</h2><table><tr><th>Program</th><th>Result</th></tr>");
            foreach (var a in applied.Apps.Where(a => a.Outcome != AppOutcome.Manual && a.Outcome != AppOutcome.NotSelected).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var (cls, word) = a.Outcome switch
                {
                    AppOutcome.Installed => ("ok", "Installed"),
                    AppOutcome.Copied => ("ok", "Copied"),
                    AppOutcome.AlreadyInstalled => ("ok", "Already there"),
                    _ => ("bad", "Not done"),
                };
                sb.Append($"<tr><td>{E(a.Name)}</td><td><span class=\"tag {cls}\">{word}</span>{(a.Outcome == AppOutcome.Failed && a.Message is not null ? " " + E(a.Message) : "")}</td></tr>");
            }
            sb.Append("</table>");
        }

        var manual = m.Apps.Where(a => a.Method == AppMethod.Manual).ToList();
        var stillMissing = applied?.AppsToReinstall.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (stillMissing is not null) manual = manual.Where(a => stillMissing.Contains(a.Name)).ToList();
        var otherMissing = applied?.AppsToReinstall.Where(a => !manual.Any(x => x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))).ToList() ?? [];
        if (manual.Count + otherMissing.Count > 0)
        {
            sb.Append("<h2>To install by hand</h2><table><tr><th>Program</th><th>Why, and how</th></tr>");
            foreach (var a in manual.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)) sb.Append($"<tr><td>{E(a.Name)}</td><td>{E(a.Reason)}</td></tr>");
            foreach (var a in otherMissing.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)) sb.Append($"<tr><td>{E(a.Name)}</td><td>Was on the old computer. Install it from its maker's website if it's still needed.</td></tr>");
            sb.Append("</table>");
        }

        // Keys
        if (m.LicenseKeys.Count > 0)
        {
            sb.Append("<h2>License keys</h2>");
            if (tech?.ShowKeys == true)
            {
                sb.Append("<table><tr><th>Product</th><th>Key</th></tr>");
                foreach (var k in m.LicenseKeys) sb.Append($"<tr><td>{E(k.Product)}{(k.Note is null ? "" : $"<br><small>{E(k.Note)}</small>")}</td><td class=\"key\">{E(k.Key)}</td></tr>");
                sb.Append("</table><p><small>Keep this page somewhere safe: these keys are what you paid for.</small></p>");
            }
            else
            {
                sb.Append($"<p>{m.LicenseKeys.Count} product key(s) were found and saved with the backup ({E(Keys.LicenseKeyReport.StorePath)}).</p>");
            }
        }

        // Problems
        var warnings = (applied?.Warnings ?? m.Warnings).Distinct().ToList();
        if (warnings.Count > 0)
        {
            sb.Append($"<h2>Things to know ({warnings.Count})</h2><ul>");
            foreach (var w in warnings.Take(25)) sb.Append($"<li>{E(w)}</li>");
            if (warnings.Count > 25) sb.Append($"<li>…and {warnings.Count - 25} more, listed in the full log.</li>");
            sb.Append("</ul>");
        }
        if (applied?.MissingUsers.Count > 0)
            sb.Append($"<p class=\"note\">Not moved, because there's no matching account on the new computer: {E(string.Join(", ", applied.MissingUsers))}.</p>");

        sb.Append("<div class=\"sign\"><div>Technician</div><div>Client</div></div>");
        sb.Append($"<footer>Made with pc-migrate {E(ToolInfo.Version)}.{(applied is null ? " The backup holds everything listed above; keep it until you've checked your files." : " Keep the backup until you've checked everything is on the new computer.")}</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Meta(StringBuilder sb, string label, string value) =>
        sb.Append($"<div><span>{WebUtility.HtmlEncode(label)}</span>{WebUtility.HtmlEncode(value)}</div>");

    private static void Tile(StringBuilder sb, string value, string label) =>
        sb.Append($"<div class=\"tile\"><b>{WebUtility.HtmlEncode(value)}</b><span>{WebUtility.HtmlEncode(label)}</span></div>");

    private sealed record UserTotal(string Name, long Files, long Bytes, List<string> Folders);

    private static List<UserTotal> UserTotals(Manifest m) =>
        m.Files
            .Select(f => StorePath.TryParseUserFile(f.StorePath, out var user, out var rel) ? (user, rel, f.Length) : (null, null, 0))
            .Where(x => x.user is not null)
            .GroupBy(x => x.user!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UserTotal(g.Key, g.LongCount(), g.Sum(x => x.Length),
                g.Select(x => x.rel!.Split('/')[0]).Where(f => !f.Equals("AppData", StringComparison.OrdinalIgnoreCase) && !f.StartsWith('.'))
                 .GroupBy(f => f, StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Count()).Select(f => f.Key).ToList()))
            .OrderByDescending(u => u.Bytes)
            .ToList();
}

/// <summary>Where the technician's details are kept between jobs.</summary>
public static class TechnicianStore
{
    public static string PathOnDisk => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pc-migrate", TechnicianSettings.FileName);

    public static TechnicianSettings Load()
    {
        try { return File.Exists(PathOnDisk) ? Json.Deserialize<TechnicianSettings>(File.ReadAllText(PathOnDisk)) : new TechnicianSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { return new TechnicianSettings(); }
    }

    public static void Save(TechnicianSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk)!);
        File.WriteAllText(PathOnDisk, Json.Serialize(settings));
    }

    /// <summary>Writes the client report into <paramref name="folder"/> and returns its path, or null if it couldn't be written.</summary>
    public static string? Write(ClientReport.Input input, string folder, IMigrationLog log)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, ClientReport.FileName(input));
            File.WriteAllText(path, ClientReport.Build(input));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not save the client report: {ex.Message}");
            return null;
        }
    }
}
