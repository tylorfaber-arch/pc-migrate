using System.Diagnostics;

namespace PcMigrate.Core.Windows;

internal readonly record struct ProcessResult(int ExitCode, string Output);

internal static class ProcessRunner
{
    public static ProcessResult Run(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }
}
