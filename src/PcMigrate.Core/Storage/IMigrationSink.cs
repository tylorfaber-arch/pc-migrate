using PcMigrate.Core.Models;

namespace PcMigrate.Core.Storage;

/// <summary>
/// Where captured data goes. A capture does not care whether it is writing to an
/// external drive (<see cref="FolderSink"/>) or streaming to another PC
/// (<see cref="Transfer.NetworkSender"/>).
/// </summary>
public interface IMigrationSink : IAsyncDisposable
{
    /// <summary>Stores one file and returns its SHA-256 as lowercase hex.</summary>
    Task<FileWriteResult> AddFileAsync(string storePath, Stream content, DateTime lastWriteUtc, CancellationToken ct);

    /// <summary>
    /// For a backup being brought up to date: true when the store already holds this file
    /// unchanged (same size and time) from the last run, so it needn't be copied again.
    /// </summary>
    bool TryReuse(string storePath, long length, DateTime lastWriteUtc, out FileWriteResult result)
    {
        result = default;
        return false;
    }

    /// <summary>Writes the manifest. Called once, after every file.</summary>
    Task CompleteAsync(Manifest manifest, CancellationToken ct);
}

public readonly record struct FileWriteResult(long Length, string Sha256);

/// <summary>The destination failed (disk full, connection lost). Unlike a source read error, the capture cannot continue.</summary>
public sealed class SinkWriteException(string message, Exception inner) : Exception(message, inner);
