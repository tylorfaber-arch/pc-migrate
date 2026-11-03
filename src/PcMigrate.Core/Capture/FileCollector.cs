namespace PcMigrate.Core.Capture;

public sealed record CollectedFile(string FullPath, string RelativePath, long Length, DateTime LastWriteUtc);

/// <summary>Walks a folder tree and yields the files a capture should copy.</summary>
public static class FileCollector
{
    // Cloud placeholders (OneDrive "online-only" files). Reading one forces a download, so they are skipped.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    /// <summary>
    /// A junction or symbolic link (it points at another folder). OneDrive's own files are reparse points
    /// too, but they hold real data and aren't links, so they're copied.
    /// </summary>
    public static bool IsLink(FileSystemInfo entry)
    {
        if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        try { return entry.LinkTarget is not null; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <param name="root">Folder to walk.</param>
    /// <param name="relativeBase">Prefix for <see cref="CollectedFile.RelativePath"/>, e.g. "Documents".</param>
    /// <param name="cloudOnly">Told about each online-only file that was skipped. Without it, each one is a warning.</param>
    public static IEnumerable<CollectedFile> Enumerate(string root, string relativeBase, CaptureRules rules, Action<string> warn, Action<string>? cloudOnly = null)
    {
        if (!Directory.Exists(root)) yield break;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        var pending = new Stack<(string Dir, string Rel)>();
        pending.Push((root, relativeBase.Replace('\\', '/').Trim('/')));

        while (pending.Count > 0)
        {
            var (dir, rel) = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                warn($"Could not read folder '{dir}': {ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                var childRel = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;

                if (entry is DirectoryInfo)
                {
                    // Junctions such as "My Music" inside Documents point back into the profile;
                    // following them would copy things twice or loop forever.
                    if (IsLink(entry)) continue;
                    if (rules.IsFolderExcluded(entry.Name)) continue;
                    pending.Push((entry.FullName, childRel));
                }
                else if (entry is FileInfo file)
                {
                    if (rules.IsFileExcluded(file.Name)) continue;
                    if ((file.Attributes & CloudOnly) != 0)
                    {
                        if (cloudOnly is not null) cloudOnly(file.FullName);
                        else warn($"Skipped online-only file (not downloaded on this PC): {file.FullName}");
                        continue;
                    }
                    if (IsLink(file)) continue;
                    yield return new CollectedFile(file.FullName, childRel, file.Length, file.LastWriteTimeUtc);
                }
            }
        }
    }
}
