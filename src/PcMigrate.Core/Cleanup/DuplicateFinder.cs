using System.Security.Cryptography;

namespace PcMigrate.Core.Cleanup;

public sealed record DuplicateCopy(string Path, DateTime Modified);

/// <summary>Files that are byte-for-byte the same. <see cref="Keep"/> is the copy suggested to keep.</summary>
public sealed record DuplicateGroup(long Size, string Hash, List<DuplicateCopy> Copies)
{
    public string Keep => Copies[0].Path;

    /// <summary>Space freed by removing every copy but one.</summary>
    public long Wasted => Size * (Copies.Count - 1);
}

/// <summary>
/// Finds files with exactly the same contents, however they're named. Files are only read when
/// another file has the very same size, then compared by their first 64 KB, and only files that
/// still match are read in full and fingerprinted (SHA-256), so a big folder is quick to check.
/// </summary>
public static class DuplicateFinder
{
    private const int PartialBytes = 64 * 1024;

    public sealed record Options
    {
        /// <summary>Files smaller than this are ignored (lots of tiny files are the same on purpose).</summary>
        public long MinSize { get; init; } = 1024 * 1024;
        public bool IncludeHidden { get; init; }
    }

    public static List<DuplicateGroup> Find(IEnumerable<string> folders, Options? options = null, IProgress<(int Files, string Path)>? progress = null, CancellationToken ct = default)
    {
        options ??= new Options();
        var bySize = new Dictionary<long, List<FileInfo>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var folder in folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            // Windows' and programs' folders are skipped when they turn up inside the search, but a
            // folder someone picked on purpose (say, inside AppData) is searched.
            var pickedInside = IsProtected(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar);
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | (options.IncludeHidden ? 0 : FileAttributes.Hidden),
            };
            foreach (var path in Directory.EnumerateFiles(folder, "*", enumeration))
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(path) || (!pickedInside && IsProtected(path))) continue;
                FileInfo info;
                try { info = new FileInfo(path); if (info.Length < Math.Max(1, options.MinSize)) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (!bySize.TryGetValue(info.Length, out var list)) bySize[info.Length] = list = [];
                list.Add(info);
                if (++count % 500 == 0) progress?.Report((count, path));
            }
        }

        var groups = new List<DuplicateGroup>();
        foreach (var (size, sameSize) in bySize)
        {
            if (sameSize.Count < 2) continue;
            foreach (var samePart in sameSize.GroupBy(f => Hash(f.FullName, PartialBytes, ct)).Where(g => g.Key is not null && g.Count() > 1))
            {
                var candidates = samePart.ToList();
                // A small file's first 64 KB is the whole file, so it doesn't need reading again.
                var full = size <= PartialBytes ? candidates.GroupBy(_ => samePart.Key!) : candidates.GroupBy(f => Hash(f.FullName, long.MaxValue, ct));
                foreach (var same in full.Where(g => g.Key is not null && g.Count() > 1))
                {
                    var copies = same.Select(f => new DuplicateCopy(f.FullName, f.LastWriteTime)).ToList();
                    groups.Add(new DuplicateGroup(size, same.Key!, SortKeepFirst(copies)));
                }
            }
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    /// <summary>
    /// Puts the copy most worth keeping first: not in Downloads or a temp folder, not named like a
    /// copy ("(1)", "- Copy"), then the oldest (usually the original), then the shortest path.
    /// </summary>
    internal static List<DuplicateCopy> SortKeepFirst(List<DuplicateCopy> copies) =>
        copies.OrderBy(c => LooksLikeACopy(c.Path) ? 1 : 0)
              .ThenBy(c => c.Modified)
              .ThenBy(c => c.Path.Length)
              .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
              .ToList();

    internal static bool LooksLikeACopy(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Downloads/", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(" - Copy", StringComparison.OrdinalIgnoreCase)
            || name.Contains(" - Copy (", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"\(\d+\)$")
            || name.EndsWith(" copy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Windows' and programs' own files: never offered for removal.</summary>
    internal static bool IsProtected(string path)
    {
        var p = path.Replace('/', '\\');
        string[] roots = [@":\Windows\", @":\Program Files\", @":\Program Files (x86)\", @":\ProgramData\", @"\AppData\", @":\$Recycle.Bin\", @"\System Volume Information\"];
        return roots.Any(r => p.Contains(r, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Hash(string path, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long left = maxBytes;
            int read;
            while (left > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
                left -= read;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public sealed record RemoveResult(int Removed, long Freed, List<string> Problems);

    /// <summary>
    /// Removes the chosen copies. Refuses to remove every copy of a file, and checks each copy is
    /// still identical to the one being kept before it goes. <paramref name="remove"/> does the
    /// removing (the app sends files to the Recycle Bin, so a mistake can be undone).
    /// </summary>
    public static RemoveResult Remove(IEnumerable<DuplicateGroup> groups, ISet<string> chosen, Action<string> remove, CancellationToken ct = default)
    {
        int removed = 0; long freed = 0; var problems = new List<string>();
        foreach (var group in groups)
        {
            var going = group.Copies.Where(c => chosen.Contains(c.Path)).ToList();
            if (going.Count == 0) continue;
            var staying = group.Copies.Where(c => !chosen.Contains(c.Path)).ToList();
            if (staying.Count == 0)
            {
                problems.Add($"Kept {going[0].Path}: every copy of it was picked, and one has to stay.");
                going.RemoveAt(0);
                staying = [group.Copies[0]];
            }
            var keep = staying.FirstOrDefault(s => File.Exists(s.Path) && Hash(s.Path, long.MaxValue, ct) == group.Hash);
            if (keep is null)
            {
                problems.Add($"Left {Path.GetFileName(group.Keep)} alone: the copy being kept has changed or gone since the search.");
                continue;
            }
            foreach (var copy in going)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(copy.Path)) continue;
                    if (Hash(copy.Path, long.MaxValue, ct) != group.Hash) { problems.Add($"Left {copy.Path}: it has changed since the search."); continue; }
                    remove(copy.Path);
                    removed++; freed += group.Size;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"Couldn't remove {copy.Path}: {ex.Message}");
                }
            }
        }
        return new RemoveResult(removed, freed, problems);
    }
}
