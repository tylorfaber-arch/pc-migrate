namespace PcMigrate.Core.Apps;

public sealed record WingetRow(string Name, string Id, string Version, string Source);

/// <summary>
/// Reads the table that "winget list" prints. winget matches installed programs to
/// its catalogue itself; rows whose Source is "winget" can be reinstalled by Id.
/// </summary>
public static class WingetList
{
    public static List<WingetRow> Parse(string output)
    {
        var rows = new List<WingetRow>();
        // winget draws a spinner with carriage returns before the table; keep only what was printed last on each line.
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l).ToList();

        var header = lines.FindIndex(l => l.Contains("Name") && l.Contains(" Id ") && l.Contains("Version"));
        if (header < 0) return rows;
        var h = lines[header];
        // Some spinner characters may be left before "Name".
        var nameAt = h.IndexOf("Name", StringComparison.Ordinal);
        var idAt = h.IndexOf(" Id ", StringComparison.Ordinal) + 1;
        var versionAt = h.IndexOf("Version", idAt, StringComparison.Ordinal);
        var availableAt = h.IndexOf("Available", versionAt, StringComparison.Ordinal);
        var sourceAt = h.IndexOf("Source", versionAt, StringComparison.Ordinal);
        var versionEnd = availableAt > 0 ? availableAt : sourceAt;

        foreach (var line in lines.Skip(header + 1))
        {
            if (line.Length <= idAt || line.TrimStart().StartsWith('-')) continue;
            string Cut(int from, int to) => from >= line.Length ? "" : line[from..(to < 0 ? line.Length : Math.Min(to, line.Length))].Trim();

            var id = Cut(idAt, versionAt);
            // A name wider than its column shifts the row; such rows are skipped rather than misread.
            if (id.Length == 0 || id.Contains(' ')) continue;
            var source = sourceAt > 0 ? Cut(sourceAt, -1) : "";
            rows.Add(new WingetRow(Cut(nameAt, idAt).TrimEnd('\u2026').Trim(), id, Cut(versionAt, versionEnd), source));
        }
        return rows;
    }
}
