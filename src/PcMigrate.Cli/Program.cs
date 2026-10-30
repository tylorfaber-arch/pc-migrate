using System.Security.Principal;
using PcMigrate.Cli;
using PcMigrate.Core;
using PcMigrate.Core.Apply;
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
    var probe = new WindowsSystemProbe(log);

    try
    {
        return cli.Command switch
        {
            "scan" => Scan(),
            "capture" => await Capture(),
            "send" => await Send(),
            "receive" => await Receive(),
            "apply" => await Apply(cli.Require("from")),
            _ => Fail($"Unknown command '{cli.Command}'. Run 'pcmigrate help'."),
        };
    }
    catch (OperationCanceledException)
    {
        return Fail("Cancelled.");
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException
                                   or FileNotFoundException or SinkWriteException or System.Net.Sockets.SocketException)
    {
        return Fail(ex.Message);
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
            long files = 0, bytes = 0;
            foreach (var folder in rules.ProfileFolders)
            {
                foreach (var f in FileCollector.Enumerate(Path.Combine(user.ProfilePath, folder), folder, rules, _ => { }))
                {
                    files++;
                    bytes += f.Length;
                }
            }
            Console.WriteLine($"  {user.UserName,-24} {files,10:N0} files  {CaptureEngine.FormatBytes(bytes),10}   {user.ProfilePath}");
        }
        Console.WriteLine();
        Console.WriteLine($"Installed programs ({inventory.Apps.Count}):");
        foreach (var app in inventory.Apps)
            Console.WriteLine($"  {app.Name}{(app.Version is null ? "" : "  " + app.Version)}{(app.Scope == "Machine" ? "" : "  (for " + app.Scope + ")")}");
        return 0;
    }

    CaptureOptions ReadCaptureOptions() => new()
    {
        Users = cli.GetAll("user").SelectMany(u => u.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(),
        UseShadowCopy = !cli.Flag("no-shadow"),
        IncludeWifi = !cli.Flag("no-wifi"),
        IncludeUserSettings = !cli.Flag("no-settings"),
    };

    async Task<int> Capture()
    {
        var to = cli.Require("to");
        var options = ReadCaptureOptions();
        cli.RejectUnknown();

        if (File.Exists(Path.Combine(to, Manifest.FileName)))
            return Fail($"'{to}' already holds a finished capture. Pick an empty folder.");

        await using var sink = new FolderSink(to);
        var manifest = await new CaptureEngine(probe, log).RunAsync(sink, options, cts.Token);
        return Done($"Capture saved to {sink.Root}. Plug the drive into the new PC and run: pcmigrate apply --from \"{sink.Root}\"", manifest.Warnings.Count);
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
        var manifest = await new CaptureEngine(probe, log).RunAsync(sink, options, cts.Token);
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

        Console.WriteLine();
        Console.WriteLine("Waiting for the old PC. On it, run:");
        foreach (var ip in NetworkReceiver.LocalAddresses())
            Console.WriteLine($"    pcmigrate send --host {ip} --code {code}{(port == NetworkReceiver.DefaultPort ? "" : " --port " + port)}");
        Console.WriteLine();
        Console.WriteLine($"Pairing code: {code}");
        Console.WriteLine();

        await receiver.ReceiveAsync(cts.Token);
        log.Info($"Transfer finished. Saved to {to}");
        return applyAfter ? await Apply(to, applyOptions) : Done($"Run 'pcmigrate apply --from \"{to}\"' when ready.", 0);
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
        var result = await new ApplyEngine(probe, log).RunAsync(from, options, cts.Token);

        Console.WriteLine();
        Console.WriteLine($"Copied {result.FilesCopied:N0} files ({CaptureEngine.FormatBytes(result.BytesCopied)}), skipped {result.FilesSkipped:N0}, failed {result.FilesFailed:N0}.");
        if (result.AppsToReinstall.Count > 0)
            Console.WriteLine($"{result.AppsToReinstall.Count} program(s) from the old PC are not installed here; they are listed in the report.");
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
              On the old PC:  pcmigrate capture --to E:\migration
              On the new PC:  pcmigrate apply --from E:\migration

            Over the network (both PCs on the same network):
              On the new PC:  pcmigrate receive          (shows a pairing code)
              On the old PC:  pcmigrate send --host <new PC's IP> --code <code>

            Other commands:
              pcmigrate scan [--json]      List users, how much would be copied, and installed programs.
              pcmigrate version

            Options for capture and send:
              --user <name>[,<name>]       Only these users (default: everyone).
              --no-shadow                  Do not take a snapshot; open files may be skipped.
              --no-wifi                    Do not move saved Wi-Fi networks.
              --no-settings                Do not move wallpaper and mapped drives.

            Options for apply and receive:
              --map old=new                Put old user 'old' into new user 'new'. Repeatable.
              --conflicts newer|overwrite|skip
                                           When a file already exists (default: newer wins).
              --dry-run                    Show what would happen without changing anything.
              --no-wifi, --no-settings     As above.
              --to <folder>                (receive) Where to keep the received data.
              --no-apply                   (receive) Only receive; run apply later.
              --port <n>                   (send/receive) Network port, default {NetworkReceiver.DefaultPort}.

            Any command:
              --log <file>                 Also write messages to a log file.
            """);
    }
}
