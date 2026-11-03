using System.Text.RegularExpressions;
using PcMigrate.Core.Models;

namespace PcMigrate.Core.Apps;

/// <summary>
/// Looks for a program's original installer on the old PC, so a program winget doesn't know
/// can still be installed properly on the new one instead of being left for the technician.
/// </summary>
public static partial class InstallerFinder
{
    /// <summary>Installers bigger than this are left alone (they're usually whole DVDs).</summary>
    public const long MaxBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// The installer's path as this tool can read it, or null. Places checked, best first:
    /// the copy a "bundle" installer keeps of itself, the Package Cache path in its uninstall
    /// command, the folder an .msi was installed from, then the people's Downloads folders.
    /// </summary>
    public static string? Find(InstalledApp app, Func<string, string> readable, IEnumerable<string> downloadFolders)
    {
        if (Usable(app.BundleCachePath, readable) is { } bundle) return bundle;
        if (CachedFromUninstall(app.UninstallString) is { } cached && Usable(cached, readable) is { } fromUninstall) return fromUninstall;
        if (FromInstallSource(app, readable) is { } msi) return msi;
        return FromDownloads(app.Name, downloadFolders);
    }

    /// <summary>"C:\ProgramData\Package Cache\{id}\setup.exe" /uninstall  ->  the exe path.</summary>
    internal static string? CachedFromUninstall(string? uninstall)
    {
        if (string.IsNullOrWhiteSpace(uninstall)) return null;
        var m = QuotedOrPlainExe().Match(uninstall);
        if (!m.Success) return null;
        var path = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["p"].Value;
        return path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string? FromInstallSource(InstalledApp app, Func<string, string> readable)
    {
        if (!app.IsMsi || string.IsNullOrWhiteSpace(app.InstallSource)) return null;
        var source = app.InstallSource.Trim().Trim('"');
        if (source.StartsWith(@"\\", StringComparison.Ordinal)) return null; // a network share may not be there any more
        var folder = readable(source);
        if (!Directory.Exists(folder)) return null;
        string[] msis;
        try { msis = Directory.GetFiles(folder, "*.msi", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        // Temp folders hold other programs' installers too, so a single file or a name match is needed.
        var pick = msis.Length == 1 ? msis[0] : msis.FirstOrDefault(f => NameMatches(app.Name, Path.GetFileNameWithoutExtension(f)));
        return Usable(pick, s => s);
    }

    private static string? FromDownloads(string name, IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                    .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            // Newest first: the one most likely to match the installed version.
            var hit = files
                .Where(f => NameMatches(name, Path.GetFileNameWithoutExtension(f)))
                .Select(f => new FileInfo(f))
                .Where(f => f.Length is > 0 and < MaxBytes)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (hit is not null) return hit.FullName;
        }
        return null;
    }

    /// <summary>
    /// True when a file name plainly belongs to the program: its first three real words all appear
    /// ("Foxit PDF Reader" matches FoxitPDFReader_Setup.exe). A one-word name needs five letters or more.
    /// </summary>
    public static bool NameMatches(string programName, string fileName)
    {
        var words = Words(programName).Take(3).ToList();
        if (words.Count == 0 || (words.Count == 1 && words[0].Length < 5)) return false;
        var file = Squash(fileName);
        return words.All(w => file.Contains(w, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Words(string name) =>
        Word().Matches(name.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length >= 2 && !char.IsDigit(w[0]) && !Filler.Contains(w));

    private static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static readonly HashSet<string> Filler = ["the", "inc", "llc", "ltd", "corp", "corporation", "software", "version", "for", "and", "by", "x64", "x86", "bit"];

    private static string? Usable(string? path, Func<string, string> readable)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var file = readable(path.Trim().Trim('"'));
        try
        {
            var info = new FileInfo(file);
            return info.Exists && info.Length is > 0 and < MaxBytes ? info.FullName : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    [GeneratedRegex(@"^\s*(""(?<q>[^""]+\.exe)""|(?<p>[A-Za-z]:\\.+?\.exe))", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedOrPlainExe();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();
}
