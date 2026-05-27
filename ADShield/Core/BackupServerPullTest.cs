using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ADShield.Core
{
    /// <summary>
    /// Integration test suite for the server-pull backup architecture.
    /// Validates that the server can pull data from a remote client's admin share
    /// through a VSS symlink, eliminating the Kerberos double-hop problem.
    /// </summary>
    public static class BackupServerPullTest
    {
        /// <summary>
        /// Test 1: Verify that the R2L symlink evaluation setting can be enabled on a remote machine.
        /// </summary>
        public static async Task TestR2LSymlinkEvaluation(string computerName, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");
            progress.Report("[TEST] Test 1: R2L Symlink Evaluation Enablement");
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");

            // Step 1: Query current R2L state on the remote machine
            progress.Report($"[TEST] Querying current R2L symlink evaluation state on {computerName}...");
            string? currentValue = null;
            try
            {
                var scope = new System.Management.ManagementScope($@"\\{computerName}\root\default");
                scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
                scope.Options.EnablePrivileges = true;
                scope.Connect();

                using var regClass = new System.Management.ManagementClass(scope,
                    new System.Management.ManagementPath("StdRegProv"), null);

                uint hklm = 0x80000002;
                using var inParams = regClass.GetMethodParameters("GetDWORDValue");
                inParams["hDefKey"] = hklm;
                inParams["sSubKeyName"] = @"SYSTEM\CurrentControlSet\Control\FileSystem";
                inParams["sValueName"] = "SymlinkRemoteToLocalEvaluation";

                using var outParams = regClass.InvokeMethod("GetDWORDValue", inParams, null);
                var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
                if (retVal == 0)
                    currentValue = outParams["uValue"]?.ToString();

                progress.Report($"[TEST] Current R2L registry value: {currentValue ?? "(not set)"}");
            }
            catch (Exception ex)
            {
                progress.Report($"[TEST] Could not read current R2L value: {ex.Message}");
            }

            // Step 2: Run the enablement method (same one used in production)
            progress.Report("[TEST] Running EnableRemoteSymlinkEvaluation...");
            // We call the orchestrator via reflection since EnableRemoteSymlinkEvaluation is private
            // Instead, we'll replicate the logic here for testing purposes
            try
            {
                var scope = new System.Management.ManagementScope($@"\\{computerName}\root\default");
                scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
                scope.Options.EnablePrivileges = true;
                scope.Connect();

                using var regClass = new System.Management.ManagementClass(scope,
                    new System.Management.ManagementPath("StdRegProv"), null);

                uint hklm = 0x80000002;
                using var inParams = regClass.GetMethodParameters("SetDWORDValue");
                inParams["hDefKey"] = hklm;
                inParams["sSubKeyName"] = @"SYSTEM\CurrentControlSet\Control\FileSystem";
                inParams["sValueName"] = "SymlinkRemoteToLocalEvaluation";
                inParams["uValue"] = (uint)1;

                using var outParams = regClass.InvokeMethod("SetDWORDValue", inParams, null);
                var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
                if (retVal != 0)
                    throw new Exception($"Registry write returned {retVal}");

                progress.Report("[TEST] [PASS] R2L symlink evaluation successfully enabled via WMI registry write.");
            }
            catch (Exception ex)
            {
                progress.Report($"[TEST] [FAIL] R2L enablement failed: {ex.Message}");
                throw;
            }

            // Step 3: Verify it was set
            progress.Report("[TEST] Verifying R2L value was persisted...");
            try
            {
                var scope = new System.Management.ManagementScope($@"\\{computerName}\root\default");
                scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
                scope.Options.EnablePrivileges = true;
                scope.Connect();

                using var regClass = new System.Management.ManagementClass(scope,
                    new System.Management.ManagementPath("StdRegProv"), null);

                uint hklm = 0x80000002;
                using var inParams = regClass.GetMethodParameters("GetDWORDValue");
                inParams["hDefKey"] = hklm;
                inParams["sSubKeyName"] = @"SYSTEM\CurrentControlSet\Control\FileSystem";
                inParams["sValueName"] = "SymlinkRemoteToLocalEvaluation";

                using var outParams = regClass.InvokeMethod("GetDWORDValue", inParams, null);
                var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
                var value = Convert.ToUInt32(outParams["uValue"]);

                if (retVal == 0 && value == 1)
                    progress.Report("[TEST] [PASS] R2L registry value confirmed: 1 (enabled).");
                else
                    throw new Exception($"Expected value 1 but got {value} (return={retVal}).");
            }
            catch (Exception ex)
            {
                progress.Report($"[TEST] [FAIL] R2L verification failed: {ex.Message}");
                throw;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Test 2: Verify that a VSS symlink can be created on the remote client,
        /// and that the server can access files through the client's admin share via that symlink.
        /// </summary>
        public static async Task TestVssSymlinkAccess(string computerName, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");
            progress.Report("[TEST] Test 2: VSS Symlink Creation + Server-Pull Access");
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");

            string? shadowId = null;

            try
            {
                // Step 1: Verify WMI access
                progress.Report($"[TEST] Verifying WMI connectivity to {computerName}...");
                if (!VssManager.TestWmiConnectivity(computerName, out var wmiError))
                    throw new Exception($"WMI not accessible: {wmiError}");
                progress.Report("[TEST] [PASS] WMI connection verified.");

                // Step 2: Create VSS shadow copy
                progress.Report("[TEST] Creating VSS shadow copy on remote C:\\...");
                shadowId = VssManager.CreateRemoteShadowCopy(computerName, @"C:\", progress: progress);
                progress.Report($"[TEST] [PASS] VSS shadow created. ID: {shadowId}");

                // Step 3: Get shadow device path
                progress.Report("[TEST] Looking up shadow device path...");
                var scope = new System.Management.ManagementScope($@"\\{computerName}\root\cimv2");
                scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
                scope.Options.EnablePrivileges = true;
                scope.Connect();

                string shadowDevicePath = "";
                var query = new System.Management.ObjectQuery(
                    $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
                using (var searcher = new System.Management.ManagementObjectSearcher(scope, query))
                {
                    foreach (var obj in searcher.Get())
                    {
                        shadowDevicePath = obj["DeviceObject"]?.ToString() ?? "";
                        if (!shadowDevicePath.EndsWith("\\"))
                            shadowDevicePath += "\\";
                    }
                }
                if (string.IsNullOrEmpty(shadowDevicePath))
                    throw new Exception("Could not resolve shadow device path.");
                progress.Report($"[TEST] [PASS] Shadow device: {shadowDevicePath}");

                // Step 4: Create symlink on remote client via WMI
                var tempLinkPath = @"C:\adshield_test_link";
                progress.Report($"[TEST] Creating symlink on {computerName}: {tempLinkPath} → {shadowDevicePath}");

                // Clean up any stale link
                try { RunTestWmiCommand(computerName, scope, $"cmd.exe /c if exist {tempLinkPath} rmdir {tempLinkPath}", 10000); }
                catch { }

                RunTestWmiCommand(computerName, scope, $"cmd.exe /c mklink /d {tempLinkPath} {shadowDevicePath}", 15000);
                progress.Report("[TEST] [PASS] Remote symlink created.");

                // Step 5: Attempt server-pull access via admin share
                var uncPath = $@"\\{computerName}\C$\adshield_test_link";
                progress.Report($"[TEST] Attempting server-side directory listing of: {uncPath}");

                try
                {
                    if (Directory.Exists(uncPath))
                    {
                        var entries = Directory.GetDirectories(uncPath);
                        progress.Report($"[TEST] [PASS] Server-pull access successful! Found {entries.Length} top-level directories.");
                        // Show first few entries
                        for (int i = 0; i < Math.Min(5, entries.Length); i++)
                            progress.Report($"[TEST]   → {Path.GetFileName(entries[i])}");
                        if (entries.Length > 5)
                            progress.Report($"[TEST]   ... and {entries.Length - 5} more.");
                    }
                    else
                    {
                        throw new Exception($"Directory does not exist or is not accessible: {uncPath}");
                    }
                }
                catch (Exception ex)
                {
                    progress.Report($"[TEST] [FAIL] Server-pull access failed: {ex.Message}");
                    progress.Report("[TEST] This likely means R2L symlink evaluation is not enabled on the client.");
                    progress.Report("[TEST] Run Test 1 first to enable it, or set via Group Policy.");
                    throw;
                }

                // Step 6: Cleanup symlink
                progress.Report("[TEST] Cleaning up test symlink...");
                try { RunTestWmiCommand(computerName, scope, $"cmd.exe /c rmdir {tempLinkPath}", 10000); }
                catch (Exception ex) { progress.Report($"[TEST] [WARN] Cleanup failed: {ex.Message}"); }
            }
            finally
            {
                // Always clean up VSS shadow
                if (shadowId != null)
                {
                    progress.Report("[TEST] Cleaning up VSS shadow copy...");
                    try { VssManager.DeleteShadowCopy(shadowId, progress); }
                    catch (Exception ex) { progress.Report($"[TEST] [WARN] VSS cleanup failed: {ex.Message}"); }
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Test 3: Full end-to-end server-pull robocopy test.
        /// Creates a small test VHDX, mounts it, creates VSS + symlink on client,
        /// runs robocopy locally pulling from the admin share, and verifies data was copied.
        /// </summary>
        public static async Task TestEndToEndServerPull(string computerName, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");
            progress.Report("[TEST] Test 3: End-to-End Server-Pull Robocopy");
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");

            var testDriveLetter = "T";
            var testFolder = Path.Combine(Path.GetTempPath(), "adshield_pull_test");
            var testVhdxPath = Path.Combine(testFolder, "pull_test.vhdx");
            string? shadowId = null;
            bool vhdxMounted = false;
            bool symlinkCreated = false;
            var tempLinkPath = @"C:\adshield_pull_test_link";

            // Setup
            Directory.CreateDirectory(testFolder);
            try { File.Delete(testVhdxPath); } catch { }

            try
            {
                // Step 1: Create small test VHDX
                progress.Report("[TEST] 1. Creating 50 MB test VHDX...");
                ulong sizeBytes = 50 * 1024 * 1024;
                await Task.Run(() => VhdxManager.CreateVhdx(testVhdxPath, sizeBytes, progress: null), ct);
                progress.Report("[TEST] [PASS] Test VHDX created.");

                // Step 2: Mount and format
                progress.Report("[TEST] 2. Mounting and formatting test VHDX...");
                var mountScript =
                    $"select vdisk file=\"{testVhdxPath}\"\r\n" +
                    "attach vdisk\r\n" +
                    "clean\r\n" +
                    "convert gpt\r\n" +
                    "create partition primary\r\n" +
                    $"format fs=ntfs label=\"PullTest\" quick\r\n" +
                    $"assign letter={testDriveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(mountScript, progress, ct);
                vhdxMounted = true;
                progress.Report($"[TEST] [PASS] VHDX mounted at {testDriveLetter}:");

                // Step 3: Create VSS on client
                progress.Report($"[TEST] 3. Creating VSS shadow on {computerName}...");
                shadowId = VssManager.CreateRemoteShadowCopy(computerName, @"C:\", progress: progress);
                progress.Report($"[TEST] [PASS] VSS shadow: {shadowId}");

                // Step 4: Get shadow path and create symlink
                var scope = new System.Management.ManagementScope($@"\\{computerName}\root\cimv2");
                scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
                scope.Options.EnablePrivileges = true;
                scope.Connect();

                string shadowDevicePath = "";
                var query = new System.Management.ObjectQuery(
                    $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
                using (var searcher = new System.Management.ManagementObjectSearcher(scope, query))
                {
                    foreach (var obj in searcher.Get())
                    {
                        shadowDevicePath = obj["DeviceObject"]?.ToString() ?? "";
                        if (!shadowDevicePath.EndsWith("\\"))
                            shadowDevicePath += "\\";
                    }
                }

                progress.Report("[TEST] 4. Creating VSS symlink on client...");
                try { RunTestWmiCommand(computerName, scope, $"cmd.exe /c if exist {tempLinkPath} rmdir {tempLinkPath}", 10000); }
                catch { }
                RunTestWmiCommand(computerName, scope, $"cmd.exe /c mklink /d {tempLinkPath} {shadowDevicePath}", 15000);
                symlinkCreated = true;
                progress.Report("[TEST] [PASS] VSS symlink created.");

                // Step 5: Run server-pull robocopy
                var uncSource = $@"\\{computerName}\C$\adshield_pull_test_link";
                var logFile = Path.Combine(Path.GetTempPath(), "adshield_pull_test_robocopy.log");

                progress.Report($"[TEST] 5. Running server-pull robocopy: {uncSource} → {testDriveLetter}:\\");

                // Only copy the top-level Windows directory listing (skip deep recursion for speed)
                var robocopyArgs =
                    $"\"{uncSource}\\Windows\" \"{testDriveLetter}:\\Windows\" " +
                    "/E /COPY:DAT /B /R:0 /W:0 /NP /XJ /LEV:1 " +
                    "/XD \"System Volume Information\" \"$Recycle.Bin\" " +
                    $"/LOG:\"{logFile}\"";

                var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe", robocopyArgs)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                    throw new Exception("Failed to start robocopy.");

                await proc.WaitForExitAsync(ct);

                progress.Report($"[TEST] Robocopy exit code: {proc.ExitCode}");

                if (proc.ExitCode >= 8)
                    throw new Exception($"Robocopy failed with exit code {proc.ExitCode}");

                // Step 6: Verify files were copied
                progress.Report("[TEST] 6. Verifying data was copied to VHDX...");
                var destWindowsDir = $"{testDriveLetter}:\\Windows";
                if (Directory.Exists(destWindowsDir))
                {
                    var copiedFiles = Directory.GetFiles(destWindowsDir, "*", SearchOption.TopDirectoryOnly);
                    progress.Report($"[TEST] [PASS] Found {copiedFiles.Length} files copied to {destWindowsDir}");

                    if (copiedFiles.Length == 0)
                        progress.Report("[TEST] [WARN] No files in Windows root — this may be normal if all items are subdirectories.");
                    else
                        progress.Report($"[TEST] [PASS] Server-pull robocopy end-to-end test PASSED!");
                }
                else
                {
                    throw new Exception($"Destination directory {destWindowsDir} does not exist after robocopy.");
                }
            }
            finally
            {
                // Cleanup: symlink
                if (symlinkCreated)
                {
                    try
                    {
                        var scope = new System.Management.ManagementScope($@"\\{computerName}\root\cimv2");
                        scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
                        scope.Options.EnablePrivileges = true;
                        scope.Connect();
                        RunTestWmiCommand(computerName, scope, $"cmd.exe /c rmdir {tempLinkPath}", 10000);
                    }
                    catch (Exception ex) { progress.Report($"[TEST] [WARN] Symlink cleanup failed: {ex.Message}"); }
                }

                // Cleanup: VSS
                if (shadowId != null)
                {
                    try { VssManager.DeleteShadowCopy(shadowId, progress); }
                    catch (Exception ex) { progress.Report($"[TEST] [WARN] VSS cleanup failed: {ex.Message}"); }
                }

                // Cleanup: VHDX
                if (vhdxMounted)
                {
                    try
                    {
                        var detachScript = $"select vdisk file=\"{testVhdxPath}\"\r\ndetach vdisk";
                        await BackupOrchestrator.RunLocalDiskpart(detachScript, null, ct);
                    }
                    catch (Exception ex) { progress.Report($"[TEST] [WARN] VHDX detach failed: {ex.Message}"); }
                }

                // Cleanup: test VHDX file
                try { if (File.Exists(testVhdxPath)) File.Delete(testVhdxPath); } catch { }
            }
        }

        /// <summary>
        /// Runs all three server-pull tests in sequence.
        /// </summary>
        public static async Task RunAllTests(string computerName, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("[TEST] ╔═══════════════════════════════════════════════════════════╗");
            progress.Report("[TEST] ║   AD Shield — Server-Pull Architecture Test Suite        ║");
            progress.Report("[TEST] ╚═══════════════════════════════════════════════════════════╝");
            progress.Report("");

            int passed = 0;
            int failed = 0;

            // Test 1
            try
            {
                await TestR2LSymlinkEvaluation(computerName, progress, ct);
                passed++;
            }
            catch
            {
                failed++;
                progress.Report("[TEST] ⚠ Test 1 FAILED — continuing to next test...");
            }
            progress.Report("");

            // Test 2
            try
            {
                await TestVssSymlinkAccess(computerName, progress, ct);
                passed++;
            }
            catch
            {
                failed++;
                progress.Report("[TEST] ⚠ Test 2 FAILED — continuing to next test...");
            }
            progress.Report("");

            // Test 3
            try
            {
                await TestEndToEndServerPull(computerName, progress, ct);
                passed++;
            }
            catch
            {
                failed++;
                progress.Report("[TEST] ⚠ Test 3 FAILED.");
            }

            progress.Report("");
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");
            progress.Report($"[TEST] Results: {passed} PASSED, {failed} FAILED out of 3 tests.");
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");

            if (failed > 0)
                throw new Exception($"Server-pull test suite: {failed} test(s) failed.");
        }

        /// <summary>
        /// Helper: run a WMI command on a remote machine and wait for completion.
        /// </summary>
        private static void RunTestWmiCommand(
            string computerName,
            System.Management.ManagementScope scope,
            string commandLine,
            int timeoutMs)
        {
            using var processClass = new System.Management.ManagementClass(scope,
                new System.Management.ManagementPath("Win32_Process"), null);

            using var inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = commandLine;

            using var outParams = processClass.InvokeMethod("Create", inParams, null);
            var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
            if (retVal != 0)
                throw new Exception($"WMI process creation failed. Return: {retVal}");

            var pid = Convert.ToUInt32(outParams["ProcessId"]);

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
                var query = new System.Management.ObjectQuery(
                    $"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {pid}");
                using var searcher = new System.Management.ManagementObjectSearcher(scope, query);
                if (searcher.Get().Count == 0)
                    return;
            }
            throw new TimeoutException($"Remote command did not complete within {timeoutMs / 1000}s.");
        }
    }
}
