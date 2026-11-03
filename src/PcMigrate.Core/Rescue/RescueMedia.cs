using System.Text.RegularExpressions;

namespace PcMigrate.Core.Rescue;

/// <summary>
/// The pieces of a pc-migrate rescue USB that don't need Windows to work out: what goes on it,
/// the start-up script, and the boot menu commands. <see cref="Windows.RescueUsbMaker"/> does the writing.
/// </summary>
public static partial class RescueMedia
{
    /// <summary>The small boot partition (FAT32, which every PC's firmware can start from).</summary>
    public const long BootPartitionMb = 2048;
    /// <summary>Smaller sticks get just the boot partition; bigger ones also get room to save to.</summary>
    public const long MinBytesForDataPartition = 8L * 1000 * 1000 * 1000;
    public const long MinBytes = 1L * 1000 * 1000 * 1000;

    public const string BootLabel = "PCM-BOOT";
    public const string DataLabel = "PCM-DATA";
    public const string ToolFolder = "pc-migrate";
    public const string CliName = "pcmigrate-cli.exe";
    public const string RescueScript = "pcmigrate-rescue.cmd";

    /// <summary>
    /// Replaces Windows' recovery menu with pc-migrate: winpeshl.ini is what the recovery
    /// environment runs at start-up.
    /// </summary>
    public static string WinpeshlIni =>
        "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\cmd.exe, /k %SYSTEMROOT%\\System32\\" + RescueScript + "\r\n";

    /// <summary>Starts networking, finds pc-migrate on whichever letter the USB got, and opens its menu.</summary>
    public static string StartScript => string.Join("\r\n",
    [
        "@echo off",
        "title pc-migrate rescue",
        "wpeinit",
        "set PM=",
        $"for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do if exist %%d:\\{ToolFolder}\\{CliName} set PM=%%d:\\{ToolFolder}\\{CliName}",
        "echo.",
        "echo pc-migrate rescue USB",
        "echo  - This PC's own drive usually shows up as C: here (sometimes D:).",
        "echo  - Choose 6 to save a drive's files to the USB stick or another drive.",
        "echo  - Type exit, or close this window, to restart the PC.",
        "echo.",
        "if not defined PM echo pc-migrate wasn't found on the USB stick. Copy pcmigrate-cli.exe into a pc-migrate folder on it.",
        "if defined PM \"%PM%\"",
        "",
    ]);

    /// <summary>
    /// bcdedit commands that build a boot menu starting the recovery image from a RAM disk,
    /// the same way Windows' own install USBs start. {id} stands for the new entry's id.
    /// </summary>
    public static IReadOnlyList<string[]> BootEntryCommands(string store) =>
    [
        ["/store", store, "/set", "{ramdiskoptions}", "ramdisksdidevice", "boot"],
        ["/store", store, "/set", "{ramdiskoptions}", "ramdisksdipath", @"\boot\boot.sdi"],
        ["/store", store, "/set", "{id}", "device", @"ramdisk=[boot]\sources\boot.wim,{ramdiskoptions}"],
        ["/store", store, "/set", "{id}", "osdevice", @"ramdisk=[boot]\sources\boot.wim,{ramdiskoptions}"],
        ["/store", store, "/set", "{id}", "path", @"\windows\system32\boot\winload.efi"],
        ["/store", store, "/set", "{id}", "systemroot", @"\windows"],
        ["/store", store, "/set", "{id}", "winpe", "yes"],
        ["/store", store, "/set", "{id}", "detecthal", "yes"],
        ["/store", store, "/set", "{bootmgr}", "device", "boot"],
        ["/store", store, "/set", "{bootmgr}", "timeout", "5"],
        ["/store", store, "/displayorder", "{id}", "/addfirst"],
        ["/store", store, "/default", "{id}"],
    ];

    /// <summary>The recovery environment's folder from "reagentc /info", e.g. \\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE.</summary>
    public static string? RecoveryFolder(string reagentcInfo) =>
        GlobalRoot().Match(reagentcInfo) is { Success: true } m ? m.Value.Trim() : null;

    /// <summary>The id bcdedit prints for a new entry.</summary>
    public static string? NewEntryId(string bcdeditOutput) =>
        EntryId().Match(bcdeditOutput) is { Success: true } m ? m.Value : null;

    public static string ReadMe => string.Join("\r\n",
    [
        "pc-migrate rescue USB",
        "",
        "Starting a PC from this stick:",
        "  1. Plug it in and turn the PC on while tapping its boot menu key",
        "     (F12 on Dell and Lenovo, F9 on HP, F8 on ASUS, F12 or Esc on most others).",
        "  2. Pick the USB stick (it may say UEFI: followed by the stick's name).",
        "  3. pc-migrate's menu opens. Choose 6 to save the PC's files from its drive.",
        "",
        "If the PC refuses to start from it, turn off Secure Boot in its firmware settings, or",
        "pull the drive and use 'The old PC is dead, but I have its drive' on another PC instead.",
        "",
        $"Backups can be saved to the {DataLabel} part of this stick if it's big enough, or to another USB drive.",
        "",
    ]);

    [GeneratedRegex(@"\\\\\?\\GLOBALROOT\\[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex GlobalRoot();

    [GeneratedRegex(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}")]
    private static partial Regex EntryId();
}
