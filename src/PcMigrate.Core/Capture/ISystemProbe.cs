using PcMigrate.Core.Models;

namespace PcMigrate.Core.Capture;

/// <summary>Looks at the PC this program is running on. Swapped for a fake in tests.</summary>
public interface ISystemProbe
{
    Inventory GetInventory();

    /// <summary>SID of the account running pc-migrate, used to apply settings live.</summary>
    string? CurrentUserSid { get; }

    /// <summary>Set when reading a drive pulled from a dead PC rather than the running Windows.</summary>
    Offline.OfflinePaths? Offline => null;

    /// <summary>License keys found on the PC (or drive). Empty where they can't be read.</summary>
    IReadOnlyList<Keys.LicenseKey> LicenseKeys() => [];
}
