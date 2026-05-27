using ADShield.Models;

namespace ADShield.Core;

/// <summary>
/// Full agentless backup orchestration using a server-pull architecture:
/// 1. Verify VeraCrypt volume mounted
/// 2. Ping target
/// 3. WMI connectivity check
/// 4. Ensure per-machine folder + VHDX on server
/// 5. Mount VHDX locally and initialize NTFS
/// 6. Trigger remote VSS shadow copy, create symlink on client
/// 7. Run robocopy locally on the server, pulling from client admin share
/// 8. Cleanup: delete shadow copy, remove symlink, detach VHDX
/// All steps report progress via IProgress&lt;string&gt;.
/// </summary>
public class BackupOrchestrator
{
    private readonly AppSettings _settings;

    /// <summary>
    /// Initializes a new instance of <see cref="BackupOrchestrator"/> with the given application settings.
    /// </summary>
    /// <param name="settings">The loaded <see cref="AppSettings"/> including vault path, mount letter, and storage config.</param>
    public BackupOrchestrator(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Executes the complete 8-step agentless backup sequence for a single domain computer.
    /// </summary>
    /// <remarks>
    /// Pipeline steps:
    /// <list type="number">
    ///   <item>Mount VeraCrypt vault if not already mounted.</item>
    ///   <item>ICMP ping the target to confirm reachability.</item>
    ///   <item>Verify WMI remote access is available.</item>
    ///   <item>Create per-machine folder and VHDX on the backup server (auto-sized to remote C: usage + 20%).</item>
    ///   <item>Mount VHDX locally and initialize NTFS (self-heals RAW/unformatted partitions).</item>
    ///   <item>Create a hidden SMB share pointing to the mounted VHDX drive.</item>
    ///   <item>Trigger VSS shadow copy on the remote client and run robocopy push via WMI.</item>
    ///   <item>Cleanup: delete shadow copy, remove SMB share, detach VHDX.</item>
    /// </list>
    /// </remarks>
    /// <param name="computerName">The NetBIOS name of the target domain computer.</param>
    /// <param name="backupType">The backup type: <c>"Incremental"</c> or <c>"Full"</c>.</param>
    /// <param name="veraCryptPassword">The VeraCrypt vault passphrase. Held in memory only; never persisted.</param>
    /// <param name="progress">Receives <c>[LEVEL] message</c> strings throughout execution.</param>
    /// <param name="ct">Cancellation token to abort the sequence between steps.</param>
    /// <exception cref="Exception">Thrown on ICMP failure, WMI access denial, robocopy error, or mount failure.</exception>
    public async Task RunAsync(
        string computerName,
        string backupType,
        string veraCryptPassword,
        IProgress<string> progress,
        CancellationToken ct = default)
    {
        Log(progress, "INFO", $"Starting {backupType} backup sequence for {computerName}");

        // ── Step 1: Verify VeraCrypt volume ───────────────────────────────────
        if (!VeraCryptManager.IsMounted(_settings.MountLetter))
        {
            Log(progress, "INFO", "VeraCrypt volume not mounted. Attempting to mount...");
            VeraCryptManager.Mount(_settings, veraCryptPassword, progress);
        }
        else
        {
            Log(progress, "INFO", $"VeraCrypt volume is ready at {_settings.MountLetter}:");
        }
        ct.ThrowIfCancellationRequested();

        // ── Step 2: Ping target ───────────────────────────────────────────────
        Log(progress, "INFO", $"Pinging {computerName}...");
        using (var ping = new System.Net.NetworkInformation.Ping())
        {
            var reply = await Task.Run(() => ping.Send(computerName, 1500), ct);
            if (reply.Status != System.Net.NetworkInformation.IPStatus.Success)
                throw new Exception($"{computerName} is offline or unreachable (ICMP failed).");
            Log(progress, "INFO", $"Ping OK — {reply.RoundtripTime} ms");
        }
        ct.ThrowIfCancellationRequested();

        // ── Step 3: WMI connectivity ──────────────────────────────────────────
        Log(progress, "INFO", "Verifying WMI remote administration access...");
        if (!VssManager.TestWmiConnectivity(computerName, out var wmiError))
            throw new Exception($"WMI not accessible on {computerName}: {wmiError}");
        Log(progress, "SUCCESS", "WMI remote connection verified.");
        ct.ThrowIfCancellationRequested();

        // ── Step 4: Prepare server-side VHDX & folder ────────────────────────
        var clientFolder  = Path.Combine(_settings.BackupRootPath, computerName);
        var vhdxPath      = Path.Combine(clientFolder, "disk.vhdx");

        Log(progress, "INFO", $"Configuring storage at {clientFolder}...");
        Directory.CreateDirectory(clientFolder);

        // Grant explicit permissions to Everyone, Authenticated Users, Domain Computers, and target computer account
        try
        {
            var domain = Environment.UserDomainName;
            var commands = new System.Collections.Generic.List<string>
            {
                $"\"{clientFolder}\" /grant Everyone:(OI)(CI)F /T",
                $"\"{clientFolder}\" /grant *S-1-5-11:(OI)(CI)F /T" // Well-known SID for Authenticated Users (language-independent)
            };

            if (!string.IsNullOrEmpty(domain))
            {
                commands.Add($"\"{clientFolder}\" /grant \"{domain}\\Domain Computers\":(OI)(CI)F /T");
                commands.Add($"\"{clientFolder}\" /grant \"{domain}\\{computerName}$\":(OI)(CI)F /T");
            }

            foreach (var args in commands)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
            }
        }
        catch (Exception ex)
        {
            Log(progress, "WARN", $"Could not set NTFS permissions on client folder: {ex.Message}");
        }

        bool isNewVhdx = false;
        if (!VhdxManager.VhdxExists(vhdxPath))
        {
            isNewVhdx = true;
            // Query the remote machine's C: drive used space to size the VHDX appropriately
            long vhdxSizeGb = _settings.VhdxSizeGb; // fallback to config
            try
            {
                var (usedGb, totalGb) = GetRemoteDiskUsage(computerName, @"C:");
                // Size VHDX to used space + 20% headroom, minimum 10 GB
                vhdxSizeGb = Math.Max(10, (long)Math.Ceiling(usedGb * 1.2));
                Log(progress, "INFO", $"Remote C: drive: {usedGb:F1} GB used / {totalGb:F1} GB total");
                Log(progress, "INFO", $"VHDX will be sized to {vhdxSizeGb} GB (used + 20% headroom)");
            }
            catch (Exception ex)
            {
                Log(progress, "WARN", $"Could not query remote disk usage: {ex.Message}");
                Log(progress, "INFO", $"Falling back to configured size: {vhdxSizeGb} GB");
            }

            // Verify the VeraCrypt container has enough free space
            try
            {
                var mountDrive = new DriveInfo(_settings.MountLetter + ":\\");
                var freeGb = mountDrive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                if (freeGb < vhdxSizeGb)
                {
                    Log(progress, "WARN", $"VeraCrypt volume has {freeGb:F1} GB free but VHDX needs {vhdxSizeGb} GB");
                    vhdxSizeGb = Math.Max(10, (long)Math.Floor(freeGb * 0.9));
                    Log(progress, "INFO", $"Reducing VHDX size to {vhdxSizeGb} GB to fit available space");
                }
            }
            catch { /* drive info unavailable — proceed with calculated size */ }

            Log(progress, "INFO", $"Creating {vhdxSizeGb} GB VHDX at {vhdxPath}...");
            ulong sizeBytes = (ulong)vhdxSizeGb * 1024 * 1024 * 1024;
            await Task.Run(() => VhdxManager.CreateVhdx(vhdxPath, sizeBytes, progress: progress), ct);
        }
        else
        {
            Log(progress, "INFO", "Existing VHDX container found.");
        }
        ct.ThrowIfCancellationRequested();

        string? shadowId = null;
        bool vhdxMounted = false;
        bool vssSymlinkCreated = false;
        var vhdxDriveLetter = "B"; // temp letter for the VHDX locally on the backup server

        try
        {
            // ── Step 5: Mount VHDX locally on the backup server ─────────────────
            Log(progress, "INFO", $"Mounting VHDX locally at {vhdxPath}...");
            var mountScript = $"select vdisk file=\"{vhdxPath}\"\r\nattach vdisk";
            await RunLocalDiskpart(mountScript, progress, ct);
            vhdxMounted = true;
            Log(progress, "SUCCESS", "VHDX mounted locally.");
            ct.ThrowIfCancellationRequested();

            // ── Step 6: Initialize VHDX locally (partition, format, assign letter) ──
            string initScript;

            if (isNewVhdx)
            {
                Log(progress, "INFO", $"Initializing fresh local VHDX — partitioning, formatting, and assigning drive {vhdxDriveLetter}:...");
                initScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    $"clean\r\n" +
                    $"convert gpt\r\n" +
                    $"create partition primary\r\n" +
                    $"format fs=ntfs label=\"ADShield\" quick\r\n" +
                    $"assign letter={vhdxDriveLetter} NOERR\r\n";
            }
            else
            {
                Log(progress, "INFO", $"Mounting existing local VHDX — assigning drive {vhdxDriveLetter}:...");
                initScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    $"online disk NOERR\r\n" +
                    $"select partition 1\r\n" +
                    $"assign letter={vhdxDriveLetter} NOERR\r\n";
            }

            await RunLocalDiskpart(initScript, progress, ct);

            // Self-heal: Verify if drive B: exists and is fully formatted/writable.
            // If not (uninitialized/RAW disk), clean, partition, and format it.
            bool isWritable = false;
            try
            {
                if (Directory.Exists($"{vhdxDriveLetter}:\\"))
                {
                    var testFile = Path.Combine($"{vhdxDriveLetter}:\\", "adshield_write_test.txt");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    isWritable = true;
                }
            }
            catch
            {
                isWritable = false;
            }

            if (!isWritable)
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
                
                // Verify again after recovery
                try
                {
                    if (Directory.Exists($"{vhdxDriveLetter}:\\"))
                    {
                        var testFile = Path.Combine($"{vhdxDriveLetter}:\\", "adshield_write_test.txt");
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                        isWritable = true;
                    }
                }
                catch
                {
                    isWritable = false;
                }
            }

            if (!isWritable)
                throw new Exception($"Failed to mount, format and write to local drive {vhdxDriveLetter}:\\");

            Log(progress, "SUCCESS", $"Local VHDX initialized as {vhdxDriveLetter}:");
            ct.ThrowIfCancellationRequested();

            // Grant explicit NTFS permissions on the mounted local VHDX drive root B:\ to remote computer account
            try
            {
                var domain = Environment.UserDomainName;
                var commands = new System.Collections.Generic.List<string>
                {
                    $"\"{vhdxDriveLetter}:\\.\" /grant Everyone:(OI)(CI)F /T",
                    $"\"{vhdxDriveLetter}:\\.\" /grant *S-1-5-11:(OI)(CI)F /T" // Authenticated Users
                };

                if (!string.IsNullOrEmpty(domain))
                {
                    commands.Add($"\"{vhdxDriveLetter}:\\.\" /grant \"{domain}\\Domain Computers\":(OI)(CI)F /T");
                    commands.Add($"\"{vhdxDriveLetter}:\\.\" /grant \"{domain}\\{computerName}$\":(OI)(CI)F /T");
                }

                foreach (var args in commands)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe", args)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        string stdout = proc.StandardOutput.ReadToEnd();
                        string stderr = proc.StandardError.ReadToEnd();
                        proc.WaitForExit();
                        if (proc.ExitCode != 0)
                        {
                            Log(progress, "WARN", $"icacls failed (code {proc.ExitCode}) for: {args}. Error: {stderr.Trim()} {stdout.Trim()}");
                        }
                        else
                        {
                            Log(progress, "INFO", $"icacls permission applied successfully: {args}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(progress, "WARN", $"Could not set NTFS permissions on VHDX root drive {vhdxDriveLetter}:\\ : {ex.Message}");
            }

            // ── Step 6b: Enable R2L symlink evaluation on the remote client ──────
            Log(progress, "INFO", $"Ensuring R2L symlink evaluation is enabled on {computerName}...");
            await Task.Run(() => EnableRemoteSymlinkEvaluation(computerName, progress), ct);
            ct.ThrowIfCancellationRequested();

            // ── Step 7: Trigger VSS on remote client ──────────────────────────────
            Log(progress, "INFO", $"Triggering VSS shadow copy on {computerName} (C:\\)...");
            shadowId = await Task.Run(() =>
                VssManager.CreateRemoteShadowCopy(computerName, @"C:\", progress: progress), ct);
            Log(progress, "SUCCESS", $"VSS shadow copy created. ID: {shadowId}");
            ct.ThrowIfCancellationRequested();

            // ── Step 7b: Create symlink on client pointing to VSS shadow ──────────
            Log(progress, "INFO", "Looking up VSS shadow device path...");
            var shadowDevicePath = await Task.Run(() =>
                GetShadowDevicePath(computerName, shadowId), ct);
            Log(progress, "INFO", $"Shadow device: {shadowDevicePath}");

            var tempLinkPath = @"C:\adshield_vss_link";
            Log(progress, "INFO", $"Creating VSS symlink on {computerName}...");
            await CreateRemoteVssSymlink(computerName, tempLinkPath, shadowDevicePath, progress, ct);
            vssSymlinkCreated = true;
            ct.ThrowIfCancellationRequested();

            // ── Step 7c: Server-pull robocopy from client admin share → local VHDX ──
            var uncSource = $"\\\\{computerName}\\C$\\adshield_vss_link";
            Log(progress, "INFO", $"Copying data: {uncSource} → {vhdxDriveLetter}:\\");
            Log(progress, "INFO", "This may take a while depending on data size. Please wait...");
            await RunLocalDataCopy(uncSource, $"{vhdxDriveLetter}:\\", computerName, progress, ct);
            Log(progress, "SUCCESS", "Server-pull data copy to virtual disk completed successfully.");
            ct.ThrowIfCancellationRequested();

            // Persist result
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
            // ── Step 7d: Remove VSS symlink on remote client ─────────────────────
            if (vssSymlinkCreated)
            {
                Log(progress, "INFO", "Cleaning up remote VSS symbolic link...");
                try { await RemoveRemoteVssSymlink(computerName, @"C:\adshield_vss_link", progress, ct); }
                catch (Exception ex) { progress.Report($"[WARN] Could not remove VSS symlink: {ex.Message}"); }
            }

            // ── Step 7e: Delete VSS shadow copy ──────────────────────────────────
            if (shadowId != null)
            {
                Log(progress, "INFO", "Cleaning up remote VSS shadow copy...");
                try { VssManager.DeleteShadowCopy(shadowId, progress); }
                catch (Exception ex) { progress.Report($"[WARN] Could not delete shadow copy: {ex.Message}"); }
            }

            // ── Step 8: Cleanup — detach VHDX locally ────────────────────────────
            if (vhdxMounted)
            {
                Log(progress, "INFO", "Unmounting VHDX locally...");
                var detachScript = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                try { await RunLocalDiskpart(detachScript, progress, ct); }
                catch (Exception ex) { progress.Report($"[WARN] Could not unmount local VHDX: {ex.Message}"); }
                Log(progress, "SUCCESS", "Local VHDX unmounted.");
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Formats and reports a leveled log message via <paramref name="p"/>.</summary>
    private static void Log(IProgress<string> p, string level, string msg) =>
        p.Report($"[{level}] {msg}");

    /// <summary>
    /// Writes a diskpart script to a temp file and runs it locally via <c>cmd.exe /c diskpart /s</c>.
    /// Captures and forwards all output to <paramref name="progress"/>.
    /// </summary>
    /// <param name="diskpartScript">The multi-line diskpart command text to execute.</param>
    /// <param name="progress">Optional progress sink for diskpart output lines.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task RunLocalDiskpart(
        string diskpartScript,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var tempScript = Path.Combine(Path.GetTempPath(), $"adshield_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempScript, diskpartScript);

        var logFile = Path.Combine(Path.GetTempPath(), $"adshield_diskpart_{Guid.NewGuid():N}.log");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c diskpart /s \"{tempScript}\" > \"{logFile}\" 2>&1")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
            }

            if (File.Exists(logFile))
            {
                progress?.Report("[INFO] --- Local Diskpart Output ---");
                var lines = File.ReadAllLines(logFile);
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        progress?.Report($"[INFO]   {line.Trim()}");
                }
                progress?.Report("[INFO] -----------------------------");
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"[WARN] Local diskpart failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempScript); } catch {}
            try { File.Delete(logFile); } catch {}
        }
    }

    /// <summary>
    /// Runs a diskpart script on a remote machine via WMI Win32_Process.Create().
    /// This is a native WMI API call — not a local Process.Start.
    /// </summary>
    private static async Task RunRemoteDiskpart(
        string computerName,
        string diskpartScript,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // Write the diskpart script directly to the remote machine's temp directory via C$ share
        var tempFileName = $"adshield_{Guid.NewGuid():N}.txt";
        var tempScript   = $@"C:\Windows\Temp\{tempFileName}";
        var remotePath   = $@"\\{computerName}\C$\Windows\Temp\{tempFileName}";

        File.WriteAllText(remotePath, diskpartScript);

        // Use WMI Win32_Process to run diskpart remotely
        var scope = new System.Management.ManagementScope($@"\\{computerName}\root\cimv2");
        scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
        scope.Options.EnablePrivileges = true;
        scope.Connect();

        using var processClass = new System.Management.ManagementClass(scope,
            new System.Management.ManagementPath("Win32_Process"), null);

        // Run diskpart with the script file and redirect output to a log file
        var logFile = $"C:\\Windows\\Temp\\adshield_diskpart_{Guid.NewGuid():N}.log";
        using var inDp = processClass.GetMethodParameters("Create");
        inDp["CommandLine"] = $"cmd.exe /c diskpart /s \"{tempScript}\" > \"{logFile}\" 2>&1";
        using var outDp = processClass.InvokeMethod("Create", inDp, null);
        var pid = Convert.ToUInt32(outDp["ProcessId"]);
        progress?.Report($"[INFO] Remote diskpart running (PID {pid})...");

        // Wait for diskpart to finish (poll via WMI)
        await WaitForRemoteProcess(scope, pid, 60_000, ct);

        // Read and log diskpart results
        try
        {
            var logPath = $@"\\{computerName}\C$\" + logFile.Substring(3);
            if (File.Exists(logPath))
            {
                progress?.Report("[INFO] --- Remote Diskpart Output ---");
                var lines = File.ReadAllLines(logPath);
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        progress?.Report($"[INFO]   {line.Trim()}");
                }
                progress?.Report("[INFO] ------------------------------");
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"[WARN] Could not read diskpart output log: {ex.Message}");
        }

        // Cleanup temp file and log file
        using var inClean = processClass.GetMethodParameters("Create");
        inClean["CommandLine"] = $"cmd.exe /c del /f /q \"{tempScript}\" \"{logFile}\"";
        processClass.InvokeMethod("Create", inClean, null);
    }

    private static async Task WaitForRemoteProcess(
        System.Management.ManagementScope scope,
        uint pid, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var q = new System.Management.ObjectQuery(
                $"SELECT * FROM Win32_Process WHERE ProcessId = {pid}");
            using var s = new System.Management.ManagementObjectSearcher(scope, q);
            if (s.Get().Count == 0) return;
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException($"Remote process {pid} did not complete within timeout.");
    }

    private static string GetLocalIpAddress()
    {
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        return host.AddressList
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                               && !System.Net.IPAddress.IsLoopback(a))
            ?.ToString()
            ?? "127.0.0.1";
    }

    /// <summary>
    /// Queries a remote machine's disk usage via WMI Win32_LogicalDisk.
    /// Returns (usedGB, totalGB) for the specified drive letter.
    /// </summary>
    private static (double usedGb, double totalGb) GetRemoteDiskUsage(string computerName, string driveLetter)
    {
        var scope = new System.Management.ManagementScope($@"\\{computerName}\root\cimv2");
        scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
        scope.Options.EnablePrivileges = true;
        scope.Connect();

        var query = new System.Management.ObjectQuery(
            $"SELECT Size, FreeSpace FROM Win32_LogicalDisk WHERE DeviceID = '{driveLetter}'");
        using var searcher = new System.Management.ManagementObjectSearcher(scope, query);
        foreach (var obj in searcher.Get())
        {
            var totalBytes = Convert.ToDouble(obj["Size"]);
            var freeBytes  = Convert.ToDouble(obj["FreeSpace"]);
            var usedBytes  = totalBytes - freeBytes;
            return (usedBytes / (1024.0 * 1024 * 1024), totalBytes / (1024.0 * 1024 * 1024));
        }
        throw new Exception($"Drive {driveLetter} not found on {computerName}");
    }

    /// <summary>
    /// Looks up the VSS shadow copy's DeviceObject path via WMI.
    /// Returns something like: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\
    /// </summary>
    private static string GetShadowDevicePath(string computerName, string shadowId)
    {
        var scope = new System.Management.ManagementScope($@"\\{computerName}\root\cimv2");
        scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
        scope.Options.EnablePrivileges = true;
        scope.Connect();

        var query = new System.Management.ObjectQuery(
            $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
        using var searcher = new System.Management.ManagementObjectSearcher(scope, query);
        foreach (var obj in searcher.Get())
        {
            var devicePath = obj["DeviceObject"]?.ToString();
            if (!string.IsNullOrEmpty(devicePath))
                return devicePath.TrimEnd('\\') + "\\";
        }
        throw new Exception($"Shadow copy {shadowId} not found on {computerName}");
    }

    /// <summary>
    /// Enables Remote-to-Local (R2L) symlink evaluation on the target machine via WMI registry write.
    /// This allows the server to traverse symlinks on the client's C$ admin share that point to
    /// local VSS device paths. Idempotent — safe to call multiple times.
    /// </summary>
    private static void EnableRemoteSymlinkEvaluation(string computerName, IProgress<string> progress)
    {
        try
        {
            var scope = new System.Management.ManagementScope($@"\\{computerName}\root\default");
            scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
            scope.Options.EnablePrivileges = true;
            scope.Connect();

            using var regClass = new System.Management.ManagementClass(scope,
                new System.Management.ManagementPath("StdRegProv"), null);

            // HKLM = 0x80000002
            uint hklm = 0x80000002;
            string subKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
            string valueName = "SymlinkRemoteToLocalEvaluation";

            using var inParams = regClass.GetMethodParameters("SetDWORDValue");
            inParams["hDefKey"] = hklm;
            inParams["sSubKeyName"] = subKey;
            inParams["sValueName"] = valueName;
            inParams["uValue"] = (uint)1;

            using var outParams = regClass.InvokeMethod("SetDWORDValue", inParams, null);
            var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
            if (retVal != 0)
                throw new Exception($"Registry write returned {retVal}");

            progress.Report("[SUCCESS] R2L symlink evaluation enabled on remote client.");
        }
        catch (Exception ex)
        {
            progress.Report($"[WARN] Could not enable R2L symlink evaluation via registry: {ex.Message}");
            progress.Report("[INFO] Falling back to fsutil via WMI process...");

            // Fallback: run fsutil remotely via WMI Win32_Process
            try
            {
                RunRemoteWmiCommand(computerName,
                    "cmd.exe /c fsutil behavior set symlinkevaluation R2L:1", 10000);
                progress.Report("[SUCCESS] R2L symlink evaluation enabled via fsutil.");
            }
            catch (Exception ex2)
            {
                progress.Report($"[WARN] fsutil fallback also failed: {ex2.Message}. Backup may fail if R2L is not already enabled.");
            }
        }
    }

    /// <summary>
    /// Creates a directory symlink on the remote client pointing to the VSS shadow device path.
    /// Uses WMI Win32_Process to run mklink on the target machine.
    /// </summary>
    private static async Task CreateRemoteVssSymlink(
        string computerName,
        string linkPath,
        string shadowDevicePath,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // Clean up any stale symlink first
        try
        {
            RunRemoteWmiCommand(computerName,
                $"cmd.exe /c if exist {linkPath} rmdir {linkPath}", 10000);
        }
        catch { /* ignore */ }

        var createCmd = $"cmd.exe /c mklink /d {linkPath} {shadowDevicePath}";
        Log(progress, "INFO", $"Creating remote symlink: {createCmd}");
        RunRemoteWmiCommand(computerName, createCmd, 15000);
        Log(progress, "SUCCESS", "Remote VSS symbolic link created.");
        await Task.CompletedTask; // maintain async signature for orchestration consistency
    }

    /// <summary>
    /// Removes the VSS symlink from the remote client.
    /// </summary>
    private static async Task RemoveRemoteVssSymlink(
        string computerName,
        string linkPath,
        IProgress<string> progress,
        CancellationToken ct)
    {
        RunRemoteWmiCommand(computerName, $"cmd.exe /c rmdir {linkPath}", 10000);
        Log(progress, "INFO", "Remote VSS symbolic link removed.");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Runs robocopy locally on the backup server, pulling data from the client's admin share
    /// (which traverses the VSS symlink via R2L evaluation) to the locally mounted VHDX drive.
    /// This eliminates the Kerberos double-hop problem entirely — single network hop only.
    /// </summary>
    private async Task RunLocalDataCopy(
        string uncSource,
        string localDest,
        string computerName,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var logFile = Path.Combine(Path.GetTempPath(), $"adshield_robocopy_{computerName}.log");

        // /E = copy subdirectories including empty ones
        // /COPY:DAT = copy Data, Attributes, Timestamps
        // /B = backup mode (uses SeBackupPrivilege to bypass ACLs)
        // /R:1 /W:1 = retry once, wait 1 second
        // /NP = no progress percentage
        // /XJ = exclude junction points to avoid infinite loops
        // /XD = exclude directories that shouldn't be copied
        // /LOG = write results to log file
        var robocopyArgs =
            $"\"{uncSource}\" \"{localDest}\" " +
            "/E /COPY:DAT /B /R:1 /W:1 /NP /XJ " +
            "/XD \"System Volume Information\" \"$Recycle.Bin\" \"$WinREAgent\" Recovery " +
            $"/LOG:\"{logFile}\"";

        Log(progress, "INFO", $"Server-pull robocopy: robocopy {robocopyArgs}");

        var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe", robocopyArgs)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
            throw new Exception("Failed to start local robocopy process.");

        // Don't block on stdout/stderr — robocopy can produce huge output
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        // Robocopy can take a LONG time — 2 hour timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromHours(2));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Local robocopy did not complete within the 2 hour timeout.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // Robocopy exit codes: 0-7 = success/warnings, 8+ = errors
        Log(progress, "INFO", $"Robocopy exited with code {proc.ExitCode}.");

        // Parse log file for detailed results
        if (File.Exists(logFile))
        {
            var logLines = File.ReadAllLines(logFile);

            // Show the summary (last 12 lines)
            var summary = logLines.Skip(Math.Max(0, logLines.Length - 12)).ToList();
            foreach (var line in summary.Where(l => !string.IsNullOrWhiteSpace(l)))
                Log(progress, "INFO", $"  {line.Trim()}");

            // Scan for critical errors
            foreach (var line in logLines)
            {
                if (line.Contains("ERROR ") && !line.Contains("ERROR 0 (0x00000000)"))
                {
                    throw new Exception($"Robocopy failed: {line.Trim()}");
                }
            }
        }

        // Exit code 8+ means a real failure
        if (proc.ExitCode >= 8)
        {
            throw new Exception($"Robocopy failed with exit code {proc.ExitCode}. Stderr: {stderr.Trim()}");
        }
    }

    /// <summary>
    /// Executes a command on a remote machine via WMI Win32_Process.Create and waits for completion.
    /// Used for lightweight operations (symlink create/delete, fsutil) — NOT for long-running data copies.
    /// </summary>
    private static void RunRemoteWmiCommand(string computerName, string commandLine, int timeoutMs)
    {
        var scope = new System.Management.ManagementScope($@"\\{computerName}\root\cimv2");
        scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
        scope.Options.EnablePrivileges = true;
        scope.Connect();

        using var processClass = new System.Management.ManagementClass(scope,
            new System.Management.ManagementPath("Win32_Process"), null);

        using var inParams = processClass.GetMethodParameters("Create");
        inParams["CommandLine"] = commandLine;

        using var outParams = processClass.InvokeMethod("Create", inParams, null);
        var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
        if (retVal != 0)
            throw new Exception($"WMI process creation failed for '{commandLine}'. Return: {retVal}");

        var pid = Convert.ToUInt32(outParams["ProcessId"]);

        // Poll for process completion
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            var query = new System.Management.ObjectQuery(
                $"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            using var searcher = new System.Management.ManagementObjectSearcher(scope, query);
            if (searcher.Get().Count == 0)
                return; // Process finished
        }
        throw new TimeoutException($"Remote command did not complete within {timeoutMs / 1000}s.");
    }
}
