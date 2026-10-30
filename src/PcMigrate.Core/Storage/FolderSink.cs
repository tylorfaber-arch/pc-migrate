using PcMigrate.Core.Models;

namespace PcMigrate.Core.Storage;

/// <summary>Writes a migration store to a folder, e.g. on an external drive.</summary>
public sealed class FolderSink : IMigrationSink
{
    private readonly string _root;

    public FolderSink(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<FileWriteResult> AddFileAsync(string storePath, Stream content, DateTime lastWriteUtc, CancellationToken ct)
    {
        var target = StorePath.ToLocal(_root, storePath);
        // Write to a temp name first so an interrupted copy never looks like a finished file.
        var temp = target + ".partial";
        FileStream output;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SinkWriteException($"Cannot write to '{target}': {ex.Message}", ex);
        }

        FileWriteResult result;
        try
        {
            await using (output)
                result = await HashingCopy.CopyAsync(content, output, ct);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        File.Move(temp, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, lastWriteUtc);
        return result;
    }

    public async Task CompleteAsync(Manifest manifest, CancellationToken ct)
    {
        var path = Path.Combine(_root, Manifest.FileName);
        await File.WriteAllTextAsync(path, Json.Serialize(manifest), ct);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static Manifest ReadManifest(string root)
    {
        var path = Path.Combine(root, Manifest.FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No {Manifest.FileName} in '{root}'. Either this is not a migration store or the capture did not finish.", path);
        var manifest = Json.Deserialize<Manifest>(File.ReadAllText(path));
        if (manifest.FormatVersion > Manifest.CurrentFormatVersion)
            throw new InvalidDataException(
                $"This store was made by a newer version of pc-migrate (format {manifest.FormatVersion}). Update pc-migrate on this PC.");
        return manifest;
    }
}
