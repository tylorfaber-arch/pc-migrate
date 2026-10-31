using System.Security.Principal;
using PcMigrate.Cli;
using PcMigrate.Core;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Models;
using PcMigrate.Core.Storage;
using PcMigrate.Core.Transfer;
using PcMigrate.Core.Windows;

// Double-clicked in Explorer: there are no arguments, so ask what to do instead of
// printing help into a window that closes immediately, and keep the window open at the end.
var interactive = args.Length == 0;
if (interactive)
{
    var chosen = Menu.Ask();
    if (chosen is null) return 0;
    args = chosen;
}

var exitCode = await Run(args);
if (interactive) Menu.WaitForEnter();
return exitCode;

async Task<int> Run(string[] argv)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("Stopping... (shadow copies and temporary files are being cleaned up)");
        cts.Cancel();
    };

    Args cli;
    try
    {
        cli = new Args(argv);
    }
    catch (ArgumentException ex)
    {
        return Fail(ex.Message);
    }

    if (cli.Command is "help" or "--help" or "-h" or "/?") { PrintHelp(); return 0; }
    if (cli.Command is "version" or "--version") { Console.WriteLine($"pc-migrate {ToolInfo.Version}"); return 0; }

    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        return Fail("pc-migrate needs to run as administrator. Right-click it and choose 'Run as administrator'.");

    using var log = new ConsoleLog(cli.Get("log"));
    ISystemProbe probe = new WindowsSystemProbe(log);
    IAppPlatform appPlatform = new WindowsAppPlatform(log);
    OfflineWindows? pulled = null;

    try
    {
        // --from-drive E: reads a drive pulled from a dead PC instead of this PC's own Windows.
        if (cli.Get("from-drive") is { } driveArg)
        {
            if (cli.Command is not ("scan" or "capture" or "send"))
                return Fail("--from-drive works with scan, capture and send.");
            var root = driveArg.TrimEnd('\\', '/', ':')[..1] + ":\\";
            if (cli.Get("recovery-key") is { } key && BitLocker.IsLocked(root))
            {
                if (BitLocker.Unlock(root, key) is { } problem) return Fail(problem);
                log.Info($"Unlocked {root}.");
            }
            else if (BitLocker.IsLocked(root))
            {
                return Fail($"{root} is locked with BitLocker. Add --recovery-key <48 digits> (find it at aka.ms/myrecoverykey).");
            }
            log.Info($"Opening the Windows on {root}...");
            pulled = OfflineWindows.Open(root);
            probe = new OfflineSystemProbe(pulled, log);
            appPlatform = new OfflineAppPlatform(pulled, log);
            log.Info($"Found {pulled.ComputerName} ({pulled.OsDescription}).");
        }

        return cli.Command switch
        {
            "drives" => Drives(),
            "scan" => Scan(),
            "capture" => await Capture(),
            "send" => await Send(),
            "receive" => await Receive(),
            "apply" => await Apply(cli.Require("from")),
            _ => Fail($"Unknown command '{cli.Command}'. Run 'pcmigrate-cli help'."),
        };
    }
    catch (OperationCanceledException)
    {
        return Fail("Cancelled.");
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException
                                   or FileNotFoundException or SinkWriteException or System.Net.Sockets.SocketException or IOException)
    {
        return Fail(ex.Message);
    }
    finally
    {
        pulled?.Dispose();
    }

    int Drives()
    {
        cli.RejectUnknown();
        var drives = OfflineWindows.Find();
        if (drives.Count == 0)
        {
            Console.WriteLine("No plugged-in drives with Windows on them. Plug the old PC's drive in with a USB adapter and try again.");
            return 0;
        }
        Console.WriteLine("Drives from other PCs:");
        foreach (var d in drives)
        {
            if (d.Locked) Console.WriteLine($"  {d.Root,-5} locked with BitLocker (use --recovery-key)");
            else Console.WriteLine($"  {d.Root,-5} {d.ComputerName ?? "unknown PC",-20} {CaptureEngine.FormatBytes(d.TotalBytes - d.FreeBytes)} used   users: {string.Join(", ", d.Users)}");
        }
        Console.WriteLine();
        Console.WriteLine("Then run, for example:  pcmigrate-cli capture --from-drive E: --to F:\\migration");
        return 0;
    }

    int Scan()
    {
        var asJson = cli.Flag("json");
        cli.RejectUnknown();
        var inventory = probe.GetInventory();
        if (asJson)
        {
            Console.WriteLine(Json.Serialize(inventory));
            return 0;
        }

        var rules = new CaptureRules();
        Console.WriteLine($"{inventory.Machine.ComputerName}  ({inventory.Machine.OsDescription})");
        Console.WriteLine();
        Console.WriteLine("Users and how much would be copied:");
        foreach (var user in inventory.Users)
        {
            var (files, bytes) = CaptureEngine.Estimate(user, rules, default, probe.Offline);
            Console.WriteLine($"  {user.UserName,-24} {files,10:N0} files  {CaptureEngine.FormatBytes(bytes),10}   {user.ProfilePath}");
        }
        Console.WriteLine();
        Console.WriteLine("Checking which programs can move...");
        var plans = AppPlanner.Plan(inventory, appPlatform, inventory.Users, rules);
        foreach (var group in plans.GroupBy(p => p.Method))
        {
            Console.WriteLine();
            Console.WriteLine(group.Key switch
            {
                AppMethod.Reinstall => $"Reinstalled automatically, with settings ({group.Count()}):",
                AppMethod.Copy => $"Copied as they are ({group.Count()}):",
                _ => $"Need installing by hand ({group.Count()}):",
            });
            foreach (var app in group)
                Console.WriteLine($"  {app.Name}{(app.Version is null ? "" : "  " + app.Version)}{(group.Key == AppMethod.Manual ? "  - " + app.Reason : "")}");
        }
        return 0;
    }

    CaptureOptions ReadCaptureOptions()
    {
        var users = cli.GetAll("user").SelectMany(u => u.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        IReadOnlyList<AppEntry> apps = [];
        if (!cli.Flag("no-apps"))
        {
            log.Info("Checking which programs can move...");
            var inventory = probe.GetInventory();
            var selected = users.Count == 0 ? inventory.Users : inventory.Users.Where(u => users.Contains(u.UserName, StringComparer.OrdinalIgnoreCase)).ToList();
            apps = AppPlanner.Plan(inventory, appPlatform, selected, new CaptureRules());
            log.Info($"{apps.Count(a => a.Method == AppMethod.Reinstall)} program(s) will be reinstalled, {apps.Count(a => a.Method == AppMethod.Copy)} copied, {apps.Count(a => a.Method == AppMethod.Manual)} need installing by hand.");
        }
        return new CaptureOptions
        {
            Users = users,
            UseShadowCopy = !cli.Flag("no-shadow"),
            IncludeWifi = !cli.Flag("no-wifi"),
            IncludeUserSettings = !cli.Flag("no-settings"),
            Apps = apps,
        };
    }

    async Task<int> Capture()
    {
        var to = cli.Require("to");
        var options = ReadCaptureOptions();
        cli.RejectUnknown();

        if (File.Exists(Path.Combine(to, Manifest.FileName)))
            return Fail($"'{to}' already holds a finished capture. Pick an empty folder.");

        await using var sink = new FolderSink(to);
        var manifest = await new CaptureEngine(probe, log, appPlatform).RunAsync(sink, options, cts.Token);
        return Done($"Capture saved to {sink.Root}. Plug the drive into the new PC and run: pcmigrate-cli apply --from \"{sink.Root}\"", manifest.Warnings.Count);
    }

    async Task<int> Send()
    {
        var host = cli.Require("host");
        var code = cli.Require("code");
        var port = cli.GetInt("port", NetworkReceiver.DefaultPort);
        var options = ReadCaptureOptions();
        cli.RejectUnknown();

        log.Info($"Connecting to {host}:{port}...");
        await using var sink = await NetworkSender.ConnectAsync(host, port, code, cts.Token);
        var manifest = await new CaptureEngine(probe, log, appPlatform).RunAsync(sink, options, cts.Token);
        return Done("Everything was sent. Finish up on the new PC.", manifest.Warnings.Count);
    }

    async Task<int> Receive()
    {
        var to = cli.Get("to") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pc-migrate", $"store-{DateTime.Now:yyyyMMdd-HHmmss}");
        var port = cli.GetInt("port", NetworkReceiver.DefaultPort);
        var applyAfter = !cli.Flag("no-apply");
        var applyOptions = ReadApplyOptions();
        cli.RejectUnknown();

        var code = NetworkReceiver.NewPairingCode();
        using var receiver = new NetworkReceiver(to, code, log);
        receiver.Start(port);
        using var firewall = FirewallRule.Open(port, log);
        using var discoveryStop = new CancellationTokenSource();
        var discovery = Discovery.RespondAsync(port, discoveryStop.Token);

        Console.WriteLine();
        Console.WriteLine("Waiting for the old PC. On it, run:");
        foreach (var ip in NetworkReceiver.LocalAddresses())
            Console.WriteLine($"    pcmigrate-cli send --host {ip} --code {code}{(port == NetworkReceiver.DefaultPort ? "" : " --port " + port)}");
        Console.WriteLine();
        Console.WriteLine($"Pairing code: {code}");
        Console.WriteLine();

        await receiver.ReceiveAsync(cts.Token);
        discoveryStop.Cancel();
        await discovery;
        log.Info($"Transfer finished. Saved to {to}");
        return applyAfter ? await Apply(to, applyOptions) : Done($"Run 'pcmigrate-cli apply --from \"{to}\"' when ready.", 0);
    }

    ApplyOptions ReadApplyOptions()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in cli.GetAll("map"))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                throw new ArgumentException($"--map expects olduser=newuser, got '{pair}'.");
            map[parts[0]] = parts[1];
        }
        var conflicts = cli.Get("conflicts")?.ToLowerInvariant() switch
        {
            null or "newer" => ConflictMode.NewerWins,
            "overwrite" => ConflictMode.Overwrite,
            "skip" => ConflictMode.Skip,
            var other => throw new ArgumentException($"--conflicts must be newer, overwrite or skip (got '{other}')."),
        };
        return new ApplyOptions
        {
            UserMap = map,
            Conflicts = conflicts,
            DryRun = cli.Flag("dry-run"),
            IncludeWifi = !cli.Flag("no-wifi"),
            IncludeUserSettings = !cli.Flag("no-settings"),
            IncludeApps = !cli.Flag("no-apps"),
        };
    }

    async Task<int> Apply(string from, ApplyOptions? options = null)
    {
        if (options is null)
        {
            options = ReadApplyOptions();
            cli.RejectUnknown();
        }
        log.Info("Tip: close web browsers and Outlook first so their data can be replaced.");
        var result = await new ApplyEngine(probe, log, appPlatform).RunAsync(from, options, cts.Token);

        Console.WriteLine();
        Console.WriteLine($"Copied {result.FilesCopied:N0} files ({CaptureEngine.FormatBytes(result.BytesCopied)}), skipped {result.FilesSkipped:N0}, failed {result.FilesFailed:N0}.");
        foreach (var app in result.Apps.Where(a => a.Outcome is not (AppOutcome.Manual or AppOutcome.NotSelected)))
            Console.WriteLine($"  {app.Outcome,-16} {app.Name}{(app.Outcome == AppOutcome.Failed ? ": " + app.Message : "")}");
        if (result.AppsToReinstall.Count > 0)
            Console.WriteLine($"{result.AppsToReinstall.Count} program(s) from the old PC still need installing by hand; they are listed in the report.");
        if (result.ReportPath is not null) Console.WriteLine($"Report: {result.ReportPath}");
        return Done(options.DryRun ? "Dry run only, nothing was changed." : "Done. Sign out and back in to see the moved settings.", result.Warnings.Count);
    }

    int Done(string message, int warnings)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
        if (warnings > 0) Console.WriteLine($"{warnings} warning(s) above{(cli.Get("log") is { } l ? $" and in {l}" : "")}.");
        return 0;
    }

    static int Fail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("Error: " + message);
        Console.ResetColor();
        return 1;
    }

    static void PrintHelp()
    {
        Console.WriteLine($"""
            pc-migrate {ToolInfo.Version}: move files and settings from an old Windows PC to a new one.

            Using an external drive:
              On the old PC:  pcmigrate-cli capture --to E:\migration
              On the new PC:  pcmigrate-cli apply --from E:\migration

            Over the network (both PCs on the same network):
              On the new PC:  pcmigrate-cli receive      (shows a pairing code)
              On the old PC:  pcmigrate-cli send --host <new PC's IP> --code <code>

            From a dead PC's drive, plugged in by USB:
              pcmigrate-cli drives         List plugged-in drives that have Windows on them.
              pcmigrate-cli capture --from-drive E: --to F:\migration
              pcmigrate-cli send --from-drive E: --host <new PC's IP> --code <code>
              --recovery-key <key>         Unlock a BitLocker drive first.
              (Wi-Fi passwords can't be read off a drive; the networks come over without them.)

            Other commands:
              pcmigrate-cli scan [--json]  List users, how much would be copied, and installed programs.
              pcmigrate-cli version

            Options for capture and send:
              --user <name>[,<name>]       Only these users (default: everyone).
              --no-shadow                  Do not take a snapshot; open files may be skipped.
              --no-wifi                    Do not move saved Wi-Fi networks.
              --no-settings                Do not move wallpaper and mapped drives.
              --no-apps                    Do not bring programs along.

            Options for apply and receive:
              --map old=new                Put old user 'old' into new user 'new'. Repeatable.
              --conflicts newer|overwrite|skip
                                           When a file already exists (default: newer wins).
              --dry-run                    Show what would happen without changing anything.
              --no-wifi, --no-settings, --no-apps
                                           As above.
              --to <folder>                (receive) Where to keep the received data.
              --no-apply                   (receive) Only receive; run apply later.
              --port <n>                   (send/receive) Network port, default {NetworkReceiver.DefaultPort}.

            Any command:
              --log <file>                 Also write messages to a log file.
            """);
    }
}
