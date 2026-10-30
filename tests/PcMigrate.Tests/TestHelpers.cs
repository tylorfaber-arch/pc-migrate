using PcMigrate.Core;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;

namespace PcMigrate.Tests;

/// <summary>A pretend PC: a temp folder with user profiles under it.</summary>
internal sealed class FakePc : ISystemProbe, IDisposable
{
    public FakePc(string name, params string[] users)
    {
        Root = Path.Combine(Path.GetTempPath(), "pcmigrate-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Inventory = new Inventory
        {
            Machine = new MachineInfo { ComputerName = name, OsDescription = "Test OS" },
            Users = users.Select((u, i) => new UserProfileInfo
            {
                UserName = u,
                Sid = $"S-1-5-21-1-2-3-{1000 + i}",
                ProfilePath = Directory.CreateDirectory(Path.Combine(Root, "Users", u)).FullName,
            }).ToList(),
        };
    }

    public string Root { get; }
    public Inventory Inventory { get; }
    public string? CurrentUserSid => null;
    public Inventory GetInventory() => Inventory;

    public string Profile(string user) => Inventory.Users.Single(u => u.UserName == user).ProfilePath;

    public string WriteFile(string user, string relative, string content, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(Profile(user), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (lastWriteUtc is { } t) File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcmigrate-tests", "store-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal sealed class ListLog : IMigrationLog
{
    public List<string> Warnings { get; } = [];
    public void Info(string message) { }
    public void Warn(string message) { lock (Warnings) Warnings.Add(message); }
    public void FileProgress(long filesDone, long bytesDone, string currentPath) { }
}

internal static class Options
{
    // Shadow copies, Wi-Fi and registry settings only exist on Windows.
    public static CaptureOptions Capture(params string[] users) => new()
    {
        Users = users,
        UseShadowCopy = false,
        IncludeWifi = false,
        IncludeUserSettings = false,
    };
}
