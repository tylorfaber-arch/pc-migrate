using PcMigrate.Core.Windows;

namespace PcMigrate.Cli;

/// <summary>Turns a few plain questions into the same arguments someone could type on the command line.</summary>
internal static class Menu
{
    public static string[]? Ask()
    {
        Console.Title = "pc-migrate";
        Console.WriteLine("pc-migrate: move files and settings to a new PC");
        Console.WriteLine();
        Console.WriteLine("  1. See what's on this PC (changes nothing)");
        Console.WriteLine("  2. OLD PC: save everything to a drive or folder");
        Console.WriteLine("  3. NEW PC: load everything from a drive or folder");
        Console.WriteLine("  4. NEW PC: receive over the network  (start this first)");
        Console.WriteLine("  5. OLD PC: send over the network");
        Console.WriteLine("  6. Rescue a drive pulled from a dead PC (plugged in by USB)");
        Console.WriteLine("  7. Clone a whole drive onto another (erases the target)");
        Console.WriteLine("  8. Show license keys (Windows, Office, programs)");
        Console.WriteLine("  9. Save a whole drive as an image file, or put one back");
        Console.WriteLine("  R. Make a rescue USB (starts a PC whose Windows won't start)");
        Console.WriteLine("  0. Show all commands");
        Console.WriteLine();

        while (true)
        {
            switch (Prompt("Choose 0-9 (or press Enter to quit)"))
            {
                case "":
                    return null;
                case "1":
                    return ["scan"];
                case "2":
                {
                    var to = PromptRequired("Folder to save to, for example E:\\migration");
                    var encrypt = Prompt("Protect it with a password? (y/N)").StartsWith('y');
                    return [.. new[] { "capture", "--to", to }, .. (encrypt ? new[] { "--encrypt" } : []), .. Users(), .. Log(to)];
                }
                case "3":
                {
                    var from = PromptRequired("Folder the old PC saved to, for example E:\\migration");
                    var result = new List<string> { "apply", "--from", from };
                    var map = Prompt("If your user name is different on this PC, type old=new (or press Enter)");
                    if (map.Contains('=')) result.AddRange(["--map", map]);
                    return [.. result, .. Log(from)];
                }
                case "4":
                    return ["receive"];
                case "5":
                {
                    var host = PromptRequired("IP address shown on the new PC");
                    var code = PromptRequired("Pairing code shown on the new PC");
                    return [.. new[] { "send", "--host", host, "--code", code }, .. Users()];
                }
                case "6":
                {
                    Console.WriteLine();
                    var drives = OfflineWindows.Find();
                    foreach (var d in drives)
                        Console.WriteLine(d.Locked ? $"  {d.Root}  locked with BitLocker" : $"  {d.Root}  {d.ComputerName}  (users: {string.Join(", ", d.Users)})");
                    if (drives.Count == 0) Console.WriteLine("  No drives with Windows found. Plug it in and choose 6 again.");
                    if (drives.Count == 0) break;
                    var drive = PromptRequired("Drive letter of the old PC's drive, for example E");
                    var to = PromptRequired("Folder to save to, for example F:\\migration");
                    var result = new List<string> { "capture", "--from-drive", drive, "--to", to };
                    if (drives.Any(d => d.Locked && d.Root.StartsWith(drive.Trim()[..1], StringComparison.OrdinalIgnoreCase)))
                        result.AddRange(["--recovery-key", PromptRequired("BitLocker recovery key (48 digits, from aka.ms/myrecoverykey)")]);
                    return [.. result, .. Users(), .. Log(to)];
                }
                case "7":
                {
                    Console.WriteLine();
                    foreach (var disk in Disks.List())
                        Console.WriteLine($"  {disk.Describe()}{(disk.IsSystem ? "   <- this PC runs from it" : "")}");
                    var from = PromptRequired("Number of the disk to copy FROM");
                    var to = PromptRequired("Number of the disk to copy TO (it will be erased)");
                    return ["clone", "--from", from, "--to", to, "--shrink-source"];
                }
                case "8":
                {
                    var drive = Prompt("Drive letter of a dead PC's drive, or press Enter for this PC");
                    return drive.Length == 0 ? ["keys"] : ["keys", "--from-drive", drive];
                }
                case "9":
                {
                    Console.WriteLine();
                    foreach (var disk in Disks.List())
                        Console.WriteLine($"  {disk.Describe()}{(disk.IsSystem ? "   <- this PC runs from it" : "")}");
                    if (Prompt("Make an image (M) or restore one (R)?").StartsWith('r'))
                    {
                        var image = PromptRequired("Image file, for example E:\\images\\PC.vhdx");
                        var disk = PromptRequired("Number of the disk to put it on (it will be erased)");
                        return ["restore-image", "--from", image, "--to", disk];
                    }
                    var from = PromptRequired("Number of the disk to image");
                    var to = PromptRequired("Folder to save the image in, for example E:\\images");
                    return ["image", "--from", from, "--to", to];
                }
                case "0":
                    return ["help"];
                case "r" or "R":
                {
                    Console.WriteLine();
                    foreach (var disk in Disks.List().Where(d => d.Bus is "USB" or "SD card" && !d.IsSystem))
                        Console.WriteLine($"  {disk.Describe()}");
                    var number = PromptRequired("Number of the USB stick to use (it will be erased)");
                    return ["rescue-usb", "--disk", number];
                }
                case "u" or "U":
                    return ["update"];
                default:
                    Console.WriteLine("Please type a number from 0 to 9.");
                    break;
            }
        }
    }

    public static void WaitForEnter()
    {
        Console.WriteLine();
        Console.Write("Press Enter to close this window.");
        Console.ReadLine();
    }

    private static string[] Users()
    {
        var users = Prompt("Only some users? Type names separated by commas (or press Enter for everyone)");
        return users.Length == 0 ? [] : ["--user", users];
    }

    // Keep a log next to the data, so problems can be looked at later.
    private static string[] Log(string folder) =>
        ["--log", Path.Combine(folder, $"pcmigrate-{DateTime.Now:yyyyMMdd-HHmmss}.log")];

    /// <summary>Reads a password without showing it.</summary>
    internal static string ReadSecret(string question)
    {
        Console.Write(question + ": ");
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }

    private static string Prompt(string question)
    {
        Console.Write(question + ": ");
        // "Copy as path" in Explorer adds quotes.
        return (Console.ReadLine() ?? "").Trim().Trim('"').Trim();
    }

    private static string PromptRequired(string question)
    {
        while (true)
        {
            var answer = Prompt(question);
            if (answer.Length > 0) return answer;
        }
    }
}
