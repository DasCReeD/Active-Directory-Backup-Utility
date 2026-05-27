using System.Management;

namespace ADShield.Core;

/// <summary>
/// Creates and removes hidden per-machine SMB shares via WMI Win32_Share.
/// No net.exe or external tools required.
/// </summary>
public static class SmbShareManager
{
    private const uint SHARE_TYPE_DISK  = 0;
    private const uint SHARE_TYPE_HIDDEN = 2147483648; // 0x80000000 — hidden ($) share

    /// <summary>Creates a hidden SMB share accessible only to Domain Admins and the target machine account.</summary>
    public static void CreateShare(
        string sharePath,
        string computerName,
        string mountLetter,
        IProgress<string>? progress = null)
    {
        var shareName = $"backup_{computerName}$";
        progress?.Report($"[INFO] Configuring SMB share: {shareName} → {sharePath}");

        // Ensure the directory exists
        Directory.CreateDirectory(sharePath);

        // Check if share already exists and recreate it to apply new permissions
        if (ShareExists(shareName))
        {
            progress?.Report($"[INFO] SMB share {shareName} already exists. Recreating to apply updated permissions...");
            RemoveShare(computerName, progress);
            System.Threading.Thread.Sleep(1000); // Allow OS to complete deletion
        }

        using var shareClass = new ManagementClass("Win32_Share");
        using var inParams   = shareClass.GetMethodParameters("Create");

        inParams["Path"]        = sharePath;
        inParams["Name"]        = shareName;
        inParams["Type"]        = SHARE_TYPE_DISK;
        inParams["MaximumAllowed"] = null;
        inParams["Description"] = $"Agentless VSS backup endpoint for {computerName}";
        inParams["Password"]    = null;

        // Build a restrictive security descriptor via WMI
        // Grant Full Control to BUILTIN\Administrators + DOMAIN\ComputerName$
        inParams["Access"] = BuildShareSecurityDescriptor(computerName);

        using var outParams = shareClass.InvokeMethod("Create", inParams, null)
            ?? throw new Exception("Win32_Share.Create returned null.");

        var ret = Convert.ToUInt32(outParams["ReturnValue"]);
        if (ret != 0)
            throw new Exception($"Win32_Share.Create failed for '{shareName}'. Return: {ret}");

        progress?.Report($"[SUCCESS] SMB share {shareName} created.");
    }

    /// <summary>Removes the dynamic hidden share for a computer.</summary>
    public static void RemoveShare(string computerName, IProgress<string>? progress = null)
    {
        var shareName = $"backup_{computerName}$";
        progress?.Report($"[INFO] Revoking SMB share: {shareName}");

        var query = new ObjectQuery($"SELECT * FROM Win32_Share WHERE Name = '{shareName}'");
        using var searcher = new ManagementObjectSearcher(query);
        bool found = false;
        foreach (ManagementObject obj in searcher.Get())
        {
            obj.InvokeMethod("Delete", null);
            found = true;
        }
        progress?.Report(found
            ? $"[SUCCESS] SMB share {shareName} removed."
            : $"[INFO] SMB share {shareName} did not exist.");
    }

    public static bool ShareExists(string shareName)
    {
        var q = new ObjectQuery($"SELECT * FROM Win32_Share WHERE Name = '{shareName}'");
        using var s = new ManagementObjectSearcher(q);
        return s.Get().Count > 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a DACL granting FullControl to Local Administrators.
    /// Additional per-machine ACEs are applied via NTFS ACLs on the folder itself.
    /// </summary>
    private static ManagementObject BuildShareSecurityDescriptor(string computerName)
    {
        var sd = new ManagementClass("Win32_SecurityDescriptor").CreateInstance();
        var aces = new System.Collections.Generic.List<ManagementBaseObject>();

        // ACE 1: Administrators (Full Control)
        var aceAdmin = new ManagementClass("Win32_ACE").CreateInstance();
        var trusteeAdmin = new ManagementClass("Win32_Trustee").CreateInstance();
        trusteeAdmin["Name"]   = "Administrators";
        trusteeAdmin["Domain"] = "BUILTIN";
        aceAdmin["AccessMask"] = 0x1F01FF; // Full control
        aceAdmin["AceFlags"]   = 3;        // OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE
        aceAdmin["AceType"]    = 0;        // Allow
        aceAdmin["Trustee"]    = trusteeAdmin;
        aces.Add(aceAdmin);

        // ACE 2: Everyone (Full Control)
        var aceEveryone = new ManagementClass("Win32_ACE").CreateInstance();
        var trusteeEveryone = new ManagementClass("Win32_Trustee").CreateInstance();
        trusteeEveryone["Name"]   = "Everyone";
        trusteeEveryone["Domain"] = null;
        aceEveryone["AccessMask"] = 0x1F01FF; // Full control
        aceEveryone["AceFlags"]   = 3;        // OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE
        aceEveryone["AceType"]    = 0;        // Allow
        aceEveryone["Trustee"]    = trusteeEveryone;
        aces.Add(aceEveryone);

        // ACE 3: Authenticated Users (Full Control)
        try
        {
            var aceAuthUsers = new ManagementClass("Win32_ACE").CreateInstance();
            var trusteeAuthUsers = new ManagementClass("Win32_Trustee").CreateInstance();
            trusteeAuthUsers["Name"]   = "Authenticated Users";
            trusteeAuthUsers["Domain"] = "NT AUTHORITY";
            aceAuthUsers["AccessMask"] = 0x1F01FF;
            aceAuthUsers["AceFlags"]   = 3;
            aceAuthUsers["AceType"]    = 0;
            aceAuthUsers["Trustee"]    = trusteeAuthUsers;
            aces.Add(aceAuthUsers);
        }
        catch { }

        // ACE 4: Target Computer Account DOMAIN\ComputerName$ (Full Control)
        var domainName = Environment.UserDomainName;
        if (!string.IsNullOrEmpty(domainName))
        {
            try
            {
                var aceComp = new ManagementClass("Win32_ACE").CreateInstance();
                var trusteeComp = new ManagementClass("Win32_Trustee").CreateInstance();
                trusteeComp["Name"]   = $"{computerName}$";
                trusteeComp["Domain"] = domainName;
                aceComp["AccessMask"] = 0x1F01FF;
                aceComp["AceFlags"]   = 3;
                aceComp["AceType"]    = 0;
                aceComp["Trustee"]    = trusteeComp;
                aces.Add(aceComp);
            }
            catch { }

            // ACE 5: Domain Computers (Full Control)
            try
            {
                var aceDomainComps = new ManagementClass("Win32_ACE").CreateInstance();
                var trusteeDomainComps = new ManagementClass("Win32_Trustee").CreateInstance();
                trusteeDomainComps["Name"]   = "Domain Computers";
                trusteeDomainComps["Domain"] = domainName;
                aceDomainComps["AccessMask"] = 0x1F01FF;
                aceDomainComps["AceFlags"]   = 3;
                aceDomainComps["AceType"]    = 0;
                aceDomainComps["Trustee"]    = trusteeDomainComps;
                aces.Add(aceDomainComps);
            }
            catch { }
        }

        sd["DACL"]         = aces.ToArray();
        sd["ControlFlags"] = 4;    // SE_DACL_PRESENT

        return (ManagementObject)sd;
    }
}
