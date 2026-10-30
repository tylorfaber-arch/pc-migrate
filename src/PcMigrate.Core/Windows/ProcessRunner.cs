using System.Diagnostics;

namespace PcMigrate.Core.Windows;

internal readonly record struct ProcessResult(int ExitCode, string Output);

internal static class ProcessRunner
{
    public static ProcessResult Run(string fileName, params string[] args)
    {
        using var p = Start(fileName, args);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    public static async Task<ProcessResult> RunAsync(string fileName, CancellationToken ct, params string[] args)
    {
        using var p = Start(fileName, args);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return new ProcessResult(p.ExitCode, (await stdout + await stderr).Trim());
    }

    private static Process Start(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
    }
}
