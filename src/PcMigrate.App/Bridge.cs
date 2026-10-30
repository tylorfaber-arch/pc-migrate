using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using PcMigrate.Core;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;
using PcMigrate.Core.Transfer;
using PcMigrate.Core.Windows;

namespace PcMigrate.App;

/// <summary>
/// Answers the page. The page sends {id, cmd, args}; each command replies with
/// {id, result} or {id, error}. Ongoing work is reported as {event, ...} messages.
/// </summary>
internal sealed class Bridge(Form form, CoreWebView2 web) : IDisposable
{
    private readonly WindowsSystemProbe _probe = new(NullLog.Instance);
    private Inventory? _inventory;
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

    private object? MeasureUsers()
    {
        _measuring?.Cancel();
        _measuring = new CancellationTokenSource();
        var ct = _measuring.Token;
        var users = _inventory?.Users ?? [];
        // One user at a time in the background; each result shows up on the page as it comes in.
        _ = Task.Run(() =>
        {
            foreach (var user in users)
            {
                if (ct.IsCancellationRequested) return;
                var (files, bytes) = CaptureEngine.Estimate(user, new CaptureRules(), ct);
                Post("userMeasured", new { name = user.UserName, files, bytes });
            }
        }, ct);
        return null;
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
        var options = new CaptureOptions
        {
            Users = args.GetProperty("users").EnumerateArray().Select(u => u.GetString()!).ToList(),
            IncludeWifi = args.GetProperty("wifi").GetBoolean(),
            IncludeUserSettings = args.GetProperty("settings").GetBoolean(),
            UseShadowCopy = args.GetProperty("shadow").GetBoolean(),
        };
        var estimate = args.TryGetProperty("estimatedBytes", out var e) && e.ValueKind == JsonValueKind.Number ? (long)e.GetDouble() : 0;
        var logPath = toNetwork ? null : Path.Combine(destination, $"pc-migrate-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var ct = BeginWork();
        using var log = new GuiLog(Post, logPath);
        var timer = Stopwatch.StartNew();
        try
        {
            Manifest manifest;
            if (toNetwork)
            {
                log.Info($"Connecting to {destination}...");
                await using var sink = await NetworkSender.ConnectAsync(destination, Int(args, "port", NetworkReceiver.DefaultPort), Str(args, "code"), ct, estimate);
                manifest = await Task.Run(() => new CaptureEngine(_probe, log).RunAsync(sink, options, ct), ct);
            }
            else
            {
                await using var sink = new FolderSink(destination);
                manifest = await Task.Run(() => new CaptureEngine(_probe, log).RunAsync(sink, options, ct), ct);
            }
            return new
            {
                files = manifest.Files.Count,
                bytes = manifest.Files.Sum(f => f.Length),
                seconds = timer.Elapsed.TotalSeconds,
                warnings = log.Warnings,
                logPath,
            };
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
                Post("received", new { folder });
            }
            catch (Exception ex)
            {
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
        return new
        {
            fromComputer = manifest.Inventory.Machine.ComputerName,
            captured = manifest.Inventory.Machine.CapturedUtc,
            files = manifest.Files.Count,
            bytes = manifest.Files.Sum(f => f.Length),
            users,
            targetUsers = (_inventory ?? _probe.GetInventory()).Users.Select(u => u.UserName),
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
            UserMap = map,
            SkipUsers = skip,
            Conflicts = Str(args, "conflicts") switch { "overwrite" => ConflictMode.Overwrite, "skip" => ConflictMode.Skip, _ => ConflictMode.NewerWins },
        };

        var ct = BeginWork();
        using var log = new GuiLog(Post);
        try
        {
            var result = await Task.Run(() => new ApplyEngine(_probe, log).RunAsync(Str(args, "folder"), options, ct), ct);
            return new
            {
                copied = result.FilesCopied,
                skipped = result.FilesSkipped,
                failed = result.FilesFailed,
                bytes = result.BytesCopied,
                warnings = result.Warnings,
                reportPath = result.ReportPath,
                apps = result.AppsToReinstall.Select(a => new { name = a.Name, publisher = a.Publisher, version = a.Version }),
            };
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

    public void Dispose()
    {
        _work?.Cancel();
        _discovery?.Cancel();
        _measuring?.Cancel();
    }
}
