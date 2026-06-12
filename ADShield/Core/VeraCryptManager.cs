using ADShield.Models;

namespace ADShield.Core;

/// <summary>
/// VeraCrypt container lifecycle — the ONE permitted Process.Start exception
/// because VeraCrypt has no COM/native .NET API surface. This is a core
/// architectural requirement of the AD Shield backup concept.
/// </summary>
public static class VeraCryptManager
{
    // ── Mount ─────────────────────────────────────────────────────────────────

    public static void Mount(AppSettings settings, string password, IProgress<string>? progress = null)
    {
        if (!File.Exists(settings.VeraCryptExePath))
            throw new FileNotFoundException(
                $"VeraCrypt executable not found at: {settings.VeraCryptExePath}\n" +
                "Update the path in System Config → VeraCrypt Settings.");

        if (IsMounted(settings.MountLetter))
        {
            progress?.Report($"[INFO] VeraCrypt volume already mounted at {settings.MountLetter}:");
            return;
        }

        var containerPath = ResolveUncPath(settings.VeraCryptContainer);
        progress?.Report($"[INFO] Mounting VeraCrypt container: {containerPath} → {settings.MountLetter}:");

        // Core Process.Start exception — VeraCrypt has no managed API
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName         = settings.VeraCryptExePath,
            UseShellExecute  = false,
            CreateNoWindow   = true,   // suppress GUI — capture stderr instead
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        // ArgumentList handles quoting automatically — prevents password/path breakage
        foreach (var arg in new[]
        {
            "/volume", containerPath,
            "/letter", settings.MountLetter,
            "/password", password,
            "/silent", "/quit", "/nowaitdlg",
        })
            psi.ArgumentList.Add(arg);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception("Failed to start VeraCrypt process.");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();

        bool exited = proc.WaitForExit(90_000); // 90s for network volumes

        if (!exited)
        {
            try { proc.Kill(); } catch { }
            throw new Exception(
                $"VeraCrypt mount timed out after 90 seconds.\n" +
                $"Container: {containerPath}\n" +
                "The network share may be slow or unreachable.");
        }

        if (proc.ExitCode != 0)
        {
            var vcOutput = $"{stdout.Trim()} {stderr.Trim()}".Trim();
            progress?.Report($"[WARN] VeraCrypt exited with code {proc.ExitCode}. Output: {(string.IsNullOrEmpty(vcOutput) ? "(none)" : vcOutput)}");
        }

        // Network-hosted containers can take a few seconds after VeraCrypt exits
        // before Windows fully registers the drive letter. Poll for up to 10s.
        bool mounted = false;
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            if (IsMounted(settings.MountLetter))
            {
                mounted = true;
                break;
            }
            progress?.Report($"[INFO] Waiting for drive {settings.MountLetter}: to register (attempt {attempt}/10)...");
            Thread.Sleep(1000);
        }

        if (!mounted)
        {
            var vcOutput = $"{stdout.Trim()} {stderr.Trim()}".Trim();
            throw new Exception(
                $"VeraCrypt mount did not succeed (exit code {proc.ExitCode}).\n" +
                $"Container: {containerPath}\n" +
                $"Drive letter: {settings.MountLetter}\n" +
                $"VeraCrypt output: {(string.IsNullOrEmpty(vcOutput) ? "(none)" : vcOutput)}\n" +
                "Check the passphrase and that the container file exists on the network.");
        }

