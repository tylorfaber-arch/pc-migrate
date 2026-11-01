using System.Xml.Linq;

namespace PcMigrate.Core.Offline;

/// <summary>
/// Wi-Fi networks saved on a pulled drive. Windows keeps each one as an XML file under
/// ProgramData\Microsoft\Wlansvc\Profiles, with the password encrypted by the old PC's
/// own system key, which only that PC (running) can unlock. So the networks come over
/// without passwords: the new PC knows them and asks for the password once.
/// </summary>
public static class OfflineWifi
{
    public static string ProfilesFolder(OfflinePaths paths) =>
        paths.ToReadable(paths.SystemDrive + @"\ProgramData\Microsoft\Wlansvc\Profiles\Interfaces");

    /// <summary>Takes out the encrypted password, which another PC can't use. Returns the network name too.</summary>
    public static (string Xml, string? Name) RemovePassword(string xml)
    {
        var doc = XDocument.Parse(xml);
        var name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
        foreach (var key in doc.Descendants().Where(e => e.Name.LocalName == "sharedKey").ToList()) key.Remove();
        return (doc.Declaration + Environment.NewLine + doc.Root, name);
    }
}
