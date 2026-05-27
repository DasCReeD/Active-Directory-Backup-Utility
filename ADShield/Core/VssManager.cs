using System.Management;

namespace ADShield.Core;

/// <summary>
/// VSS shadow copy management via WMI Win32_ShadowCopy.
/// Works locally and over remote WMI connections — no external executables.
/// </summary>
public static class VssManager
{
    // ── Create ────────────────────────────────────────────────────────────────

    /// <summary>Creates a VSS shadow copy on the local machine for the given volume.</summary>
    public static string CreateLocalShadowCopy(string volume, IProgress<string>? progress = null)
    {
        progress?.Report($"[INFO] Creating VSS shadow copy for volume {volume}...");

        using var mc       = new ManagementClass("Win32_ShadowCopy");
        using var inParams = mc.GetMethodParameters("Create");
        inParams["Volume"]  = volume.TrimEnd('\\') + "\\";
        inParams["Context"] = "ClientAccessible";

        using var outParams = mc.InvokeMethod("Create", inParams, null)
            ?? throw new Exception("Win32_ShadowCopy.Create returned null.");

        var returnVal = Convert.ToUInt32(outParams["ReturnValue"]);
        if (returnVal != 0)
            throw new Exception($"VSS Create failed. WMI return value: {returnVal}");

        var shadowId = outParams["ShadowID"]?.ToString()
            ?? throw new Exception("VSS shadow copy created but returned no ShadowID.");

        progress?.Report($"[SUCCESS] Shadow copy created. ID: {shadowId}");
        return shadowId;
    }

    /// <summary>Creates a VSS shadow copy on a remote machine via WMI.</summary>
    public static string CreateRemoteShadowCopy(
        string computerName,
        string volume,
        string? username = null,
        string? password = null,
        IProgress<string>? progress = null)
    {
        progress?.Report($"[INFO] Connecting to WMI on {computerName}...");

        var options = new ConnectionOptions();
        if (!string.IsNullOrEmpty(username))
        {
            options.Username = username;
            options.Password = password;
        }
        options.Impersonation = ImpersonationLevel.Impersonate;
        options.EnablePrivileges = true;

        var scope = new ManagementScope($@"\\{computerName}\root\cimv2", options);
        scope.Connect();
        progress?.Report($"[INFO] WMI connected. Creating VSS shadow copy on {computerName}:{volume}...");

        using var mc       = new ManagementClass(scope, new ManagementPath("Win32_ShadowCopy"), null);
        using var inParams = mc.GetMethodParameters("Create");
        inParams["Volume"]  = volume.TrimEnd('\\') + "\\";
        inParams["Context"] = "ClientAccessible";

        using var outParams = mc.InvokeMethod("Create", inParams, null)
            ?? throw new Exception("Remote Win32_ShadowCopy.Create returned null.");

        var returnVal = Convert.ToUInt32(outParams["ReturnValue"]);
        if (returnVal != 0)
            throw new Exception($"Remote VSS Create failed on {computerName}. Return: {returnVal}");

        var shadowId = outParams["ShadowID"]?.ToString()
            ?? throw new Exception("Remote VSS created but returned no ShadowID.");

        progress?.Report($"[SUCCESS] Remote shadow copy on {computerName}. ID: {shadowId}");
        return shadowId;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public static List<ShadowCopyInfo> ListShadowCopies(string? computerName = null)
    {
        var scope = computerName is null
            ? new ManagementScope(@"\\.\root\cimv2")
            : new ManagementScope($@"\\{computerName}\root\cimv2");
        scope.Connect();

        var query = new ObjectQuery("SELECT * FROM Win32_ShadowCopy");
        using var searcher = new ManagementObjectSearcher(scope, query);
        var result = new List<ShadowCopyInfo>();

        foreach (ManagementObject obj in searcher.Get())
        {
            result.Add(new ShadowCopyInfo
            {
                ID            = obj["ID"]?.ToString() ?? string.Empty,
                VolumeName    = obj["VolumeName"]?.ToString() ?? string.Empty,
                DeviceObject  = obj["DeviceObject"]?.ToString() ?? string.Empty,
                InstallDate   = obj["InstallDate"]?.ToString() ?? string.Empty
            });
        }
        return result;
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public static void DeleteShadowCopy(string shadowId, IProgress<string>? progress = null)
    {
        progress?.Report($"[INFO] Deleting shadow copy {shadowId}...");
        var query   = new ObjectQuery($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
        using var s = new ManagementObjectSearcher(query);
        foreach (ManagementObject obj in s.Get())
            obj.Delete();
        progress?.Report("[SUCCESS] Shadow copy deleted.");
    }

    // ── Test WMI Connectivity ─────────────────────────────────────────────────

    public static bool TestWmiConnectivity(string computerName, out string error)
    {
        error = string.Empty;
        try
        {
            var scope = new ManagementScope($@"\\{computerName}\root\cimv2");
            scope.Options.Timeout = TimeSpan.FromSeconds(10);
            scope.Connect();
            return scope.IsConnected;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

public record ShadowCopyInfo(
    string ID            = "",
    string VolumeName    = "",
    string DeviceObject  = "",
    string InstallDate   = "")
{
    public ShadowCopyInfo() : this("", "", "", "") { }
    public string ID           { get; init; } = ID;
    public string VolumeName   { get; init; } = VolumeName;
    public string DeviceObject { get; init; } = DeviceObject;
    public string InstallDate  { get; init; } = InstallDate;
}
