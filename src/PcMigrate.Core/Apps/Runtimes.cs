using System.Text.RegularExpressions;
using PcMigrate.Core.Models;

namespace PcMigrate.Core.Apps;

/// <summary>Recognises shared runtimes among installed programs and says how to install each one.</summary>
public static partial class Runtimes
{
    private sealed record Rule(Regex Pattern, Func<Match, string> Id, string QuietArgs, Func<Match, string> Name);

    private static readonly Rule[] Rules =
    [
        new(VcRedist(), m => $"Microsoft.VCRedist.{VcYear(m.Groups["year"].Value)}.{Arch(m)}",
            "/install /quiet /norestart", m => $"Visual C++ {VcYear(m.Groups["year"].Value)} runtime ({Arch(m)})"),
        new(DesktopRuntime(), m => $"Microsoft.DotNet.DesktopRuntime.{m.Groups["v"].Value}", "/install /quiet /norestart", m => $".NET Desktop Runtime {m.Groups["v"].Value}"),
        new(DotNetRuntime(), m => $"Microsoft.DotNet.Runtime.{m.Groups["v"].Value}", "/install /quiet /norestart", m => $".NET Runtime {m.Groups["v"].Value}"),
        new(AspNetCore(), m => $"Microsoft.DotNet.AspNetCore.{m.Groups["v"].Value}", "/install /quiet /norestart", m => $"ASP.NET Core Runtime {m.Groups["v"].Value}"),
        new(Java(), _ => "Oracle.JavaRuntimeEnvironment", "/s", _ => "Java runtime"),
    ];

    // Older Visual C++ versions take different switches.
    private static string QuietFor(string id) =>
        id.Contains(".2005.") || id.Contains(".2008.") ? "/q" : id.Contains(".2010.") ? "/q /norestart" : "/install /quiet /norestart";

    private static string VcYear(string year) => year is "2015" or "2017" or "2019" or "2022" ? "2015+" : year;
    // The 32-bit ones often don't say so ("Microsoft Visual C++ 2005 Redistributable").
    private static string Arch(Match m) => m.Groups["arch"].Value.Equals("x64", StringComparison.OrdinalIgnoreCase) ? "x64" : "x86";

    /// <summary>The runtime an installed program entry stands for, or null if it isn't one we know.</summary>
    public static RuntimeNeed? Match(string name)
    {
        foreach (var rule in Rules)
        {
            var m = rule.Pattern.Match(name);
            if (!m.Success) continue;
            var id = rule.Id(m);
            return new RuntimeNeed { Name = rule.Name(m), WingetId = id, QuietArgs = rule.Pattern == Rules[0].Pattern ? QuietFor(id) : rule.QuietArgs };
        }
        return null;
    }

    /// <summary>Every runtime on a PC, once each.</summary>
    public static List<RuntimeNeed> On(IEnumerable<InstalledApp> apps) =>
        apps.Select(a => Match(a.Name)).OfType<RuntimeNeed>().DistinctBy(r => r.WingetId, StringComparer.OrdinalIgnoreCase).OrderBy(r => r.Name).ToList();

    [GeneratedRegex(@"^Microsoft Visual C\+\+ (?<year>2005|2008|2010|2012|2013|2015|2017|2019|2022)(-\d{4})?\b(?=.*Redistributable)(?:.*?\b(?<arch>x64|x86)\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex VcRedist();
    [GeneratedRegex(@"^Microsoft Windows Desktop Runtime - (?<v>\d+)\.", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopRuntime();
    [GeneratedRegex(@"^Microsoft \.NET (Core )?Runtime - (?<v>\d+)\.", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetRuntime();
    [GeneratedRegex(@"^Microsoft ASP\.NET Core (?<v>\d+)\.\S+ (Shared Framework|- Shared Framework|Hosting Bundle)", RegexOptions.IgnoreCase)]
    private static partial Regex AspNetCore();
    [GeneratedRegex(@"^Java(\(TM\))? (\d+ Update \d+|SE Runtime Environment)", RegexOptions.IgnoreCase)]
    private static partial Regex Java();
}
