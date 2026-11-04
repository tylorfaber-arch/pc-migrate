using PcMigrate.Core.Models;

namespace PcMigrate.Core.Storage;

/// <summary>Writes a migration store to a folder, e.g. on an external drive.</summary>
public sealed class FolderSink : IMigrationSink
{
    private readonly string _root;
    private readonly StoreKey? _key;
    // For a backup being brought up to date: what the last run saved.
    private readonly Dictionary<string, ManifestFile>? _previous;

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
        else
        {
            // A plain backup into a folder an unfinished password-protected run left behind: its
            // password file would make this backup ask for a password it doesn't have.
            File.Delete(Path.Combine(_root, EncryptionInfo.FileName));
        }
    }

    private FolderSink(string root, StoreKey? key, Dictionary<string, ManifestFile>? previous)
    {
        _root = root;
        _key = key;
        _previous = previous;
    }

    /// <summary>
    /// Opens a backup folder to bring it up to date: files that haven't changed since the last
    /// run are kept as they are, and files that are gone from the PC are removed at the end.
    /// A new or empty folder starts a fresh backup.
    /// </summary>
    public static FolderSink OpenForUpdate(string root, string? password)
    {
        var full = Path.GetFullPath(root);
        if (!HasStore(full)) return new FolderSink(full, password);
        var info = StoreKey.ReadInfo(full);
        if (info is null && password is not null)
            throw new InvalidOperationException("This backup folder has a backup without a password. Use an empty folder for a password-protected backup.");
        var previous = ReadManifest(full, password);
        var key = info is null ? null : StoreKey.Unlock(info, password);
        var files = previous.Files
            .GroupBy(f => f.StorePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        return new FolderSink(full, key, files);
    }

    public string Root => _root;

    /// <summary>How many files the last call to <see cref="CompleteAsync"/> removed because they're gone from the PC.</summary>
    public int Removed { get; private set; }

    public bool TryReuse(string storePath, long length, DateTime lastWriteUtc, out FileWriteResult result)
    {
        result = default;
        if (_previous is null || !_previous.TryGetValue(storePath, out var old)) return false;
        if (old.Length != length || old.LastWriteUtc != lastWriteUtc) return false;
        if (!File.Exists(StorePath.ToLocal(_root, storePath) + (_key is null ? "" : StoreKey.Extension))) return false;
        result = new FileWriteResult(old.Length, old.Sha256);
        return true;
    }

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
        var path = Path.Combine(_root, Manifest.FileName) + (_key is null ? "" : StoreKey.Extension);
        var json = Json.Serialize(manifest);
        var bytes = _key is null ? System.Text.Encoding.UTF8.GetBytes(json) : _key.Seal(System.Text.Encoding.UTF8.GetBytes(json), Manifest.FileName);
        // Replace the old manifest in one step, so a backup being updated always has a whole one.
        await File.WriteAllBytesAsync(path + ".partial", bytes, ct);
        File.Move(path + ".partial", path, overwrite: true);
        if (_previous is not null) Removed = RemoveLeftovers(manifest);
    }

    /// <summary>Deletes saved files the new manifest doesn't list (they're gone from the PC), and empty folders.</summary>
    private int RemoveLeftovers(Manifest manifest)
    {
        var keep = manifest.Files.Select(f => StorePath.ToLocal(_root, f.StorePath) + (_key is null ? "" : StoreKey.Extension))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var top in new[] { "files", StorePath.SettingsRoot })
        {
            var folder = Path.Combine(_root, top);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
            {
                if (keep.Contains(file)) continue;
                try { File.Delete(file); removed++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return removed;
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
