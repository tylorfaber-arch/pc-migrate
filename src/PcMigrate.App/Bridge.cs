using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using PcMigrate.Core;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Clone;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;
using PcMigrate.Core.Transfer;
using PcMigrate.Core.Update;
using PcMigrate.Core.Windows;

namespace PcMigrate.App;

/// <summary>
/// Answers the page. The page sends {id, cmd, args}; each command replies with
/// {id, result} or {id, error}. Ongoing work is reported as {event, ...} messages.
/// </summary>
internal sealed class Bridge(Form form, CoreWebView2 web) : IDisposable
{
    // This PC, which is where things get applied.
    private readonly WindowsSystemProbe _probe = new(NullLog.Instance);
    private readonly WindowsAppPlatform _apps = new(NullLog.Instance);
    private readonly Notifier _notify = new(form);
    private Inventory? _inventory;
    // Set while copying from a drive pulled out of a dead PC instead of from this PC.
    private OfflineWindows? _pulled;
    private OfflineSystemProbe? _pulledProbe;
    private OfflineAppPlatform? _pulledApps;
    private Inventory? _pulledInventory;
    private Task<List<AppEntry>>? _planning;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _discovery;
    private CancellationTokenSource? _measuring;

    public bool IsBusy => _work is { IsCancellationRequested: false };

