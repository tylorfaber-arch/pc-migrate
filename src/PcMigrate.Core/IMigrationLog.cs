namespace PcMigrate.Core;

/// <summary>Progress and messages from a long-running operation. The CLI prints them; a GUI can show them.</summary>
public interface IMigrationLog
{
    void Info(string message);
    void Warn(string message);
    void FileProgress(long filesDone, long bytesDone, string currentPath);
}

public sealed class NullLog : IMigrationLog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void FileProgress(long filesDone, long bytesDone, string currentPath) { }
}
