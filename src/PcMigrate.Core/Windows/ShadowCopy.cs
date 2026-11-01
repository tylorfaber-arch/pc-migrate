using System.Management;
using System.Runtime.Versioning;

namespace PcMigrate.Core.Windows;

/// <summary>
/// A Volume Shadow Copy: a frozen, read-only snapshot of a drive. Copying from it
/// lets us read files that are open and locked (Outlook data files, browser
/// databases) and gives a consistent point-in-time copy. Deleted on Dispose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShadowCopy : IDisposable
{
    private ShadowCopy(string volume, string id, string deviceObject)
    {
        Volume = volume;
        Id = id;
        DeviceObject = deviceObject;
    }

    /// <summary>Drive root the snapshot was taken of, e.g. "C:\".</summary>
    public string Volume { get; }
    public string Id { get; }
    /// <summary>e.g. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7</summary>
    public string DeviceObject { get; }

    public static ShadowCopy Create(string volume)
    {
        volume = Path.GetPathRoot(volume) ?? throw new ArgumentException("Not a drive path", nameof(volume));

        using var shadowClass = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null);
        using var input = shadowClass.GetMethodParameters("Create");
        input["Volume"] = volume;
        input["Context"] = "ClientAccessible";
        using var output = shadowClass.InvokeMethod("Create", input, null);

        var code = Convert.ToUInt32(output["ReturnValue"]);
        if (code != 0) throw new InvalidOperationException($"Windows could not create a shadow copy of {volume} (error {code}).");
        var id = (string)output["ShadowID"];

        using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='{id}'");
        foreach (ManagementObject item in searcher.Get())
        {
            using (item) return new ShadowCopy(volume, id, (string)item["DeviceObject"]);
        }
        throw new InvalidOperationException("Shadow copy was created but could not be found.");
    }

    /// <summary>Converts C:\Users\a\Documents into the same path inside the snapshot.</summary>
    public string MapPath(string livePath)
    {
        if (!livePath.StartsWith(Volume, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{livePath}' is not on {Volume}");
        return DeviceObject + @"\" + livePath[Volume.Length..];
    }

    public void Dispose()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID='{Id}'");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item) item.Delete();
            }
        }
        catch (ManagementException)
        {
            // Best effort. Windows also cleans these up when space runs low.
        }
    }
}