        progress?.Report($"[SUCCESS] VeraCrypt container mounted at {settings.MountLetter}:");
    }

    // ── Dismount ──────────────────────────────────────────────────────────────

    public static void Dismount(AppSettings settings, IProgress<string>? progress = null)
    {
        if (!IsMounted(settings.MountLetter))
        {
            progress?.Report($"[INFO] Volume {settings.MountLetter}: is not mounted.");
            return;
        }

        progress?.Report($"[INFO] Dismounting VeraCrypt volume {settings.MountLetter}:");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName        = settings.VeraCryptExePath,
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
        foreach (var arg in new[] { "/dismount", settings.MountLetter, "/silent", "/quit" })
            psi.ArgumentList.Add(arg);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception("Failed to start VeraCrypt dismount process.");
        proc.WaitForExit(30_000);

        progress?.Report(!IsMounted(settings.MountLetter)
            ? $"[SUCCESS] VeraCrypt volume {settings.MountLetter}: dismounted."
            : $"[WARN] Dismount may have failed. Volume {settings.MountLetter}: still appears mounted.");
    }

    // ── Create New Container ──────────────────────────────────────────────────

    public static void CreateContainer(AppSettings settings, string password, string sizeSpec,
        IProgress<string>? progress = null)
    {
        // Volume CREATION uses "VeraCrypt Format.exe", not the main VeraCrypt.exe
        var formatExe = Path.Combine(
            Path.GetDirectoryName(settings.VeraCryptExePath) ?? "",
            "VeraCrypt Format.exe");

        if (!File.Exists(formatExe))
            throw new FileNotFoundException(
                $"VeraCrypt Format.exe not found at: {formatExe}\n" +
                "It should be in the same folder as VeraCrypt.exe.");
        // Resolve mapped drive letters to UNC paths (elevated process can't see them)
        var containerPath = ResolveUncPath(settings.VeraCryptContainer);

        // Ensure the parent directory for the container exists
        var containerDir = Path.GetDirectoryName(containerPath);
        if (!string.IsNullOrEmpty(containerDir) && !Directory.Exists(containerDir))
            Directory.CreateDirectory(containerDir);

        progress?.Report($"[INFO] Creating VeraCrypt container ({sizeSpec}) at {containerPath}...");
        progress?.Report($"[INFO] Using: {formatExe}");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName        = formatExe,
            UseShellExecute = false,
            CreateNoWindow  = false,  // Let VeraCrypt Format show its window for error visibility
        };
        // Use ArgumentList for reliable quoting
        foreach (var arg in new[]
        {
            "/create", containerPath,
            "/size", sizeSpec,
            "/password", password,
            "/hash", "sha-512",
            "/encryption", "AES",
            "/filesystem", "NTFS",
            "/force",
            "/silent",
        })
            psi.ArgumentList.Add(arg);

        progress?.Report($"[INFO] Launching VeraCrypt Format — watch for its window...");

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception("Failed to start VeraCrypt Format process.");

        proc.WaitForExit(300_000); // up to 5 min for very large volumes

        progress?.Report($"[INFO] VeraCrypt Format exited with code {proc.ExitCode}");

        if (!File.Exists(containerPath))
            throw new Exception(
                $"VeraCrypt container creation failed (exit code {proc.ExitCode}).\n" +
                $"File not found: {containerPath}\n\n" +
                "If VeraCrypt showed an error, note it and check:\n" +
                "• Does the destination folder exist and is it writable?\n" +
                "• Try using the full UNC path directly (e.g. \\\\server\\share\\vault.hc)");

        progress?.Report("[SUCCESS] VeraCrypt encrypted container created.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Returns true if the configured mount letter resolves to an accessible drive.</summary>
    public static bool IsMounted(string mountLetter) =>
        DriveInfo.GetDrives()
                 .Any(d => d.Name.StartsWith(mountLetter, StringComparison.OrdinalIgnoreCase)
                        && d.IsReady);

    public static bool VeraCryptInstalled(string exePath) => File.Exists(exePath);

    // ── UNC resolution (fixes mapped drives invisible to elevated processes) ───

    /// <summary>
    /// If the path starts with a mapped drive letter (e.g. Z:\), resolves it to the
    /// UNC path (e.g. \\server\share\). Elevated processes can't see drive mappings
    /// from the non-elevated session, so we look up the mapping in the registry
    /// (HKCU\Network\Z → RemotePath) which persists across sessions.
    /// </summary>
    public static string ResolveUncPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':')
            return path;

        // Already a UNC path
        if (path.StartsWith(@"\\"))
            return path;

        var letter = char.ToUpper(path[0]).ToString();

        // Method 1: Registry lookup (works from elevated processes for persistent mappings)
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Network\{letter}");
            var remotePath = key?.GetValue("RemotePath") as string;
            if (!string.IsNullOrEmpty(remotePath))
                return remotePath + path[2..];
        }
        catch { /* registry key doesn't exist */ }

        // Method 2: WMI Win32_LogicalDisk (catches non-persistent mappings sometimes)
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProviderName FROM Win32_LogicalDisk WHERE DeviceID = '{letter}:'");
            foreach (var obj in searcher.Get())
            {
                var provider = obj["ProviderName"]?.ToString();
                if (!string.IsNullOrEmpty(provider))
                    return provider + path[2..];
            }
        }
        catch { /* WMI not available */ }

        return path; // not a mapped drive or resolution failed — use as-is
    }
}
