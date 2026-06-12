using ADShield.Models;
using System;
using System.IO;
using System.Text;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ADShield.Core
{
    /// <summary>
    /// Full agent-based backup orchestration:
    /// 1. Verify VeraCrypt volume mounted locally.
    /// 2. Ping target client computer to verify ICMP connectivity.
    /// 3. Verify connection to the client ADShield HTTP Agent.
    /// 4. Prepare local staging directory and VHDX volume.
    /// 5. Mount VHDX locally on the server.
    /// 6. Trigger wbadmin system backup via the client agent and poll progress.
    /// 7. Copy completed staged backup into the local encrypted VHDX using Robocopy.
    /// 8. Cleanup staging directory and unmount VHDX.
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
            Log(progress, "INFO", $"Starting {backupType} backup sequence for {computerName} (Agent-Based)");

            // ── Step 1: Verify VeraCrypt volume ───────────────────────────────────
            bool isMounted = await Task.Run(() => VeraCryptManager.IsMounted(_settings.MountLetter), ct);
            if (!isMounted)
            {
                Log(progress, "INFO", "VeraCrypt volume not mounted. Attempting to mount...");
                await Task.Run(() => VeraCryptManager.Mount(_settings, veraCryptPassword, progress), ct);
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

            // ── Step 3: Verify client Agent is listening ──────────────────────────
            Log(progress, "INFO", $"Verifying connection to ADShield agent on {computerName}:{_settings.AgentPort}...");
            AgentStatusResponse? agentStatus = null;
            try
            {
                agentStatus = await Task.Run(() => GetAgentStatus(computerName, progress), ct);
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not connect to ADShield agent on {computerName} (port {_settings.AgentPort}): {ex.Message}");
            }

            if (agentStatus == null)
            {
                throw new Exception($"Could not retrieve status from ADShield agent on {computerName}.");
            }
            Log(progress, "SUCCESS", $"ADShield agent connection verified (Status: {agentStatus.Status}).");
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

                await Task.Run(() =>
                {
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
                }, ct);
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
                if (agentStatus != null && agentStatus.TotalGb > 0)
                {
                    double usedGb = agentStatus.UsedGb;
                    double totalGb = agentStatus.TotalGb;
                    // Size VHDX to used space + 20% headroom, minimum 10 GB
                    vhdxSizeGb = Math.Max(10, (long)Math.Ceiling(usedGb * 1.2));
                    Log(progress, "INFO", $"Remote C: drive: {usedGb:F1} GB used / {totalGb:F1} GB total");
                    Log(progress, "INFO", $"VHDX will be sized to {vhdxSizeGb} GB (used + 20% headroom)");
                }
                else
                {
                    Log(progress, "WARN", "Could not retrieve remote disk usage. Falling back to configured size.");
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

            bool vhdxMounted = false;
            var vhdxDriveLetter = "B"; // temp letter for the VHDX locally on the backup server
            var tempBackupDir = Path.Combine(@"E:\adshield_temp", computerName);

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
                        $"select partition 2\r\n" +
                        $"assign letter={vhdxDriveLetter} NOERR\r\n";
                }

                await RunLocalDiskpart(initScript, progress, ct);

                // Self-heal: Verify if drive B: exists and is fully formatted/writable.
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

                    await Task.Run(() =>
                    {
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
                    }, ct);
                }
                catch (Exception ex)
                {
                    Log(progress, "WARN", $"Could not set NTFS permissions on VHDX root drive {vhdxDriveLetter}:\\ : {ex.Message}");
                }

                // ── Step 5b: Configure local temporary share adshield_temp$ ─────────────────
                var tempSharePath = @"E:\adshield_temp";
                
                Directory.CreateDirectory(tempSharePath);
                Directory.CreateDirectory(tempBackupDir);
                
                // Set NTFS permission on E:\adshield_temp using icacls
                try
                {
                    var domain = Environment.UserDomainName;
                    var args = $"\"{tempSharePath}\" /grant \"{domain}\\Domain Computers\":(OI)(CI)M /T";
                    var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe", args)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Log(progress, "WARN", $"Could not set NTFS permissions on staging folder: {ex.Message}");
                }

                Log(progress, "INFO", "Configuring temporary SMB share adshield_temp$...");
                SmbShareManager.CreateStagingShare(tempSharePath, progress);
                ct.ThrowIfCancellationRequested();

                // ── Step 6: Trigger remote backup via agent ─────────────────────────────────
                Log(progress, "INFO", $"Triggering client backup via ADShield agent...");
                var backupTarget = $"\\\\FILESVR\\adshield_temp$\\{computerName}";
                
                await Task.Run(() => TriggerAgentBackup(computerName, backupTarget, progress), ct);
                Log(progress, "SUCCESS", "Agent backup initialized.");
                ct.ThrowIfCancellationRequested();

                // ── Step 6b: Monitor backup progress ────────────────────────────────────────
                Log(progress, "INFO", "Monitoring remote backup progress...");
                int lastLength = 0;
                var pollInterval = 15;
                
                while (true)
                {
                    await Task.Delay(pollInterval * 1000, ct);

                    // Fetch latest status
                    AgentStatusResponse? status = null;
                    try
                    {
                        status = await Task.Run(() => GetAgentStatus(computerName, progress), ct);
                    }
                    catch (Exception ex)
                    {
                        Log(progress, "WARN", $"Could not query agent status: {ex.Message}");
                        continue;
                    }

                    if (status == null) continue;

                    // Output new log entries
                    string newLogs = status.ProgressMessage;
                    if (newLogs.Length > lastLength)
                    {
                        var delta = newLogs.Substring(lastLength);
                        lastLength = newLogs.Length;
                        var lines = delta.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            Log(progress, "INFO", $"[Agent] {line}");
                        }
                    }

                    if (status.Status == "Success")
                    {
                        break;
                    }
                    else if (status.Status == "Failed")
                    {
                        throw new Exception($"Backup failed on client agent. Exit Code: {status.ExitCode}");
                    }
                    else if (status.Status == "Idle")
                    {
                        throw new Exception("Backup unexpectedly went idle on client agent.");
                    }
                }

                Log(progress, "SUCCESS", "Remote wbadmin system image backup completed successfully.");
                ct.ThrowIfCancellationRequested();

                // ── Step 7: Local copy from staging directory into the mounted VHDX ──────
                var srcPath = Path.Combine(tempBackupDir, "WindowsImageBackup");
                var destPath = $"{vhdxDriveLetter}:\\WindowsImageBackup";

                Log(progress, "INFO", $"Copying data into the encrypted VHDX: {srcPath} → {destPath}");
                Log(progress, "INFO", "This may take a while depending on data size. Please wait...");
                await RunLocalDataCopy(srcPath, destPath, computerName, progress, ct);
                Log(progress, "SUCCESS", "Staged backup successfully copied and encrypted inside the virtual disk.");
                ct.ThrowIfCancellationRequested();

                // Persist result
                AppConfig.UpdateBackupResult(computerName, "Success");
                Log(progress, "SUCCESS", $"Backup sequence for {computerName} completed successfully!");
            }
            catch (Exception ex)
            {
                AppConfig.UpdateBackupResult(computerName, $"Failed: {ex.Message}");
                
                // Try to cancel running backup on agent if cancelled or errored
                try
                {
                    Log(progress, "WARN", "Attempting to cancel active remote backup session...");
                    CancelAgentBackup(computerName);
                }
                catch { }

                throw;
            }
            finally
            {
                // ── Cleanup — detach VHDX locally ───────────────────────────
                if (vhdxMounted)
                {
                    Log(progress, "INFO", "Unmounting VHDX locally...");
                    var detachScript = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                    try { await RunLocalDiskpart(detachScript, progress, ct); }
                    catch (Exception ex) { progress.Report($"[WARN] Could not unmount local VHDX: {ex.Message}"); }
                    Log(progress, "SUCCESS", "Local VHDX unmounted.");
                }

                // ── Clean up local staging folder ───────────────────────────
                if (Directory.Exists(tempBackupDir))
                {
                    Log(progress, "INFO", "Cleaning up temporary staging folder...");
                    try { Directory.Delete(tempBackupDir, true); }
                    catch (Exception ex) { progress.Report($"[WARN] Could not delete staging folder: {ex.Message}"); }
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static void Log(IProgress<string> p, string level, string msg) =>
            p.Report($"[{level}] {msg}");

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

        private async Task RunLocalDataCopy(
            string uncSource,
            string localDest,
            string computerName,
            IProgress<string> progress,
            CancellationToken ct)
        {
            var logFile = Path.Combine(Path.GetTempPath(), $"adshield_robocopy_{computerName}.log");

            var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[]
            {
                uncSource, localDest,
                "/E", "/COPY:DAT", "/R:1", "/W:1", "/NP", "/XJ",
                "/XD", "System Volume Information", "$Recycle.Bin", "$WinREAgent", "Recovery",
                $"/LOG:{logFile}"
            })
                psi.ArgumentList.Add(arg);

            Log(progress, "INFO", $"Server-pull robocopy: robocopy {string.Join(" ", psi.ArgumentList)}");

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                throw new Exception("Failed to start local robocopy process.");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

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

            Log(progress, "INFO", $"Robocopy exited with code {proc.ExitCode}.");

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

            if (proc.ExitCode >= 8)
            {
                throw new Exception(
                    $"Robocopy failed with exit code {proc.ExitCode}. " +
                    $"Stdout: {stdout.Trim()} Stderr: {stderr.Trim()}");
            }
        }

        private AgentStatusResponse? GetAgentStatus(string computerName, IProgress<string> progress)
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("X-ADShield-Key", _settings.AgentApiKey);

            var url = $"http://{computerName}:{_settings.AgentPort}/status";
            var response = client.GetAsync(url).Result;
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP Status: {response.StatusCode}");
            }

            var json = response.Content.ReadAsStringAsync().Result;
            return Newtonsoft.Json.JsonConvert.DeserializeObject<AgentStatusResponse>(json);
        }

        private void TriggerAgentBackup(string computerName, string backupTarget, IProgress<string> progress)
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("X-ADShield-Key", _settings.AgentApiKey);

            var url = $"http://{computerName}:{_settings.AgentPort}/backup";
            var payload = new BackupRequestPayload { BackupTarget = backupTarget };
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            using var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");

            var response = client.PostAsync(url, content).Result;
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new Exception("Agent is already running a backup session.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP Status: {response.StatusCode}");
            }
        }

        private void CancelAgentBackup(string computerName)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("X-ADShield-Key", _settings.AgentApiKey);

                var url = $"http://{computerName}:{_settings.AgentPort}/cancel";
                var response = client.PostAsync(url, null).Result;
            }
            catch { }
        }
    }
}
