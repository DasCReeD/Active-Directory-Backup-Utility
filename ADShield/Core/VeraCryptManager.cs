using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

public static async Task MountAsync(AppSettings settings, string password, IProgress<string>? progress = null, CancellationToken ct = default)
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

        var argumentList = new List<string>
        {
            "/volume", containerPath,
            "/letter", settings.MountLetter,
            "/password", password,
            "/silent", "/quit", "/nowaitdlg",
        };

        var result = await ProcessRunner.RunAsync(
            settings.VeraCryptExePath,
            argumentList: argumentList,
            timeout: TimeSpan.FromSeconds(90),
            progress: progress,
            ct: ct);

        if (result.ExitCode != 0)
        {
            var vcOutput = $"{result.StandardOutput.Trim()} {result.StandardError.Trim()}".Trim();
            progress?.Report($"[WARN] VeraCrypt exited with code {result.ExitCode}. Output: {(string.IsNullOrEmpty(vcOutput) ? "(none)" : vcOutput)}");
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
            await Task.Delay(1000, ct);
        }

        if (!mounted)
        {
            var vcOutput = $"{result.StandardOutput.Trim()} {result.StandardError.Trim()}".Trim();
            throw new Exception(
                $"VeraCrypt mount did not succeed (exit code {result.ExitCode}).\n" +
                $"Container: {containerPath}\n" +
                $"Drive letter: {settings.MountLetter}\n" +
                $"VeraCrypt output: {(string.IsNullOrEmpty(vcOutput) ? "(none)" : vcOutput)}\n" +
                "Check the passphrase and that the container file exists on the network.");
        }

        progress?.Report($"[SUCCESS] VeraCrypt container mounted at {settings.MountLetter}:");
    }

    // ── Dismount ──────────────────────────────────────────────────────────────

    public static async Task DismountAsync(AppSettings settings, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsMounted(settings.MountLetter))
        {
            progress?.Report($"[INFO] Volume {settings.MountLetter}: is not mounted.");
            return;
        }

        progress?.Report($"[INFO] Dismounting VeraCrypt volume {settings.MountLetter}:");

        var argumentList = new List<string> { "/dismount", settings.MountLetter, "/silent", "/quit" };

        await ProcessRunner.RunAsync(
            settings.VeraCryptExePath,
            argumentList: argumentList,
            timeout: TimeSpan.FromSeconds(30),
            progress: progress,
            ct: ct);

        progress?.Report(!IsMounted(settings.MountLetter)
            ? $"[SUCCESS] VeraCrypt volume {settings.MountLetter}: dismounted."
            : $"[WARN] Dismount may have failed. Volume {settings.MountLetter}: still appears mounted.");
    }

    // ── Create New Container ──────────────────────────────────────────────────

    public static async Task CreateContainerAsync(AppSettings settings, string password, string sizeSpec,
        IProgress<string>? progress = null, CancellationToken ct = default)
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

        var argumentList = new List<string>
        {
            "/create", containerPath,
            "/size", sizeSpec,
            "/password", password,
            "/hash", "sha-512",
            "/encryption", "AES",
            "/filesystem", "NTFS",
            "/force",
            "/silent",
        };

        progress?.Report($"[INFO] Launching VeraCrypt Format — wait for completion...");

        var result = await ProcessRunner.RunAsync(
            formatExe,
            argumentList: argumentList,
            timeout: TimeSpan.FromMinutes(5),
            progress: progress,
            ct: ct);

        progress?.Report($"[INFO] VeraCrypt Format exited with code {result.ExitCode}");

        if (!File.Exists(containerPath))
            throw new Exception(
                $"VeraCrypt container creation failed (exit code {result.ExitCode}).\n" +
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
