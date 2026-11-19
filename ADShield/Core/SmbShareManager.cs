using System.Management;

namespace ADShield.Core;

/// <summary>
/// Queries SMB shares via WMI Win32_Share.
/// </summary>
public static class SmbShareManager
{
    public static bool ShareExists(string shareName)
    {
        var q = new ObjectQuery($"SELECT * FROM Win32_Share WHERE Name = '{shareName}'");
        using var s = new ManagementObjectSearcher(q);
        return s.Get().Count > 0;
    }
}
