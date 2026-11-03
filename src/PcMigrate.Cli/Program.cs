using System.Security.Principal;
using PcMigrate.Cli;
using PcMigrate.Core;
using PcMigrate.Core.Apply;
using PcMigrate.Core.Apps;
using PcMigrate.Core.Capture;
using PcMigrate.Core.Clone;
using PcMigrate.Core.Models;
using PcMigrate.Core.Report;
using PcMigrate.Core.Schedule;
using PcMigrate.Core.Storage;
using PcMigrate.Core.Transfer;
using PcMigrate.Core.Update;
using PcMigrate.Core.Windows;

// Double-clicked in Explorer: there are no arguments, so ask what to do instead of
// printing help into a window that closes immediately, and keep the window open at the end.
var interactive = args.Length == 0;
var exePath = Environment.ProcessPath!;
Updater.CleanUp(exePath);
if (interactive)
{
    // A quick look for a newer version, so people who double-click find out about it.
    using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try
    {
        if (await Updater.CheckAsync("pcmigrate-cli.exe", ToolInfo.FullVersion, quick.Token) is { } newer)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"pc-migrate {newer.Tag.TrimStart('v')} is out (you have {ToolInfo.Version}). Type U below to update.");
            Console.ResetColor();
            Console.WriteLine();
        }
    }
    catch (OperationCanceledException) { }

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
            if (cli.Command is not ("scan" or "capture" or "send" or "keys"))
                return Fail("--from-drive works with scan, capture, send and keys.");
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
            "keys" => Keys(),
            "health" => Health(),
            "disks" => ListDisks(),
            "clone" => await CloneDisk(),
            "image" => await MakeImage(),
            "restore-image" => await RestoreImage(),
            "rescue-usb" => await RescueUsb(),
            "backup" => await Backup(),
            "tech" => Technician(),
            "schedule" => await ScheduleBackup(),
            "update" => await SelfUpdate(),
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
                                   or FileNotFoundException or SinkWriteException or System.Net.Sockets.SocketException or IOException or HttpRequestException)
    {
        return Fail(ex.Message);
    }
    finally
    {
        pulled?.Dispose();
    }

    int ListDisks()
    {
        cli.RejectUnknown();
        foreach (var disk in Disks.List())
        {
            Console.WriteLine($"{disk.Describe()}{(disk.IsSystem ? "   <- this PC is running from it" : "")}");
            foreach (var p in disk.Partitions)
                Console.WriteLine($"    {p.Number}. {CaptureEngine.FormatBytes(p.Length),9}  {(p.DriveLetter is { } l ? l + ":" : "  ")} {p.FileSystem ?? ""}{(p.UsedBytes is { } u ? $" ({CaptureEngine.FormatBytes(u)} used)" : "")}{(p.IsEfi ? " EFI" : p.IsMsr ? " reserved" : p.IsRecovery ? " recovery" : "")}");
        }
        Console.WriteLine();
        Console.WriteLine("Clone with:  pcmigrate-cli clone --from <disk number> --to <disk number>");
        return 0;
    }

    async Task<int> CloneDisk()
    {
        var from = cli.GetInt("from", -1);
        var to = cli.GetInt("to", -1);
        var shrink = cli.Flag("shrink-source");
        var yes = cli.Flag("yes");
        cli.RejectUnknown();
        if (from < 0 || to < 0) return Fail("Say which disks: --from <number> --to <number>. 'pcmigrate-cli disks' lists them.");

        ClonePlan Plan()
        {
            var disks = Disks.List();
            var source = disks.FirstOrDefault(d => d.Number == from) ?? throw new ArgumentException($"There's no disk {from}.");
            var target = disks.FirstOrDefault(d => d.Number == to) ?? throw new ArgumentException($"There's no disk {to}.");
            return ClonePlanner.Plan(source, target);
        }

        var plan = Plan();
        if (!plan.CanRun && plan.ShrinkSourceBy > 0 && shrink && plan.Main?.DriveLetter is { } letter)
        {
            Console.WriteLine(plan.Problem);
            if (!yes)
            {
                Console.Write($"Shrink {letter}: now? Type yes to go ahead: ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)) return Fail("Not shrunk; nothing was changed.");
            }
            log.Info($"Shrinking {letter}: by {CaptureEngine.FormatBytes(plan.ShrinkSourceBy)} (Windows' own shrink)...");
            DiskCloner.ShrinkVolume(letter, plan.ShrinkSourceBy);
            plan = Plan();
        }
        if (!plan.CanRun)
            return Fail(plan.Problem + (plan.ShrinkSourceBy > 0 && plan.Main?.DriveLetter is not null ? " Add --shrink-source to do that now." : ""));

        Console.WriteLine($"From: {plan.Source.Describe()}");
        Console.WriteLine($"To:   {plan.Target.Describe()}");
        Console.WriteLine($"About {CaptureEngine.FormatBytes(plan.BytesToCopy)} to copy.");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Everything on disk {to} ({plan.Target.Model}) will be erased.");
        Console.ResetColor();
        if (!yes)
        {
            Console.Write($"Type ERASE {to} to go ahead: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), $"ERASE {to}", StringComparison.OrdinalIgnoreCase)) return Fail("Not confirmed; nothing was changed.");
        }

        var result = await new DiskCloner(log).RunAsync(plan, cts.Token);
        foreach (var note in result.Notes) log.Warn(note);
        return Done($"Cloned {CaptureEngine.FormatBytes(result.BytesCopied)} in {result.Elapsed:h\\:mm\\:ss}. Put the new drive in the PC and start it up.", result.Notes.Count);
    }

    async Task<int> MakeImage()
    {
        var from = cli.GetInt("from", -1);
        var to = cli.Get("to");
        cli.RejectUnknown();
        if (from < 0 || to is null) return Fail("Say which disk and where: --from <disk number> --to <folder or .vhdx file>. 'pcmigrate-cli disks' lists them.");
        var source = Disks.List().FirstOrDefault(d => d.Number == from) ?? throw new ArgumentException($"There's no disk {from}.");
        var file = to.EndsWith(DiskImage.Extension, StringComparison.OrdinalIgnoreCase) ? to : Path.Combine(to, DiskImage.SuggestName(source));
        if (DiskImage.CheckCreate(source, Path.GetFullPath(file)) is { } problem) return Fail(problem);
        Console.WriteLine($"Imaging {source.Describe()}");
        Console.WriteLine($"Into {Path.GetFullPath(file)} (about {CaptureEngine.FormatBytes(DiskImage.NeededBytes(source))})");
        var result = await new DiskImage(log).CreateAsync(source, file, cts.Token);
        foreach (var note in result.Notes) log.Info(note);
        return Done($"Image saved: {result.ImagePath} ({CaptureEngine.FormatBytes(result.FileSize)}, {result.Elapsed:h\\:mm\\:ss}).", 0);
    }

    async Task<int> RestoreImage()
    {
        var from = cli.Get("from");
        var to = cli.GetInt("to", -1);
        var yes = cli.Flag("yes");
        cli.RejectUnknown();
        if (from is null || to < 0) return Fail("Say which image and which disk: --from <file.vhdx> --to <disk number>. 'pcmigrate-cli disks' lists them.");
        var image = new DiskImage(log);
        var plan = await image.PlanRestoreAsync(from, to, cts.Token);
        if (!plan.CanRun) return Fail(plan.Problem!);
        Console.WriteLine($"Image: {Path.GetFullPath(from)} (made from a {CaptureEngine.FormatBytes(plan.Source.Size)} drive)");
        Console.WriteLine($"To:    {plan.Target.Describe()}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Everything on disk {to} ({plan.Target.Model}) will be erased.");
        Console.ResetColor();
        if (!yes)
        {
            Console.Write($"Type ERASE {to} to go ahead: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), $"ERASE {to}", StringComparison.OrdinalIgnoreCase)) return Fail("Not confirmed; nothing was changed.");
        }
        var result = await image.RestoreAsync(from, to, cts.Token);
        foreach (var note in result.Notes) log.Warn(note);
        return Done($"Restored {CaptureEngine.FormatBytes(result.BytesCopied)} in {result.Elapsed:h\\:mm\\:ss}. Put the drive in the PC and start it up.", result.Notes.Count);
    }

    async Task<int> RescueUsb()
    {
        var number = cli.GetInt("disk", -1);
        var yes = cli.Flag("yes");
        cli.RejectUnknown();
        var sticks = Disks.List().Where(d => d.Bus is "USB" or "SD card" && !d.IsSystem).ToList();
        if (number < 0)
        {
            foreach (var d in sticks) Console.WriteLine($"  {d.Describe()}");
            return Fail(sticks.Count == 0 ? "No USB sticks found. Plug one in (1 GB or bigger)." : "Say which stick: --disk <number>.");
        }
        var disk = sticks.FirstOrDefault(d => d.Number == number) ?? throw new ArgumentException($"Disk {number} isn't a USB stick.");
        if (RescueUsbMaker.Check(disk) is { } problem) return Fail(problem);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Everything on {disk.Describe()} will be erased.");
        Console.ResetColor();
        if (!yes)
        {
            Console.Write($"Type ERASE {number} to go ahead: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), $"ERASE {number}", StringComparison.OrdinalIgnoreCase)) return Fail("Not confirmed; nothing was changed.");
        }
        var download = Path.Combine(Path.GetTempPath(), "pcmigrate-rescue-tools");
        var tools = await RescueUsbMaker.ToolFilesAsync(exePath, download, log, cts.Token);
        var result = await new RescueUsbMaker(log).MakeAsync(disk, tools, cts.Token);
        foreach (var note in result.Notes) log.Info(note);
        return Done($"Rescue USB ready ({result.BootDrive}{(result.DataDrive is null ? "" : ", backups go on " + result.DataDrive)}).", 0);
    }

    async Task<int> SelfUpdate()
    {
        cli.RejectUnknown();
        log.Info("Checking for a newer version...");
        var update = await Updater.CheckAsync("pcmigrate-cli.exe", ToolInfo.FullVersion, cts.Token);
        if (update is null) return Done($"You have the newest version ({ToolInfo.Version}).", 0);
        log.Info($"Downloading {update.Tag}...");
        var last = -1;
        var downloaded = await Updater.DownloadAsync(update, exePath, new Progress<double>(f =>
        {
            var pct = (int)(f * 100);
            if (pct / 10 != last / 10) { last = pct; Console.Write($"\r  {pct}%   "); }
        }), cts.Token);
        Console.WriteLine();
        Updater.Swap(exePath, downloaded);
        return Done($"Updated to {update.Tag.TrimStart('v')}. Run pcmigrate-cli again to use it.", 0);
    }

    int Health()
    {
        var reports = HealthReader.All();
        foreach (var h in reports)
        {
            Console.WriteLine($"Disk {h.DiskNumber}  {h.Model}  [{h.Level}]");
            Console.WriteLine("  " + h.Summary);
            foreach (var d in h.Details) Console.WriteLine("  - " + d);
        }
        if (reports.Count == 0) Console.WriteLine("No drives found.");
        return reports.Any(h => h.Level == PcMigrate.Core.Health.HealthLevel.Failing) ? 2 : 0;
    }

    int Keys()
    {
        var keys = probe.LicenseKeys();
        if (keys.Count == 0)
        {
            Console.WriteLine("No license keys found.");
            return 0;
        }
        foreach (var k in keys)
        {
            Console.WriteLine(k.Product);
            Console.WriteLine("  " + k.Key);
            if (k.Note is not null) Console.WriteLine("  " + k.Note);
        }
        Console.WriteLine();
        Console.WriteLine("Programs licensed through an account (Microsoft 365, Creative Cloud, most antivirus) have no key to show.");
        return 0;
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
                AppMethod.Installer => $"Installed from their saved installer ({group.Count()}):",
                _ => $"Need installing by hand ({group.Count()}):",
            });
            foreach (var app in group)
                Console.WriteLine($"  {app.Name}{(app.Version is null ? "" : "  " + app.Version)}{(group.Key is AppMethod.Manual or AppMethod.Installer ? "  - " + app.Reason : "")}");
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
            log.Info($"{apps.Count(a => a.Method == AppMethod.Reinstall)} program(s) will be reinstalled, {apps.Count(a => a.Method == AppMethod.Copy)} copied, {apps.Count(a => a.Method == AppMethod.Installer)} installed from a saved installer, {apps.Count(a => a.Method == AppMethod.Manual)} need installing by hand.");
        }
        return new CaptureOptions
        {
            Users = users,
            UseShadowCopy = !cli.Flag("no-shadow"),
            IncludeWifi = !cli.Flag("no-wifi"),
            IncludeUserSettings = !cli.Flag("no-settings"),
            Apps = apps,
            Job = ReadJob(),
        };
    }

    /// <summary>Technician mode: a printable report for the client in the backup folder.</summary>
    void WriteCaptureReport(Manifest manifest, string folder)
    {
        var tech = TechnicianStore.Load();
        if (!tech.Enabled) return;
        if (TechnicianStore.Write(new ClientReport.Input { Manifest = manifest, Technician = tech, SavedTo = folder }, folder, log) is { } path)
            Console.WriteLine($"Client report: {path}");
    }

    async Task<int> Capture()
    {
        var to = cli.Require("to");
        var options = ReadCaptureOptions();
        var password = cli.Get("password");
        var encrypt = cli.Flag("encrypt");
        if (password is null && encrypt)
        {
            password = Menu.ReadSecret("Password for the backup (at least 8 characters)");
            if (Menu.ReadSecret("Type it again") != password) return Fail("The passwords didn't match.");
        }
        if (password is { Length: < 8 }) return Fail("Use a password of at least 8 characters.");
        cli.RejectUnknown();

        if (FolderSink.HasStore(to))
            return Fail($"'{to}' already holds a finished capture. Pick an empty folder.");

        await using var sink = new FolderSink(to, password);
        var manifest = await new CaptureEngine(probe, log, appPlatform).RunAsync(sink, options, cts.Token);
        WriteCaptureReport(manifest, sink.Root);
        return Done($"Capture saved to {sink.Root}. Plug the drive into the new PC and run: pcmigrate-cli apply --from \"{sink.Root}\"", manifest.Warnings.Count);
    }

    int Technician()
    {
        var off = cli.Flag("off");
        var company = cli.Get("company");
        var name = cli.Get("name");
        var contact = cli.Get("contact");
        var showKeys = cli.Get("show-keys");
        cli.RejectUnknown();
        var tech = TechnicianStore.Load();
        if (off) tech.Enabled = false;
        else if (company is not null || name is not null || contact is not null || showKeys is not null)
        {
            tech.Enabled = true;
            tech.Company = company ?? tech.Company;
            tech.Technician = name ?? tech.Technician;
            tech.Contact = contact ?? tech.Contact;
            if (showKeys is not null) tech.ShowKeys = !showKeys.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        if (off || company is not null || name is not null || contact is not null || showKeys is not null) TechnicianStore.Save(tech);
        Console.WriteLine($"Technician mode: {(tech.Enabled ? "on" : "off")}");
        if (tech.Enabled)
        {
            Console.WriteLine($"  Business:  {tech.Company ?? "-"}");
            Console.WriteLine($"  Technician: {tech.Technician ?? "-"}");
            Console.WriteLine($"  Contact:   {tech.Contact ?? "-"}");
            Console.WriteLine($"  License keys on client reports: {(tech.ShowKeys ? "shown in full" : "just counted")}");
            Console.WriteLine("  Add --client \"Name\" [--job 1234] to capture or apply; a client report is saved with each one.");
        }
        return 0;
    }

    /// <summary>Brings a backup folder up to date: only new and changed files are copied.</summary>
    async Task<int> Backup()
    {
        var scheduled = cli.Flag("scheduled");
        var settings = scheduled ? BackupScheduler.Load() : null;
        if (scheduled && settings is null) return Fail("No scheduled backup is set up. Use: pcmigrate-cli schedule --to <folder>");
        var to = settings?.Folder ?? cli.Require("to");
        var password = settings is not null ? BackupScheduler.Password(settings) : cli.Get("password");
        if (!scheduled && password is null && cli.Flag("encrypt"))
        {
            password = Menu.ReadSecret("Password for the backup (at least 8 characters)");
            if (Menu.ReadSecret("Type it again") != password) return Fail("The passwords didn't match.");
        }
        if (password is { Length: < 8 }) return Fail("Use a password of at least 8 characters.");
        cli.RejectUnknown();

        int Record(bool ok, string result, int code)
        {
            if (settings is null) return code;
            settings.LastRun = DateTime.Now;
            settings.LastOk = ok;
            settings.LastResult = result;
            BackupScheduler.SaveSettings(settings);
            return code;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(to));
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return Record(false, $"The backup drive ({root}) wasn't plugged in.", Fail($"{root} isn't there. Plug in the backup drive."));

        try
        {
            log.Info($"Bringing the backup in {to} up to date...");
            await using var sink = FolderSink.OpenForUpdate(to, password);
            var manifest = await new CaptureEngine(probe, log, appPlatform).RunAsync(sink, new CaptureOptions { Apps = [] }, cts.Token);
            var bytes = manifest.Files.Sum(f => f.Length);
            var message = $"Backed up {manifest.Files.Count:N0} files ({CaptureEngine.FormatBytes(bytes)}){(sink.Removed > 0 ? $"; {sink.Removed:N0} deleted from the PC were removed" : "")}, {manifest.Warnings.Count} warning(s).";
            return Record(true, message, Done(message, manifest.Warnings.Count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && scheduled)
        {
            Record(false, ex.Message, 1);
            throw;
        }
    }

    async Task<int> ScheduleBackup()
    {
        var to = cli.Get("to");
        var off = cli.Flag("off");
        var now = cli.Flag("now");
        var every = (cli.Get("every") ?? "daily").ToLowerInvariant();
        var at = cli.Get("at") ?? "21:00";
        var day = cli.Get("day");
        var encrypt = cli.Flag("encrypt");
        cli.RejectUnknown();

        if (off)
        {
            BackupScheduler.Disable();
            return Done("Scheduled backups are off. The backups already made are still there.", 0);
        }
        if (now)
        {
            BackupScheduler.RunNow();
            return Done($"The backup started in the background. Check on it with 'pcmigrate-cli schedule' or in {BackupScheduler.LogPath}.", 0);
        }
        if (to is null)
        {
            var current = BackupScheduler.Load();
            if (current is null) { Console.WriteLine("No scheduled backup. Set one up with: pcmigrate-cli schedule --to E:\\Backups [--every daily|weekly] [--at 21:00] [--day monday] [--encrypt]"); return 0; }
            Console.WriteLine($"Backs up to:  {current.Folder}{(current.ProtectedPassword is null ? "" : " (password-protected)")}");
            Console.WriteLine($"When:         {current.Describe()}");
            Console.WriteLine($"Last backup:  {(current.LastRun is { } t ? $"{t:g}: {current.LastResult}" : "not yet")}");
            Console.WriteLine($"Log:          {BackupScheduler.LogPath}");
            return 0;
        }

        var settings = new ScheduleSettings
        {
            Folder = Path.GetFullPath(to), Frequency = every, Time = at,
            Day = day is null ? DayOfWeek.Monday : Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var d) ? d : throw new ArgumentException("--day takes a day name, like monday."),
        };
        if (settings.Problem() is { } problem) return Fail(problem);
        string? password = null;
        if (encrypt)
        {
            password = Menu.ReadSecret("Password for the backups (at least 8 characters)");
            if (password.Length < 8) return Fail("Use a password of at least 8 characters.");
            if (Menu.ReadSecret("Type it again") != password) return Fail("The passwords didn't match.");
        }
        var cliExe = await CliTool.FindOrDownloadAsync(exePath, Path.Combine(Path.GetTempPath(), "pcmigrate-cli-download"), log, cts.Token);
        BackupScheduler.Enable(settings, password, cliExe);
        return Done($"Scheduled: {settings.Describe()}, to {settings.Folder}. Only new and changed files are copied each time. Run now with: pcmigrate-cli schedule --now", 0);
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

    JobInfo? ReadJob() =>
        cli.Get("client") is { Length: > 0 } client ? new JobInfo { Client = client, JobNumber = cli.Get("job"), Notes = cli.Get("notes") } : null;

    ApplyOptions ReadApplyOptions(string? from = null)
    {
        var password = cli.Get("password");
        if (password is null && from is not null && FolderSink.IsProtected(from))
            password = Menu.ReadSecret("This backup is protected. Password");
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
            Password = password,
            Technician = TechnicianStore.Load(),
            Job = ReadJob(),
        };
    }

    async Task<int> Apply(string from, ApplyOptions? options = null)
    {
        if (options is null)
        {
            options = ReadApplyOptions(from);
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
        if (result.ClientReportPath is not null) Console.WriteLine($"Client report: {result.ClientReportPath}");
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
              pcmigrate-cli health         Check each drive's health (SMART) before rescuing or cloning it.
              pcmigrate-cli keys           Show Windows, Office and program license keys (add --from-drive E: for a dead PC's drive).
              pcmigrate-cli version
              pcmigrate-cli disks          List the physical disks and their partitions.
              pcmigrate-cli clone --from <n> --to <n> [--shrink-source] [--yes]
                                           Copy a whole disk so the copy starts up on its own.
                                           Erases the target. --shrink-source shrinks the source's
                                           main partition first when the target is smaller.
              pcmigrate-cli image --from <n> --to <folder or file.vhdx>
                                           Save a whole disk as one image file (only the space in use).
                                           Double-click the file in Explorer to open it and copy files out.
              pcmigrate-cli restore-image --from <file.vhdx> --to <n> [--yes]
                                           Put an image back onto a disk so it starts up. Erases the disk.
              pcmigrate-cli rescue-usb --disk <n> [--yes]
                                           Make a USB stick that starts a PC whose Windows won't start,
                                           straight into pc-migrate. Erases the stick.
              pcmigrate-cli backup --to <folder> [--encrypt]
                                           Bring a backup up to date: only new and changed files are copied,
                                           and files deleted from the PC are removed from it.
              pcmigrate-cli schedule --to <folder> [--every daily|weekly] [--at 21:00] [--day monday] [--encrypt]
                                           Back up automatically in the background. 'schedule' alone shows
                                           the last result; --now runs it straight away; --off turns it off.
              pcmigrate-cli tech --company "Name" [--name "Tech"] [--contact "phone/email"] [--show-keys] | --off
                                           Technician mode: capture and apply save a printable client report.
                                           Add --client "Client" [--job 1234] [--notes "..."] to capture or apply.
              pcmigrate-cli update         Download and install the newest version.

            Password protection (capture to a folder, and apply):
              --encrypt                    Ask for a password and encrypt the backup with it.
              --password <password>        The same without asking (for scripts). apply asks if it's needed.

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