    public async void Handle(string json)
    {
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            var cmd = root.GetProperty("cmd").GetString();
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            var result = await RunAsync(cmd!, args);
            Reply(new { id, result });
        }
        catch (OperationCanceledException)
        {
            Reply(new { id, error = "Cancelled." });
        }
        catch (Exception ex)
        {
            Reply(new { id, error = Friendly(ex) });
        }
    }

    private async Task<object?> RunAsync(string cmd, JsonElement args) => cmd switch
    {
        "init" => await Init(),
        "measureUsers" => MeasureUsers(),
        "planApps" => await PlanApps(),
        "checkUpdate" => await CheckUpdate(),
        "listDisks" => await Task.Run(ListDisks),
        "planClone" => await Task.Run(() => PlanClone(Int(args, "from", -1), Int(args, "to", -1)).View),
        "shrinkSource" => await Task.Run(() => ShrinkSource(Int(args, "from", -1), Int(args, "to", -1))),
        "clone" => await Clone(Int(args, "from", -1), Int(args, "to", -1)),
        "installUpdate" => await InstallUpdate(),
        "listDrives" => await Task.Run(ListDrives),
        "unlockDrive" => await Task.Run(() => UnlockDrive(Str(args, "root"), Str(args, "key"))),
        "useDrive" => await UseDrive(Str(args, "root")),
        "useThisPc" => UseThisPc(),
        "captureHere" => await Capture(args, LocalStore("from-drive"), toNetwork: false),
        "startDiscovery" => StartDiscovery(),
        "stopDiscovery" => StopDiscovery(),
        "pickFolder" => PickFolder(Str(args, "purpose")),
        "capture" => await Capture(args, Str(args, "folder"), toNetwork: false),
        "send" => await Capture(args, Str(args, "address"), toNetwork: true),
        "startReceive" => StartReceive(),
        "readStore" => ReadStore(Str(args, "folder")),
        "apply" => await Apply(args),
        "cancel" => Cancel(),
        "openPath" => OpenPath(Str(args, "path")),
        "close" => CloseWindow(),
        _ => throw new InvalidOperationException($"Unknown command {cmd}"),
    };

    private async Task<object> Init()
    {
        _inventory = await Task.Run(_probe.GetInventory);
        return new
        {
            computerName = _inventory.Machine.ComputerName,
            version = ToolInfo.Version,
            appCount = _inventory.Apps.Count,
            users = _inventory.Users.Select(u => new { name = u.UserName, path = u.ProfilePath }),
        };
    }

    private static List<object> ListDisks() =>
        Disks.List().Select(d => (object)new
        {
            number = d.Number, model = d.Model, serial = d.Serial, size = d.Size, bus = d.Bus, isSystem = d.IsSystem,
            used = d.Partitions.Sum(p => p.UsedBytes ?? 0),
            letters = d.Partitions.Where(p => p.DriveLetter is not null).Select(p => p.DriveLetter + ":"),
            hasWindows = d.Partitions.Any(p => p.DriveLetter is { } l && Directory.Exists(l + @":\Windows\System32")),
        }).ToList();

    private static (ClonePlan Plan, object View) PlanClone(int from, int to)
    {
        var disks = Disks.List();
        var source = disks.FirstOrDefault(d => d.Number == from) ?? throw new InvalidOperationException("Pick the drive to copy from.");
        var target = disks.FirstOrDefault(d => d.Number == to) ?? throw new InvalidOperationException("Pick the drive to copy to.");
        var plan = ClonePlanner.Plan(source, target);
        static string Label(PartitionInfo p) =>
            p.IsEfi ? "Boot (EFI)" : p.IsMsr ? "Reserved" : p.IsRecovery ? "Recovery" : p.DriveLetter is { } d ? $"{d}: {p.FileSystem}" : p.FileSystem ?? "Data";
        return (plan, new
        {
            canRun = plan.CanRun,
            problem = plan.Problem,
            shrinkBy = plan.ShrinkSourceBy,
            shrinkLetter = plan.ShrinkSourceBy > 0 ? plan.Main?.DriveLetter : null,
            bytesToCopy = plan.CanRun ? plan.BytesToCopy : 0,
            source = new { number = source.Number, model = source.Model, size = source.Size },
            target = new { number = target.Number, model = target.Model, size = target.Size, serial = target.Serial },
            partitions = plan.Partitions.Select(p => new { label = Label(p.Source), from = p.Source.Length, to = p.Length, grows = p.Grows }),
        });
    }

    private static object ShrinkSource(int from, int to)
    {
        var (plan, _) = PlanClone(from, to);
        if (plan.ShrinkSourceBy <= 0 || plan.Main?.DriveLetter is not { } letter) throw new InvalidOperationException("Nothing to shrink.");
        DiskCloner.ShrinkVolume(letter, plan.ShrinkSourceBy);
        return PlanClone(from, to).View;
    }

    private async Task<object> Clone(int from, int to)
    {
        var (plan, _) = await Task.Run(() => PlanClone(from, to));
        if (!plan.CanRun) throw new InvalidOperationException(plan.Problem);
        var ct = BeginWork();
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pc-migrate", $"clone-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        using var log = new GuiLog(Post, logPath) { OnProgress = (_, bytes) => _notify.Progress(bytes, plan.BytesToCopy) };
        try
        {
            var result = await Task.Run(() => new DiskCloner(log).RunAsync(plan, ct), ct);
            foreach (var note in result.Notes) log.Warn(note);
            _notify.Finished("Clone finished", $"{CaptureEngine.FormatBytes(result.BytesCopied)} copied to {plan.Target.Model} in {(int)result.Elapsed.TotalMinutes} min.", logPath);
            return new { bytes = result.BytesCopied, seconds = result.Elapsed.TotalSeconds, notes = result.Notes, logPath };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notify.Failed("Clone stopped", Friendly(ex), logPath);
            throw;
        }
        finally
        {
            EndWork();
        }
    }

    private UpdateInfo? _update;
    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    private async Task<object?> CheckUpdate()
    {
        // Now that the app has started properly, the version an update replaced can go.
        Updater.CleanUp(ExePath);
        _update = await Updater.CheckAsync("pc-migrate.exe", ToolInfo.FullVersion);
        if (_update is null) return null;
        return new { version = _update.Tag.TrimStart('v'), notes = _update.Notes, page = _update.PageUrl, sizeBytes = _update.Size };
    }

    /// <summary>Downloads the new version, swaps it in, and restarts into it.</summary>
    private async Task<object?> InstallUpdate()
    {
        if (_update is null) throw new InvalidOperationException("There's no update to install.");
        var ct = BeginWork();
        try
        {
            var progress = new Progress<double>(f => Post("updateProgress", new { fraction = f }));
            var downloaded = await Updater.DownloadAsync(_update, ExePath, progress, ct);
            try
            {
                Updater.Swap(ExePath, downloaded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Couldn't replace pc-migrate.exe ({ex.Message}). Download the new version from GitHub instead.");
            }
        }
        finally
        {
            EndWork();
        }
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });
        form.BeginInvoke(form.Close);
        return null;
    }

    private ISystemProbe SourceProbe => (ISystemProbe?)_pulledProbe ?? _probe;
    private IAppPlatform SourceApps => (IAppPlatform?)_pulledApps ?? _apps;
    private Inventory? SourceInventory => _pulled is null ? _inventory : _pulledInventory;

    private static List<object> ListDrives() =>
        OfflineWindows.Find().Select(d => (object)new
        {
            root = d.Root, label = d.Label, locked = d.Locked, computerName = d.ComputerName,
            usedBytes = d.TotalBytes - d.FreeBytes, totalBytes = d.TotalBytes, users = d.Users,
        }).ToList();

    private static object UnlockDrive(string root, string key)
    {
        if (BitLocker.Unlock(root, key) is { } problem) throw new InvalidOperationException(problem);
        return new { ok = true };
    }

    /// <summary>Switches the source to a pulled drive. Everything on the old-PC side then reads from it.</summary>
    private async Task<object> UseDrive(string root)
    {
        UseThisPc();
        var drive = await Task.Run(() => OfflineWindows.Open(root));
        _pulled = drive;
        _pulledProbe = new OfflineSystemProbe(drive, NullLog.Instance);
        _pulledApps = new OfflineAppPlatform(drive, NullLog.Instance);
        _pulledInventory = await Task.Run(_pulledProbe.GetInventory);
        return new
        {
            root,
            computerName = drive.ComputerName,
            os = drive.OsDescription,
            appCount = _pulledInventory.Apps.Count,
            users = _pulledInventory.Users.Select(u => new { name = u.UserName, path = drive.Paths.ToReadable(u.ProfilePath) }),
        };
    }

    private object? UseThisPc()
    {
        _measuring?.Cancel();
        _planning = null;
        _pulledInventory = null;
        _pulledProbe = null;
        _pulledApps = null;
        _pulled?.Dispose();
        _pulled = null;
        return null;
    }

    private static string LocalStore(string kind) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pc-migrate", $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}");

    private object? MeasureUsers()
    {
        _measuring?.Cancel();
        _measuring = new CancellationTokenSource();
        var ct = _measuring.Token;
        var users = SourceInventory?.Users ?? [];
        var offline = SourceProbe.Offline;
        // One user at a time in the background; each result shows up on the page as it comes in.
        _ = Task.Run(() =>
        {
            foreach (var user in users)
            {
                if (ct.IsCancellationRequested) return;
                var (files, bytes) = CaptureEngine.Estimate(user, new CaptureRules(), ct, offline);
                Post("userMeasured", new { name = user.UserName, files, bytes });
            }
        }, ct);
        return null;
    }

    /// <summary>Works out once, in the background, how each program would move (winget takes a few seconds).</summary>
    private async Task<object> PlanApps()
    {
        var inventory = SourceInventory ?? await Task.Run(SourceProbe.GetInventory);
        var platform = SourceApps;
        _planning ??= Task.Run(() => AppPlanner.Plan(inventory, platform, inventory.Users, new CaptureRules()));
        var plans = await _planning;
        return plans.Select(p => new { key = p.Key, name = p.Name, version = p.Version, publisher = p.Publisher, method = p.Method.ToString(), reason = p.Reason });
    }

    private object? StartDiscovery()
    {
        StopDiscovery();
        _discovery = new CancellationTokenSource();
        var ct = _discovery.Token;
        _ = Task.Run(() => Discovery.ScanAsync(r => Post("receiverFound", new { name = r.Name, address = r.Address, port = r.Port }), ct), ct);
        return null;
    }

    private object? StopDiscovery()
    {
        _discovery?.Cancel();
        _discovery = null;
        return null;
    }

    private object? PickFolder(string purpose)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = purpose == "save" ? "Choose where to save (an empty folder on your external drive)" : "Choose the folder the old PC saved to",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = purpose == "save",
        };
        if (dialog.ShowDialog(form) != DialogResult.OK) return null;
        var path = dialog.SelectedPath;
        var hasStore = File.Exists(Path.Combine(path, Manifest.FileName));

        if (purpose == "save")
        {
            long? free = null;
            try { free = new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException) { }
            return new { path, freeBytes = free, hasStore };
        }

        string? fromComputer = null;
        if (hasStore)
        {
            try { fromComputer = FolderSink.ReadManifest(path).Inventory.Machine.ComputerName; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { hasStore = false; }
        }
        return new { path, hasStore, fromComputer };
    }

    private async Task<object> Capture(JsonElement args, string destination, bool toNetwork)
    {
        StopDiscovery();
        var appKeys = args.TryGetProperty("apps", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(k => k.GetString()!).ToHashSet()
            : [];
        var plans = _planning is { IsCompletedSuccessfully: true } ? _planning.Result : [];
        var options = new CaptureOptions
        {
            // Programs the person can't move are still recorded, so the new PC can list them.
            Apps = plans.Where(p => appKeys.Contains(p.Key) || p.Method == AppMethod.Manual).ToList(),
            Users = args.GetProperty("users").EnumerateArray().Select(u => u.GetString()!).ToList(),
            IncludeWifi = args.GetProperty("wifi").GetBoolean(),
            IncludeUserSettings = args.GetProperty("settings").GetBoolean(),
            UseShadowCopy = args.GetProperty("shadow").GetBoolean() && _pulled is null,
        };
        var estimate = args.TryGetProperty("estimatedBytes", out var e) && e.ValueKind == JsonValueKind.Number ? (long)e.GetDouble() : 0;
        var logPath = toNetwork ? null : Path.Combine(destination, $"pc-migrate-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var ct = BeginWork();
        using var log = new GuiLog(Post, logPath) { OnProgress = (_, bytes) => _notify.Progress(bytes, estimate) };
        var timer = Stopwatch.StartNew();
        var (probe, apps) = (SourceProbe, SourceApps);
        try
        {
            Manifest manifest;
            if (toNetwork)
            {
                log.Info($"Connecting to {destination}...");
                await using var sink = await NetworkSender.ConnectAsync(destination, Int(args, "port", NetworkReceiver.DefaultPort), Str(args, "code"), ct, estimate);
                manifest = await Task.Run(() => new CaptureEngine(probe, log, apps).RunAsync(sink, options, ct), ct);
            }
            else
            {
                await using var sink = new FolderSink(destination);
                manifest = await Task.Run(() => new CaptureEngine(probe, log, apps).RunAsync(sink, options, ct), ct);
            }
            var bytes = manifest.Files.Sum(f => f.Length);
            _notify.Finished(toNetwork ? "Sent to the new PC" : "Backup finished",
                Summary(manifest.Files.Count, bytes, timer.Elapsed, log.Warnings.Count), logPath);
            return new
            {
                files = manifest.Files.Count,
                bytes,
                seconds = timer.Elapsed.TotalSeconds,
                warnings = log.Warnings,
                logPath,
                folder = toNetwork ? null : destination,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notify.Failed(toNetwork ? "Sending stopped" : "Backup stopped", Friendly(ex), logPath);
            throw;
        }
        finally
        {
            EndWork();
        }
    }

    private object StartReceive()
    {
        var ct = BeginWork();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pc-migrate", $"received-{DateTime.Now:yyyyMMdd-HHmmss}");
        var code = NetworkReceiver.NewPairingCode();
        var log = new GuiLog(Post);
        var receiver = new NetworkReceiver(folder, code, log);
        int port;
        try
        {
            port = receiver.Start();
        }
        catch (System.Net.Sockets.SocketException)
        {
            receiver.Dispose();
            EndWork();
            throw new InvalidOperationException("pc-migrate is already waiting in another window. Close it and try again.");
        }
        receiver.Connected += (name, estimate) => Post("senderConnected", new { name, estimatedBytes = estimate });

        _ = Task.Run(async () =>
        {
            using var firewall = FirewallRule.Open(port, log);
            using var stopDiscovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var discovery = Discovery.RespondAsync(port, stopDiscovery.Token);
            try
            {
                await receiver.ReceiveAsync(ct);
                stopDiscovery.Cancel();
                _notify.Finished("Everything arrived", "The old PC has finished sending. Check what to put where, then start.");
                Post("received", new { folder });
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException) _notify.Failed("Receiving stopped", Friendly(ex));
                Post("failed", new { message = ex is OperationCanceledException ? "Cancelled." : Friendly(ex) });
            }
            finally
            {
                stopDiscovery.Cancel();
                await discovery;
                receiver.Dispose();
                log.Dispose();
                EndWork();
            }
        });

        return new { code, addresses = NetworkReceiver.LocalAddresses() };
    }

    private object ReadStore(string folder)
    {
        var manifest = FolderSink.ReadManifest(folder);
        var users = manifest.Files
            .Select(f => StorePath.TryParseUserFile(f.StorePath, out var user, out _) ? (user, f.Length) : (user: (string?)null, f.Length))
            .Where(x => x.user is not null)
            .GroupBy(x => x.user!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { name = g.Key, files = g.Count(), bytes = g.Sum(x => x.Length) })
            .ToList();
        var here = (_inventory ?? _probe.GetInventory());
        var installed = here.Apps.Select(x => AppNames.Normalize(x.Name)).ToHashSet();
        return new
        {
            apps = manifest.Apps.Select(p => new
            {
                key = p.Key, name = p.Name, publisher = p.Publisher, method = p.Method.ToString(), reason = p.Reason,
                alreadyInstalled = installed.Contains(AppNames.Normalize(p.Name)),
            }),
            fromComputer = manifest.Inventory.Machine.ComputerName,
            captured = manifest.Inventory.Machine.CapturedUtc,
            files = manifest.Files.Count,
            bytes = manifest.Files.Sum(f => f.Length),
            users,
            targetUsers = here.Users.Select(u => u.UserName),
        };
    }

    private async Task<object> Apply(JsonElement args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in args.GetProperty("map").EnumerateObject())
        {
            var target = pair.Value.GetString();
            if (string.IsNullOrEmpty(target)) skip.Add(pair.Name);
            else map[pair.Name] = target;
        }
        var options = new ApplyOptions
        {
            AppKeys = args.TryGetProperty("apps", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(k => k.GetString()!).ToHashSet()
                : null,
            UserMap = map,
            SkipUsers = skip,
            Conflicts = Str(args, "conflicts") switch { "overwrite" => ConflictMode.Overwrite, "skip" => ConflictMode.Skip, _ => ConflictMode.NewerWins },
            MoveFromStore = args.TryGetProperty("move", out var mv) && mv.ValueKind == JsonValueKind.True,
        };

        var folder = Str(args, "folder");
        long total = 0;
        try { total = FolderSink.ReadManifest(folder).Files.Sum(f => f.Length); } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
        var ct = BeginWork();
        using var log = new GuiLog(Post) { OnProgress = (_, bytes) => _notify.Progress(bytes, total) };
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() => new ApplyEngine(_probe, log, _apps).RunAsync(folder, options, ct), ct);
            _notify.Finished("All moved over", Summary(result.FilesCopied, result.BytesCopied, timer.Elapsed, result.Warnings.Count), result.ReportPath);
            return new
            {
                copied = result.FilesCopied,
                skipped = result.FilesSkipped,
                failed = result.FilesFailed,
                bytes = result.BytesCopied,
                warnings = result.Warnings,
                reportPath = result.ReportPath,
                programs = result.Apps
                    .Where(x => x.Outcome is not (AppOutcome.Manual or AppOutcome.NotSelected))
                    .Select(x => new { name = x.Name, outcome = x.Outcome.ToString(), message = x.Message }),
                apps = result.AppsToReinstall.Select(x => new
                {
                    name = x.Name, publisher = x.Publisher, version = x.Version,
                    reason = result.Apps.FirstOrDefault(r => r.Name == x.Name)?.Message,
                }),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notify.Failed("Moving stopped", Friendly(ex));
            throw;
        }
        finally
        {
            EndWork();
        }
    }

    private object? Cancel()
    {
        _work?.Cancel();
        return null;
    }

    private static object? OpenPath(string path)
    {
        // Release notes; only the project's own GitHub pages are opened in the browser.
        if (path.StartsWith($"https://github.com/{Updater.Repo}/", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\""));
        return null;
    }

    private object? CloseWindow()
    {
        form.BeginInvoke(form.Close);
        return null;
    }

    private CancellationToken BeginWork()
    {
        if (IsBusy) throw new InvalidOperationException("Something is already running.");
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    private void EndWork() => _work = null;

    private void Post(string name, object data)
    {
        var payload = JsonSerializer.SerializeToElement(data, Json.Options);
        var message = new Dictionary<string, object?> { ["event"] = name };
        foreach (var p in payload.EnumerateObject()) message[p.Name] = p.Value;
        Send(message);
    }

    private void Reply(object message) => Send(message);

    private void Send(object message)
    {
        var json = JsonSerializer.Serialize(message, Json.Options);
        if (form.IsDisposed) return;
        if (form.InvokeRequired) form.BeginInvoke(() => web.PostWebMessageAsJson(json));
        else web.PostWebMessageAsJson(json);
    }

    private static string Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int Int(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    /// <summary>Error text a person can act on, rather than an exception dump.</summary>
    private static string Friendly(Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused or System.Net.Sockets.SocketError.TimedOut } =>
            "Couldn't reach the new PC. Check it's still showing the pairing code and that both PCs are on the same network.",
        System.Net.Sockets.SocketException s => $"Network problem: {s.Message}",
        _ => ex.Message,
    };

    private static string Summary(long files, long bytes, TimeSpan time, int warnings)
    {
        var took = time.TotalMinutes < 1 ? $"{time.Seconds} s" : time.TotalHours < 1 ? $"{(int)time.TotalMinutes} min" : $"{(int)time.TotalHours} h {time.Minutes} min";
        var text = $"{files:N0} files, {CaptureEngine.FormatBytes(bytes)} in {took}.";
        return warnings == 0 ? text : $"{text} {warnings:N0} item(s) couldn't be copied; click to see which.";
    }

    public void Dispose()
    {
        _work?.Cancel();
        _discovery?.Cancel();
        _measuring?.Cancel();
        _pulled?.Dispose();
        _notify.Dispose();
    }
}
