using System.Diagnostics;
using PcMigrate.Core;
using PcMigrate.Core.Capture;

namespace PcMigrate.Cli;

/// <summary>Prints progress to the console, redrawing one status line instead of a line per file.</summary>
internal sealed class ConsoleLog(string? logFile) : IMigrationLog, IDisposable
{
    private readonly object _lock = new();
    private readonly Stopwatch _sinceRedraw = Stopwatch.StartNew();
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly StreamWriter? _file = OpenLogFile(logFile);
    private bool _statusShown;
    public int WarningCount { get; private set; }

    public void Info(string message) => Write(message, ConsoleColor.Gray, "INFO");

    public void Warn(string message)
    {
        WarningCount++;
        Write(message, ConsoleColor.Yellow, "WARN");
    }

    public void FileProgress(long filesDone, long bytesDone, string currentPath)
    {
        lock (_lock)
        {
            if (_sinceRedraw.ElapsedMilliseconds < 200) return;
            _sinceRedraw.Restart();
            var rate = bytesDone / Math.Max(1, _total.Elapsed.TotalSeconds);
            var line = $"  {filesDone:N0} files, {CaptureEngine.FormatBytes(bytesDone)} ({CaptureEngine.FormatBytes((long)rate)}/s)  {Path.GetFileName(currentPath)}";
            var width = Math.Max(20, SafeWidth() - 1);
            Console.Write("\r" + (line.Length > width ? line[..width] : line.PadRight(width)));
            _statusShown = true;
        }
    }

    private void Write(string message, ConsoleColor color, string level)
    {
        lock (_lock)
        {
            if (_statusShown)
            {
                Console.Write("\r" + new string(' ', Math.Max(20, SafeWidth() - 1)) + "\r");
                _statusShown = false;
            }
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(level == "WARN" ? "! " + message : message);
            Console.ForegroundColor = old;
            _file?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}");
        }
    }

    private static StreamWriter? OpenLogFile(string? path)
    {
        if (path is null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new StreamWriter(path, append: true) { AutoFlush = true };
    }

    private static int SafeWidth()
    {
        try { return Console.IsOutputRedirected ? 120 : Console.WindowWidth; }
        catch (IOException) { return 120; }
    }

    public void Dispose() => _file?.Dispose();
}
