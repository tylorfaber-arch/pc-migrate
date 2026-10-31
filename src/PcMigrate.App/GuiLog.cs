using System.Diagnostics;
using PcMigrate.Core;

namespace PcMigrate.App;

/// <summary>Forwards engine messages to the page, and optionally to a log file.</summary>
internal sealed class GuiLog(Action<string, object> post, string? logFile = null) : IMigrationLog, IDisposable
{
    private readonly Stopwatch _sinceProgress = Stopwatch.StartNew();
    private readonly StreamWriter? _file = Open(logFile);
    private readonly object _lock = new();

    public List<string> Warnings { get; } = [];
    /// <summary>Also told about progress, a few times a second (the taskbar button uses it).</summary>
    public Action<long, long>? OnProgress { get; init; }

    public void Info(string message)
    {
        Write("INFO", message);
        post("log", new { message });
    }

    public void Warn(string message)
    {
        lock (_lock) Warnings.Add(message);
        Write("WARN", message);
        post("warning", new { message });
    }

    public void FileProgress(long filesDone, long bytesDone, string currentPath)
    {
        // The engine reports every file; the page only needs a few updates a second.
        lock (_lock)
        {
            if (_sinceProgress.ElapsedMilliseconds < 120) return;
            _sinceProgress.Restart();
        }
        post("progress", new { files = filesDone, bytes = bytesDone, path = currentPath });
        OnProgress?.Invoke(filesDone, bytesDone);
    }

    private void Write(string level, string message)
    {
        if (_file is null) return;
        lock (_lock) _file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}");
    }

    private static StreamWriter? Open(string? path)
    {
        if (path is null) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _file?.Dispose();
}
