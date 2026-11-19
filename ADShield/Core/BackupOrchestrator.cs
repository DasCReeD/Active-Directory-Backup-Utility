using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using ADShield.Models;

namespace ADShield.Core
{
    /// <summary>
    /// 100% agentless, server-only backup orchestration:
    /// 1. Verify / mount VeraCrypt vault locally.
    /// 2. Ping target host to verify ICMP reachability.
    /// 3. Verify remote WMI connectivity (root\cimv2).
    /// 4. Prepare hardened per-machine folder and auto-sized VHDX container.
    /// 5. Dynamically allocate an unused drive letter (Z..M), attach VHDX via VirtDisk.dll,
    ///    initialize/assign letter via diskpart, and self-heal if not writable.
    /// 6. Create remote VSS shadow copy and expose it via a temporary directory symlink.
    /// 7. Pull backup data via local robocopy.exe (/B) directly into the mounted VHDX.
    /// 8. Always clean up remote symlink, remote VSS shadow copy, and local VHDX mount.
    /// </summary>
    public class BackupOrchestrator
    {
        private readonly AppSettings _settings;

        public BackupOrchestrator(AppSettings settings)
        {
            _settings = settings;
        }

        public async Task RunAsync(
            string computerName,
            string backupType,
            string veraCryptPassword,
            IProgress<string> progress,
            CancellationToken ct = default)
        {
            Log(progress, "INFO", $"Starting {backupType} backup sequence for {computerName} (Agentless)");

            // ── Step 1: Verify / mount VeraCrypt vault ────────────────────────────
            bool isMounted = await Task.Run(() => VeraCryptManager.IsMounted(_settings.MountLetter), ct);
            if (!isMounted)
            {
                Log(progress, "INFO", "VeraCrypt volume not mounted. Attempting to mount...");
                await VeraCryptManager.MountAsync(_settings, veraCryptPassword, progress, ct);
            }
            else
            {
                Log(progress, "INFO", $"VeraCrypt volume is ready at {_settings.MountLetter}:");
            }
            ct.ThrowIfCancellationRequested();

            // ── Step 2: Ping target ───────────────────────────────────────────────
            Log(progress, "INFO", $"Pinging {computerName}...");
            using (var ping = new Ping())
            {
                var reply = await ping.SendPingAsync(computerName, 1500).WaitAsync(ct);
                if (reply.Status != IPStatus.Success)
                    throw new Exception($"{computerName} is offline or unreachable (ICMP failed: {reply.Status}).");
                Log(progress, "INFO", $"Ping OK — {reply.RoundtripTime} ms");
            }
            ct.ThrowIfCancellationRequested();

            // ── Step 3: Verify WMI connectivity ───────────────────────────────────
            Log(progress, "INFO", $"Verifying WMI connectivity to {computerName}...");
            string wmiError = string.Empty;
            bool wmiOk = await Task.Run(() => VssManager.TestWmiConnectivity(computerName, out wmiError), ct);
            if (!wmiOk)
            {
                throw new Exception($"WMI connectivity check failed on {computerName}: {wmiError}");
            }
            Log(progress, "SUCCESS", $"WMI connectivity verified for {computerName}.");
            ct.ThrowIfCancellationRequested();

            // ── Step 4: Prepare per-machine folder & VHDX ─────────────────────────
            var clientFolder = Path.Combine(_settings.BackupRootPath, computerName);
            var vhdxPath = Path.Combine(clientFolder, "disk.vhdx");

            Log(progress, "INFO", $"Configuring storage at {clientFolder}...");
            Directory.CreateDirectory(clientFolder);
            await ApplyHardenedAclAsync(clientFolder, progress, ct);

            bool isNewVhdx = !VhdxManager.VhdxExists(vhdxPath);
            if (isNewVhdx)
            {
                long vhdxSizeGb = await CalculateNewVhdxSizeGbAsync(computerName, progress, ct);
                Log(progress, "INFO", $"Creating {vhdxSizeGb} GB VHDX at {vhdxPath}...");
                ulong sizeBytes = (ulong)vhdxSizeGb * 1024 * 1024 * 1024;
                await Task.Run(() => VhdxManager.CreateVhdx(vhdxPath, sizeBytes, progress: progress), ct);
            }
            else
            {
                Log(progress, "INFO", "Existing VHDX container found.");
            }
            ct.ThrowIfCancellationRequested();

            bool vhdxMounted = false;
            string? shadowId = null;
            string? remoteSymlinkPath = null;
            bool symlinkCreated = false;

            try
            {
                // ── Step 5: Attach VHDX & allocate free drive letter ──────────────
                var vhdxDriveLetter = AllocateFreeDriveLetter();
                Log(progress, "INFO", $"Allocated drive letter {vhdxDriveLetter}: for VHDX {vhdxPath}");

                await Task.Run(() => VhdxManager.AttachVhdx(vhdxPath, progress), ct);
                vhdxMounted = true;
                ct.ThrowIfCancellationRequested();

                if (isNewVhdx)
                {
                    var initScript =
                        $"select vdisk file=\"{vhdxPath}\"\r\n" +
                        $"clean\r\n" +
                        $"convert gpt\r\n" +
                        $"create partition primary\r\n" +
                        $"format fs=ntfs label=\"ADShield\" quick\r\n" +
                        $"assign letter={vhdxDriveLetter} NOERR\r\n";

                    Log(progress, "INFO", $"Initializing fresh local VHDX as {vhdxDriveLetter}:...");
                    await RunLocalDiskpart(initScript, progress, ct);
                }
                else
                {
                    Log(progress, "INFO", $"Assigning existing VHDX partition to {vhdxDriveLetter}:...");
                    await AssignExistingVhdxLetterAsync(vhdxPath, vhdxDriveLetter, progress, ct);
                }

                // Self-heal write probe
                if (!TryProbeDriveWritable(vhdxDriveLetter))
                {
                    Log(progress, "WARN", $"Drive {vhdxDriveLetter}:\\ is not writable or unformatted. Re-initializing partition and formatting NTFS...");
                    var recoveryScript =
                        $"select vdisk file=\"{vhdxPath}\"\r\n" +
                        $"clean\r\n" +
                        $"convert gpt\r\n" +
                        $"create partition primary\r\n" +
                        $"format fs=ntfs label=\"ADShield\" quick\r\n" +
                        $"assign letter={vhdxDriveLetter} NOERR\r\n";
                    await RunLocalDiskpart(recoveryScript, progress, ct);

                    if (!TryProbeDriveWritable(vhdxDriveLetter))
                    {
                        throw new Exception($"Failed to mount, format and write to local drive {vhdxDriveLetter}:\\");
                    }
                }

                await ApplyHardenedAclAsync($"{vhdxDriveLetter}:\\.", progress, ct);
                Log(progress, "SUCCESS", $"Local VHDX ready at {vhdxDriveLetter}:\\");
                ct.ThrowIfCancellationRequested();

                // ── Step 6: Remote VSS shadow copy & symlink exposure ─────────────
                shadowId = await Task.Run(
                    () => VssManager.CreateRemoteShadowCopy(computerName, @"C:\", progress: progress),
                    ct);

                var deviceObject = await Task.Run(
                    () => VssManager.GetShadowCopyDeviceObject(computerName, shadowId),
                    ct);

                var sanitizedId = new string(shadowId.Where(char.IsLetterOrDigit).ToArray());
                var symlinkName = $"_adshield_vss_{sanitizedId}";
                remoteSymlinkPath = $@"C:\{symlinkName}";
                var uncSymlinkPath = $@"\\{computerName}\C$\{symlinkName}";

                Log(progress, "INFO", $"Exposing remote VSS snapshot via symlink {remoteSymlinkPath} -> {deviceObject}");
                await RunRemoteProcessAsync(
                    computerName,
                    $"cmd.exe /c mklink /d \"{remoteSymlinkPath}\" \"{deviceObject}\"",
                    ct);
                symlinkCreated = true;

                // Win32_Process.Create returns when cmd.exe starts; wait briefly for mklink to finish.
                for (int attempt = 0; attempt < 10 && !Directory.Exists(uncSymlinkPath); attempt++)
                {
                    await Task.Delay(300, ct);
                }
                ct.ThrowIfCancellationRequested();

                // ── Step 7: Server-pull copy into mounted VHDX via robocopy ───────
                var localDest = Path.Combine($"{vhdxDriveLetter}:\\", backupType);
                Directory.CreateDirectory(localDest);

                Log(progress, "INFO", $"Pulling backup data: {uncSymlinkPath} -> {localDest}");
                await RunLocalDataCopy(uncSymlinkPath, localDest, computerName, progress, ct);
                Log(progress, "SUCCESS", "Backup data successfully pulled and encrypted inside the VHDX.");

                AppConfig.UpdateBackupResult(computerName, "Success");
                Log(progress, "SUCCESS", $"Backup sequence for {computerName} completed successfully!");
            }
            catch (Exception ex)
            {
                AppConfig.UpdateBackupResult(computerName, $"Failed: {ex.Message}");
                throw;
            }
            finally
            {
                // ── Step 8: Cleanup (always runs) ─────────────────────────────────
                if (symlinkCreated && !string.IsNullOrEmpty(remoteSymlinkPath))
                {
                    try
                    {
                        Log(progress, "INFO", $"Removing remote VSS symlink {remoteSymlinkPath} on {computerName}...");
                        await RunRemoteProcessAsync(
                            computerName,
                            $"cmd.exe /c if exist \"{remoteSymlinkPath}\" rmdir \"{remoteSymlinkPath}\"",
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log(progress, "WARN", $"Could not remove remote symlink {remoteSymlinkPath}: {ex.Message}");
                    }
                }

                if (!string.IsNullOrEmpty(shadowId))
                {
                    try
                    {
                        await Task.Run(
                            () => VssManager.DeleteRemoteShadowCopy(computerName, shadowId, progress),
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log(progress, "WARN", $"Could not delete remote shadow copy {shadowId}: {ex.Message}");
                    }
                }

                if (vhdxMounted)
                {
                    await DetachVhdxWithFallbackAsync(vhdxPath, progress);
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static void Log(IProgress<string> p, string level, string msg) =>
            p.Report($"[{level}] {msg}");

        private static string AllocateFreeDriveLetter()
        {
            var used = new HashSet<char>(
                DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));

            for (char c = 'Z'; c >= 'M'; c--)
            {
                if (!used.Contains(c))
                    return c.ToString();
            }

            throw new InvalidOperationException("No free drive letters available in range Z..M.");
        }

        private static bool TryProbeDriveWritable(string driveLetter)
        {
            try
            {
                var root = $"{driveLetter}:\\";
                if (!Directory.Exists(root))
                    return false;

                var testFile = Path.Combine(root, "adshield_write_test.txt");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<long> CalculateNewVhdxSizeGbAsync(
            string computerName,
            IProgress<string> progress,
            CancellationToken ct)
        {
            long vhdxSizeGb = _settings.VhdxSizeGb;
            try
            {
                var (usedGb, totalGb) = await Task.Run(
                    () => VssManager.QueryRemoteDiskSpace(computerName, "C:"),
                    ct);

                if (totalGb > 0)
                {
                    vhdxSizeGb = Math.Max(10, (long)Math.Ceiling(usedGb * 1.2));
                    Log(progress, "INFO", $"Remote C: drive: {usedGb:F1} GB used / {totalGb:F1} GB total");
                    Log(progress, "INFO", $"VHDX will be sized to {vhdxSizeGb} GB (used + 20% headroom)");
                }
                else
                {
                    Log(progress, "WARN", "Remote disk reported 0 GB total. Falling back to configured VHDX size.");
                }
            }
            catch (Exception ex)
            {
                Log(progress, "WARN", $"Could not query remote disk usage ({ex.Message}). Falling back to configured size ({vhdxSizeGb} GB).");
            }

            try
            {
                var mountDrive = new DriveInfo($"{_settings.MountLetter}:\\");
                var freeGb = mountDrive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                if (freeGb < vhdxSizeGb)
                {
                    Log(progress, "WARN", $"VeraCrypt volume has {freeGb:F1} GB free but VHDX needs {vhdxSizeGb} GB.");
                    vhdxSizeGb = Math.Max(10, (long)Math.Floor(freeGb * 0.9));
                    Log(progress, "INFO", $"Reducing VHDX size to {vhdxSizeGb} GB to fit available space.");
                }
            }
            catch
            {
                // DriveInfo unavailable — proceed with calculated size
            }

            return vhdxSizeGb;
        }

        private static async Task ApplyHardenedAclAsync(
            string targetPath,
            IProgress<string> progress,
            CancellationToken ct)
        {
            // Grant FullControl only to BUILTIN\Administrators (*S-1-5-32-544) and NT AUTHORITY\SYSTEM (*S-1-5-18).
            // Never grant Everyone, Authenticated Users, or Domain Computers.
            var sidArgs = new[]
            {
                $"\"{targetPath}\" /grant *S-1-5-32-544:(OI)(CI)F",
                $"\"{targetPath}\" /grant *S-1-5-18:(OI)(CI)F"
            };

            foreach (var args in sidArgs)
            {
                try
                {
                    var result = await ProcessRunner.RunAsync("icacls.exe", arguments: args, ct: ct);
                    if (result.ExitCode != 0)
                    {
                        Log(progress, "WARN", $"icacls failed (code {result.ExitCode}) for {args}: {result.StandardError.Trim()} {result.StandardOutput.Trim()}");
                    }
                }
                catch (Exception ex)
                {
                    Log(progress, "WARN", $"Could not apply hardened NTFS ACL ({args}): {ex.Message}");
                }
            }
        }

        private static async Task RunRemoteProcessAsync(
            string computerName,
            string commandLine,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                var options = new ConnectionOptions
                {
                    Impersonation = ImpersonationLevel.Impersonate,
                    EnablePrivileges = true,
                    Timeout = TimeSpan.FromSeconds(30)
                };

                var scope = new ManagementScope($@"\\{computerName}\root\cimv2", options);
                scope.Connect();

                using var processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
                using var inParams = processClass.GetMethodParameters("Create");
                inParams["CommandLine"] = commandLine;

                using var outParams = processClass.InvokeMethod("Create", inParams, null)
                    ?? throw new Exception($"Win32_Process.Create returned null on {computerName}.");

                var returnVal = Convert.ToUInt32(outParams["ReturnValue"]);
                if (returnVal != 0)
                {
                    throw new Exception($"Remote Win32_Process.Create failed on {computerName} (ReturnValue: {returnVal}) for: {commandLine}");
                }
            }, ct);
        }

        private static async Task DetachVhdxWithFallbackAsync(string vhdxPath, IProgress<string> progress)
        {
            try
            {
                await Task.Run(() => VhdxManager.DetachVhdx(vhdxPath, progress), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log(progress, "WARN", $"VirtDisk DetachVhdx failed ({ex.Message}); falling back to diskpart detach...");
                try
                {
                    var detachScript = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                    await RunLocalDiskpart(detachScript, progress, CancellationToken.None);
                    Log(progress, "SUCCESS", "Local VHDX unmounted via diskpart fallback.");
                }
                catch (Exception diskpartEx)
                {
                    Log(progress, "WARN", $"Could not unmount local VHDX: {diskpartEx.Message}");
                }
            }
        }

        internal static async Task AssignExistingVhdxLetterAsync(
            string vhdxPath,
            string driveLetter,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var part2Script =
                $"select vdisk file=\"{vhdxPath}\"\r\n" +
                $"online disk NOERR\r\n" +
                $"select partition 2\r\n" +
                $"assign letter={driveLetter} NOERR\r\n";
            try
            {
                await RunLocalDiskpart(part2Script, progress, ct);
            }
            catch
            {
                var part1Script =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    $"online disk NOERR\r\n" +
                    $"select partition 1\r\n" +
                    $"assign letter={driveLetter} NOERR\r\n";
                await RunLocalDiskpart(part1Script, progress, ct);
            }
        }

        internal static async Task RunLocalDiskpart(
            string diskpartScript,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var tempScript = Path.Combine(Path.GetTempPath(), $"adshield_{Guid.NewGuid():N}.txt");
            File.WriteAllText(tempScript, diskpartScript);

            try
            {
                var progressWrapper = progress != null
                    ? new Progress<string>(line => progress.Report($"[INFO]   {line.Trim()}"))
                    : null;
                progress?.Report("[INFO] --- Local Diskpart Output ---");

                var result = await ProcessRunner.RunAsync(
                    "diskpart.exe",
                    arguments: $"/s \"{tempScript}\"",
                    progress: progressWrapper,
                    ct: ct);

                progress?.Report("[INFO] -----------------------------");

                if (result.ExitCode != 0)
                {
                    var detail = $"{result.StandardOutput.Trim()} {result.StandardError.Trim()}".Trim();
                    throw new Exception(
                        $"diskpart.exe failed with exit code {result.ExitCode}" +
                        (string.IsNullOrEmpty(detail) ? "." : $": {detail}"));
                }
            }
            finally
            {
                try { File.Delete(tempScript); } catch { }
            }
        }

        private async Task RunLocalDataCopy(
            string uncSource,
            string localDest,
            string computerName,
            IProgress<string> progress,
            CancellationToken ct)
        {
            var logFile = Path.Combine(Path.GetTempPath(), $"adshield_robocopy_{computerName}.log");

            var argumentList = new List<string>
            {
                uncSource, localDest,
                "/E", "/COPY:DAT", "/B", "/R:1", "/W:1", "/NP", "/XJ",
                "/XD", "System Volume Information", "$Recycle.Bin", "$WinREAgent", "Recovery",
                $"/LOG:{logFile}"
            };

            Log(progress, "INFO", $"Server-pull robocopy: robocopy {string.Join(" ", argumentList)}");

            ProcessRunnerResult result;
            try
            {
                result = await ProcessRunner.RunAsync(
                    "robocopy.exe",
                    argumentList: argumentList,
                    timeout: TimeSpan.FromHours(2),
                    ct: ct);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("Local robocopy did not complete within the 2 hour timeout.");
            }

            Log(progress, "INFO", $"Robocopy exited with code {result.ExitCode}.");

            if (File.Exists(logFile))
            {
                var logLines = File.ReadAllLines(logFile);

                var summary = logLines.Skip(Math.Max(0, logLines.Length - 12)).ToList();
                foreach (var line in summary.Where(l => !string.IsNullOrWhiteSpace(l)))
                    Log(progress, "INFO", $"  {line.Trim()}");

                foreach (var line in logLines)
                {
                    if (line.Contains("ERROR ") && !line.Contains("ERROR 0 (0x00000000)"))
                    {
                        throw new Exception($"Robocopy failed: {line.Trim()}");
                    }
                }
            }

            if (result.ExitCode >= 8)
            {
                throw new Exception(
                    $"Robocopy failed with exit code {result.ExitCode}. " +
                    $"Stdout: {result.StandardOutput.Trim()} Stderr: {result.StandardError.Trim()}");
            }
        }
    }
}
