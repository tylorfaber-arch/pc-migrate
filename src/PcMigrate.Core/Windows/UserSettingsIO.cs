using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PcMigrate.Core.Models;
using PcMigrate.Core.Offline;

namespace PcMigrate.Core.Windows;

/// <summary>Reads and writes the per-user settings that live in the registry: wallpaper and mapped network drives.</summary>
[SupportedOSPlatform("windows")]
internal static partial class UserSettingsIO
{
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string NetworkKey = "Network";

    public sealed record Captured(UserSettings Settings, string? WallpaperSourcePath);

    public static Captured? Read(UserProfileInfo user, IMigrationLog log, OfflinePaths? offline = null)
    {
        using var hive = UserHive.Open(user, writable: false, log, offline);
        if (hive is null) return null;

        var settings = new UserSettings { UserName = user.UserName };
        string? wallpaper = null;

        using (var desktop = hive.Root.OpenSubKey(DesktopKey))
        {
            if (desktop?.GetValue("Wallpaper") is string wp && wp.Length > 0)
            {
                wp = offline?.ToReadable(wp) ?? Environment.ExpandEnvironmentVariables(wp);
                // A signed-in user's wallpaper path may point into their own profile, which we read live.
                if (File.Exists(wp)) wallpaper = wp;
            }
            settings.WallpaperStyle = desktop?.GetValue("WallpaperStyle") as string;
            settings.TileWallpaper = desktop?.GetValue("TileWallpaper") as string;
        }

        using (var network = hive.Root.OpenSubKey(NetworkKey))
        {
            foreach (var letter in network?.GetSubKeyNames() ?? [])
            {
                using var drive = network!.OpenSubKey(letter);
                if (drive?.GetValue("RemotePath") is string remote && remote.Length > 0)
                    settings.MappedDrives.Add(new MappedDrive { Letter = letter.ToUpperInvariant(), RemotePath = remote });
            }
        }

        return new Captured(settings, wallpaper);
    }

    public static void Write(UserProfileInfo user, UserSettings settings, string? wallpaperFile, bool isCurrentUser, IMigrationLog log)
    {
        using var hive = UserHive.Open(user, writable: true, log);
        if (hive is null) return;

        if (wallpaperFile is not null)
        {
            using var desktop = hive.Root.CreateSubKey(DesktopKey);
            desktop.SetValue("Wallpaper", wallpaperFile, RegistryValueKind.String);
            if (settings.WallpaperStyle is not null) desktop.SetValue("WallpaperStyle", settings.WallpaperStyle, RegistryValueKind.String);
            if (settings.TileWallpaper is not null) desktop.SetValue("TileWallpaper", settings.TileWallpaper, RegistryValueKind.String);

            // For the person running pc-migrate, change it right away instead of at next sign-in.
            if (isCurrentUser) SystemParametersInfo(SpiSetDeskWallpaper, 0, wallpaperFile, SpifUpdateIniFile | SpifSendChange);
        }

        foreach (var drive in settings.MappedDrives)
        {
            using var existing = hive.Root.OpenSubKey($@"{NetworkKey}\{drive.Letter}");
            if (existing is not null)
            {
                log.Warn($"Drive {drive.Letter}: is already mapped for {user.UserName}, left as is.");
                continue;
            }
            // Same values Windows writes for "Map network drive" with "Reconnect at sign-in" ticked.
            using var key = hive.Root.CreateSubKey($@"{NetworkKey}\{drive.Letter}");
            key.SetValue("RemotePath", drive.RemotePath, RegistryValueKind.String);
            key.SetValue("UserName", "", RegistryValueKind.String);
            key.SetValue("ProviderName", "Microsoft Windows Network", RegistryValueKind.String);
            key.SetValue("ProviderType", 0x20000, RegistryValueKind.DWord);
            key.SetValue("ConnectionType", 1, RegistryValueKind.DWord);
            key.SetValue("DeferFlags", 4, RegistryValueKind.DWord);
            log.Info($"Mapped {drive.Letter}: to {drive.RemotePath} for {user.UserName} (connects at next sign-in).");
        }
    }

    private const uint SpiSetDeskWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, string value, uint flags);
}
