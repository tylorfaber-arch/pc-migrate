namespace PcMigrate.Core.Offline;

/// <summary>
/// Paths on a drive pulled from a dead PC. That PC's registry still says
/// C:\Users\tylor, but plugged in here the same folder is E:\Users\tylor.
/// Everything recorded in the migration keeps the old PC's own paths;
/// they're only translated at the moment a file is read.
/// </summary>
public sealed class OfflinePaths
{
    private readonly Dictionary<string, string> _variables;

    /// <param name="root">Where the drive is mounted now, e.g. "E:\".</param>
    /// <param name="systemDrive">The drive letter it had in the old PC, nearly always "C:".</param>
    public OfflinePaths(string root, string systemDrive = "C:", IDictionary<string, string>? variables = null)
    {
        Root = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        SystemDrive = systemDrive.TrimEnd('\\');
        var windows = SystemDrive + @"\Windows";
        _variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemDrive"] = SystemDrive,
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["ProgramFiles"] = SystemDrive + @"\Program Files",
            ["ProgramW6432"] = SystemDrive + @"\Program Files",
            ["ProgramFiles(x86)"] = SystemDrive + @"\Program Files (x86)",
            ["CommonProgramFiles"] = SystemDrive + @"\Program Files\Common Files",
            ["CommonProgramFiles(x86)"] = SystemDrive + @"\Program Files (x86)\Common Files",
            ["ProgramData"] = SystemDrive + @"\ProgramData",
            ["ALLUSERSPROFILE"] = SystemDrive + @"\ProgramData",
            ["PUBLIC"] = SystemDrive + @"\Users\Public",
        };
        foreach (var (name, value) in variables ?? new Dictionary<string, string>())
            _variables[name] = Expand(value);
    }

    public string Root { get; }
    public string SystemDrive { get; }

    /// <summary>Fills in %ProgramFiles% and the like with the old PC's values, not this PC's.</summary>
    public string Expand(string path)
    {
        var s = path.Trim().Trim('"');
        // Service paths are often written as \??\C:\... or \SystemRoot\System32\...
        if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];
        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = _variables["SystemRoot"] + s[11..];
        for (var start = s.IndexOf('%'); start >= 0 && start < s.Length - 1; start = s.IndexOf('%', start + 1))
        {
            var end = s.IndexOf('%', start + 1);
            if (end < 0) break;
            if (_variables.TryGetValue(s[(start + 1)..end], out var value))
            {
                s = s[..start] + value + s[(end + 1)..];
                end = start + value.Length - 1;
            }
            start = end;
        }
        return s;
    }

    /// <summary>C:\Users\tylor\Documents → E:\Users\tylor\Documents. Paths on other drives are left alone.</summary>
    public string ToReadable(string path)
    {
        var p = Expand(path);
        if (!p.StartsWith(SystemDrive, StringComparison.OrdinalIgnoreCase) || (p.Length > SystemDrive.Length && p[SystemDrive.Length] is not ('\\' or '/')))
            return p;
        var rest = p[SystemDrive.Length..].TrimStart('\\', '/');
        return Root + rest.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>E:\ProgramData\... → C:\ProgramData\..., the reverse of <see cref="ToReadable"/>.</summary>
    public string ToOriginal(string readable)
    {
        if (!readable.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return readable;
        return SystemDrive + @"\" + readable[Root.Length..].Replace(Path.DirectorySeparatorChar, '\\');
    }
}
