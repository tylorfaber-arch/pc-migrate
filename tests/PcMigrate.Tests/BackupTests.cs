using PcMigrate.Core;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Schedule;
using PcMigrate.Core.Storage;

namespace PcMigrate.Tests;

public class IncrementalBackupTests
{
    private sealed class CountingLog : IMigrationLog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
        public void Warn(string message) => Lines.Add("WARN " + message);
        public void FileProgress(long filesDone, long bytesDone, string currentPath) { }
    }

    private static async Task<(FolderSink Sink, CountingLog Log)> RunAsync(FakePc pc, string folder, string? password = null)
    {
        var log = new CountingLog();
        await using var sink = FolderSink.OpenForUpdate(folder, password);
        await new CaptureEngine(pc, log).RunAsync(sink, Options.Capture());
        return (sink, log);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("correct horse")]
    public async Task SecondRun_KeepsUnchanged_CopiesChanged_RemovesDeleted(string? password)
    {
        using var pc = new FakePc("pc", "tylor");
        using var store = new TempDir();
        var old = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        pc.WriteFile("tylor", "Documents/same.txt", "same", old);
        pc.WriteFile("tylor", "Documents/changes.txt", "v1", old);
        var gone = pc.WriteFile("tylor", "Documents/gone.txt", "bye", old);

        var (first, firstLog) = await RunAsync(pc, store.Path, password);
        Assert.DoesNotContain(firstLog.Lines, l => l.Contains("hadn't changed"));

        var ext = password is null ? "" : StoreKey.Extension;
        var sameStored = StorePath.ToLocal(store.Path, "files/users/tylor/Documents/same.txt") + ext;
        var stamp = new DateTime(2020, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sameStored, stamp); // so a rewrite would show

        pc.WriteFile("tylor", "Documents/changes.txt", "version two", old.AddDays(1));
        File.Delete(gone);
        pc.WriteFile("tylor", "Documents/new.txt", "new", old);

        var (second, secondLog) = await RunAsync(pc, store.Path, password);
        Assert.Contains(secondLog.Lines, l => l.Contains("1 of the files hadn't changed"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(sameStored));
        Assert.Equal(1, second.Removed);
        Assert.False(File.Exists(StorePath.ToLocal(store.Path, "files/users/tylor/Documents/gone.txt") + ext));

        // The updated backup loads onto a new PC like any other.
        using var newPc = new FakePc("new", "tylor");
        await new ApplyEngine(newPc, new ListLog()).RunAsync(store.Path, new ApplyOptions { Password = password });
        var docs = Path.Combine(newPc.Profile("tylor"), "Documents");
        Assert.Equal("same", File.ReadAllText(Path.Combine(docs, "same.txt")));
        Assert.Equal("version two", File.ReadAllText(Path.Combine(docs, "changes.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(docs, "new.txt")));
        Assert.False(File.Exists(Path.Combine(docs, "gone.txt")));
    }

    [Fact]
    public async Task PasswordCantBeAddedToAPlainBackup()
    {
        using var pc = new FakePc("pc", "tylor");
        using var store = new TempDir();
        pc.WriteFile("tylor", "Documents/a.txt", "a");
        await RunAsync(pc, store.Path);
        Assert.Throws<InvalidOperationException>(() => FolderSink.OpenForUpdate(store.Path, "a password"));
    }

    [Fact]
    public async Task WrongPasswordIsRefused()
    {
        using var pc = new FakePc("pc", "tylor");
        using var store = new TempDir();
        pc.WriteFile("tylor", "Documents/a.txt", "a");
        await RunAsync(pc, store.Path, "right password");
        Assert.Throws<StoreLockedException>(() => FolderSink.OpenForUpdate(store.Path, "wrong password"));
    }
}

public class ScheduleTests
{
    [Fact]
    public void TaskRunsAsSystem_CatchesUpOnMissedRuns()
    {
        var settings = new ScheduleSettings { Folder = @"E:\Backups", Frequency = "weekly", Day = DayOfWeek.Friday, Time = "18:30" };
        var xml = ScheduleTask.Xml(settings, @"C:\ProgramData\pc-migrate\pcmigrate-cli.exe", @"C:\ProgramData\pc-migrate\backup.log", new DateTime(2026, 11, 3));
        var doc = System.Xml.Linq.XDocument.Parse(xml); // well-formed
        Assert.Contains("<StartBoundary>2026-11-03T18:30:00</StartBoundary>", xml);
        Assert.Contains("<Friday />", xml);
        Assert.Contains("<UserId>S-1-5-18</UserId>", xml);
        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
        Assert.Contains("backup --scheduled --log", xml);
        Assert.Equal("Every Friday at 18:30", settings.Describe());
        Assert.NotNull(doc.Root);
    }

    [Theory]
    [InlineData(@"E:\Backups", "daily", "21:00", null)]
    [InlineData(@"\\nas\backups", "daily", "21:00", "network")]
    [InlineData(@"E:\Backups", "hourly", "21:00", "daily or weekly")]
    [InlineData(@"E:\Backups", "daily", "9pm", "HH:mm")]
    public void SettingsAreChecked(string folder, string frequency, string time, string? problem)
    {
        var p = new ScheduleSettings { Folder = folder, Frequency = frequency, Time = time }.Problem();
        if (problem is null) Assert.Null(p); else Assert.Contains(problem, p);
    }
}
