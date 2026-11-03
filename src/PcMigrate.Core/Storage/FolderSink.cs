using PcMigrate.Core.Models;

namespace PcMigrate.Core.Storage;

/// <summary>Writes a migration store to a folder, e.g. on an external drive.</summary>
public sealed class FolderSink : IMigrationSink
{
    private readonly string _root;
    private readonly StoreKey? _key;

    /// <param name="password">Protects the backup: every file is encrypted with it. Null for a plain backup.</param>
    public FolderSink(string root, string? password = null)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        if (password is not null)
        {
            var (key, info) = StoreKey.Create(password);
            _key = key;
            File.WriteAllText(Path.Combine(_root, EncryptionInfo.FileName), Json.Serialize(info));
        }
    }

    public string Root => _root;

    public async Task<FileWriteResult> AddFileAsync(string storePath, Stream content, DateTime lastWriteUtc, CancellationToken ct)
    {
        var target = StorePath.ToLocal(_root, storePath) + (_key is null ? "" : StoreKey.Extension);
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
                result = _key is null
                    ? await HashingCopy.CopyAsync(content, output, ct)
                    : await _key.EncryptAsync(content, output, storePath, ct);
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
        var json = Json.Serialize(manifest);
        if (_key is null) await File.WriteAllTextAsync(path, json, ct);
        else await File.WriteAllBytesAsync(path + StoreKey.Extension, _key.Seal(System.Text.Encoding.UTF8.GetBytes(json), Manifest.FileName), ct);
    }

    /// <summary>True when the folder holds a backup, finished, plain or password-protected.</summary>
    public static bool HasStore(string root) =>
        File.Exists(Path.Combine(root, Manifest.FileName)) || File.Exists(Path.Combine(root, Manifest.FileName + StoreKey.Extension));

    public static bool IsProtected(string root) => File.Exists(Path.Combine(root, EncryptionInfo.FileName));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <param name="password">Needed for a password-protected backup; a <see cref="StoreLockedException"/> says it's missing or wrong.</param>
    public static Manifest ReadManifest(string root, string? password = null)
    {
        var path = Path.Combine(root, Manifest.FileName);
        string json;
        if (StoreKey.ReadInfo(root) is { } info)
        {
            var key = StoreKey.Unlock(info, password);
            if (!File.Exists(path + StoreKey.Extension))
                throw new FileNotFoundException($"No {Manifest.FileName} in '{root}'. The capture did not finish.", path);
            json = System.Text.Encoding.UTF8.GetString(key.Open(File.ReadAllBytes(path + StoreKey.Extension), Manifest.FileName));
        }
        else
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"No {Manifest.FileName} in '{root}'. Either this is not a migration store or the capture did not finish.", path);
            json = File.ReadAllText(path);
        }
        var manifest = Json.Deserialize<Manifest>(json);
        if (manifest.FormatVersion > Manifest.CurrentFormatVersion)
            throw new InvalidDataException(
                $"This store was made by a newer version of pc-migrate (format {manifest.FormatVersion}). Update pc-migrate on this PC.");
        return manifest;
    }
}
