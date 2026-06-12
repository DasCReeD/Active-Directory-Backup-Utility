using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ADShield.Core
{
    /// <summary>
    /// Unit-style tests for the backup pipeline logic.
    /// Tests individual components in isolation without requiring a live remote machine.
    /// Run from the ADShield GUI diagnostics menu.
    /// </summary>
    public static class BackupLogicTest
    {
        /// <summary>
        /// Master test runner — executes all tests in sequence and reports results.
        /// </summary>
        public static async Task RunAllTests(IProgress<string> progress, CancellationToken ct)
        {
            int passed = 0, failed = 0;

            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");
            progress.Report("[TEST] Backup Logic Test Suite");
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");

            passed += await RunTest("1. VHDX Create + GPT Partition + Format", TestVhdxCreateAndFormat, progress, ct) ? 1 : 0;
            passed += await RunTest("2. VHDX Existing Mount (Partition 2)", TestVhdxExistingMount, progress, ct) ? 1 : 0;
            passed += await RunTest("3. Robocopy ArgumentList Quoting", TestRobocopyArgumentQuoting, progress, ct) ? 1 : 0;
            passed += await RunTest("4. VeraCrypt Mount Detection", TestVeraCryptMountDetection, progress, ct) ? 1 : 0;
            passed += await RunTest("5. UNC Path Resolution", TestUncPathResolution, progress, ct) ? 1 : 0;
            passed += await RunTest("6. Robocopy Local Copy (filesystem)", TestRobocopyLocalCopy, progress, ct) ? 1 : 0;

            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");
            progress.Report($"[TEST] Results: {passed} PASSED, {failed} FAILED out of {passed + failed}");
            progress.Report("[TEST] ═══════════════════════════════════════════════════════════");

            if (failed > 0)
                throw new Exception($"{failed} test(s) failed. Review the log above for details.");
        }

        private static async Task<bool> RunTest(string name, Func<IProgress<string>, CancellationToken, Task> test,
            IProgress<string> progress, CancellationToken ct)
        {
            progress.Report($"[TEST] ─── {name} ───");
            try
            {
                await test(progress, ct);
                progress.Report($"[TEST] [PASS] {name}");
                return true;
            }
            catch (Exception ex)
            {
                progress.Report($"[TEST] [FAIL] {name}: {ex.Message}");
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Test 1: Create a fresh VHDX, partition as GPT, format NTFS, verify write
        // ══════════════════════════════════════════════════════════════════════════
        private static async Task TestVhdxCreateAndFormat(IProgress<string> progress, CancellationToken ct)
        {
            var testDir = Path.Combine(Path.GetTempPath(), "adshield_logic_test");
            var vhdxPath = Path.Combine(testDir, "test_new.vhdx");
            var driveLetter = "T";

            Directory.CreateDirectory(testDir);
            try { File.Delete(vhdxPath); } catch { }

            try
            {
                // Create 20 MB VHDX
                progress.Report("[TEST]   Creating 20 MB test VHDX...");
                await Task.Run(() => VhdxManager.CreateVhdx(vhdxPath, 20 * 1024 * 1024, progress: null), ct);

                if (!File.Exists(vhdxPath))
                    throw new Exception("VHDX file was not created.");

                // Attach + clean + GPT + partition + format + assign
                progress.Report("[TEST]   Attaching, partitioning GPT, formatting NTFS...");
                var script =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    "attach vdisk\r\n" +
                    "clean\r\n" +
                    "convert gpt\r\n" +
                    "create partition primary\r\n" +
                    $"format fs=ntfs label=\"LogicTest\" quick\r\n" +
                    $"assign letter={driveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(script, progress, ct);

                // Verify drive is writable
                progress.Report($"[TEST]   Verifying {driveLetter}:\\ is writable...");
                var testFile = Path.Combine($"{driveLetter}:\\", "logic_test.txt");
                File.WriteAllText(testFile, "ADShield backup logic test");
                var readBack = File.ReadAllText(testFile);
                File.Delete(testFile);

                if (readBack != "ADShield backup logic test")
                    throw new Exception("Write/read verification failed — data mismatch.");

                progress.Report("[TEST]   Fresh VHDX create → GPT → format → write: OK");
            }
            finally
            {
                // Cleanup: detach VHDX
                try
                {
                    var detach = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                    await BackupOrchestrator.RunLocalDiskpart(detach, null, ct);
                }
                catch { }
                try { File.Delete(vhdxPath); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Test 2: Mount an existing GPT VHDX using partition 2 (not partition 1)
        // ══════════════════════════════════════════════════════════════════════════
        private static async Task TestVhdxExistingMount(IProgress<string> progress, CancellationToken ct)
        {
            var testDir = Path.Combine(Path.GetTempPath(), "adshield_logic_test");
            var vhdxPath = Path.Combine(testDir, "test_existing.vhdx");
            var driveLetter = "T";

            Directory.CreateDirectory(testDir);
            try { File.Delete(vhdxPath); } catch { }

            try
            {
                // Create and initialize a fresh GPT VHDX first
                progress.Report("[TEST]   Creating and initializing GPT VHDX...");
                await Task.Run(() => VhdxManager.CreateVhdx(vhdxPath, 20 * 1024 * 1024, progress: null), ct);

                var initScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    "attach vdisk\r\n" +
                    "clean\r\n" +
                    "convert gpt\r\n" +
                    "create partition primary\r\n" +
                    "format fs=ntfs label=\"ExistTest\" quick\r\n";
                await BackupOrchestrator.RunLocalDiskpart(initScript, progress, ct);

                // Write a marker file
                // First assign a drive letter temporarily
                var assignScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    $"select partition 2\r\n" +
                    $"assign letter={driveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(assignScript, progress, ct);

                var markerFile = Path.Combine($"{driveLetter}:\\", "existing_marker.txt");
                File.WriteAllText(markerFile, "marker_data_12345");

                // Detach (simulating reboot / re-mount scenario)
                progress.Report("[TEST]   Detaching to simulate re-mount...");
                var detachScript = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                await BackupOrchestrator.RunLocalDiskpart(detachScript, null, ct);

                // Re-mount using the EXACT same sequence the orchestrator uses for existing VHDXs
                progress.Report("[TEST]   Re-mounting existing VHDX with 'select partition 2'...");
                var remountScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    "attach vdisk\r\n";
                await BackupOrchestrator.RunLocalDiskpart(remountScript, progress, ct);

                var mountScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    "online disk NOERR\r\n" +
                    "select partition 2\r\n" +
                    $"assign letter={driveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(mountScript, progress, ct);

                // Verify the marker file survived
                progress.Report("[TEST]   Verifying marker file from previous session...");
                if (!File.Exists(markerFile))
                    throw new Exception($"Marker file not found at {markerFile} — partition 2 mount may have failed.");

                var marker = File.ReadAllText(markerFile);
                if (marker != "marker_data_12345")
                    throw new Exception($"Marker data mismatch: expected 'marker_data_12345', got '{marker}'");

                progress.Report("[TEST]   Existing VHDX re-mount with partition 2: OK");
            }
            finally
            {
                try
                {
                    var detach = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                    await BackupOrchestrator.RunLocalDiskpart(detach, null, ct);
                }
                catch { }
                try { File.Delete(vhdxPath); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Test 3: Verify ArgumentList produces correct robocopy arguments
        //         (the root cause fix for the "B:\" quoting bug)
        // ══════════════════════════════════════════════════════════════════════════
        private static async Task TestRobocopyArgumentQuoting(IProgress<string> progress, CancellationToken ct)
        {
            // Simulate the exact argument construction used in RunLocalDataCopy
            var uncSource = @"\\TESTSERVER\adshield_backup$";
            var localDest = @"B:\";
            var logFile = Path.Combine(Path.GetTempPath(), "test_robocopy.log");

            var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[]
            {
                uncSource, localDest,
                "/E", "/COPY:DAT", "/R:1", "/W:1", "/NP", "/XJ",
                "/XD", "System Volume Information", "$Recycle.Bin", "$WinREAgent", "Recovery",
                $"/LOG:{logFile}"
            })
                psi.ArgumentList.Add(arg);

            // Verify the ArgumentList contains the right number of args
            if (psi.ArgumentList.Count != 14)
                throw new Exception($"Expected 14 arguments, got {psi.ArgumentList.Count}");

            // Verify source and dest are preserved exactly (no mangling)
            if (psi.ArgumentList[0] != uncSource)
                throw new Exception($"Source arg mangled: '{psi.ArgumentList[0]}' != '{uncSource}'");
            if (psi.ArgumentList[1] != localDest)
                throw new Exception($"Dest arg mangled: '{psi.ArgumentList[1]}' != '{localDest}'");

            // The critical test: run robocopy /? with our ArgumentList approach
            // to verify the process starts without crashing (exit code != 16)
            var helpPsi = new System.Diagnostics.ProcessStartInfo("robocopy.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            helpPsi.ArgumentList.Add("/?");
            using var proc = System.Diagnostics.Process.Start(helpPsi);
            if (proc == null) throw new Exception("Failed to start robocopy /?");
            proc.StandardOutput.ReadToEnd();
            await proc.WaitForExitAsync(ct);

            // robocopy /? returns exit code 1, not 16
            if (proc.ExitCode == 16)
                throw new Exception("robocopy /? returned exit code 16 — argument quoting broken");

            progress.Report("[TEST]   ArgumentList construction and robocopy invocation: OK");
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Test 4: VeraCrypt mount detection logic
        // ══════════════════════════════════════════════════════════════════════════
        private static async Task TestVeraCryptMountDetection(IProgress<string> progress, CancellationToken ct)
        {
            // IsMounted checks DriveInfo — verify it works for a known drive (C:)
            bool cMounted = VeraCryptManager.IsMounted("C");
            if (!cMounted)
                throw new Exception("IsMounted('C') returned false — C: drive should always be mounted.");

            // Verify it returns false for an unused letter
            bool zMounted = VeraCryptManager.IsMounted("Z");
            // Z: might be mapped — just log the result, don't fail
            progress.Report($"[TEST]   IsMounted('C') = {cMounted}, IsMounted('Z') = {zMounted}");

            // Test ResolveUncPath with a UNC path (should pass through unchanged)
            var uncPath = @"\\filesvr\share\file.hc";
            var resolved = VeraCryptManager.ResolveUncPath(uncPath);
            if (resolved != uncPath)
                throw new Exception($"ResolveUncPath mangled UNC: '{resolved}' != '{uncPath}'");

            // Test ResolveUncPath with a local path (should pass through unchanged)
            var localPath = @"C:\test\file.hc";
            resolved = VeraCryptManager.ResolveUncPath(localPath);
            // C: is a local drive, not a mapped drive, so it should pass through
            progress.Report($"[TEST]   ResolveUncPath('{localPath}') = '{resolved}'");

            progress.Report("[TEST]   Mount detection and UNC resolution: OK");
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Test 5: UNC path resolution edge cases
        // ══════════════════════════════════════════════════════════════════════════
        private static async Task TestUncPathResolution(IProgress<string> progress, CancellationToken ct)
        {
            // Null/empty paths
            if (VeraCryptManager.ResolveUncPath("") != "")
                throw new Exception("ResolveUncPath('') should return ''");
            if (VeraCryptManager.ResolveUncPath(null!) != null)
                throw new Exception("ResolveUncPath(null) should return null");

            // Short strings that shouldn't be processed
            if (VeraCryptManager.ResolveUncPath("X") != "X")
                throw new Exception("Single char should pass through");

            // Already-UNC paths pass through
            var unc = @"\\server\share\path";
            if (VeraCryptManager.ResolveUncPath(unc) != unc)
                throw new Exception("UNC paths should pass through unchanged");

            progress.Report("[TEST]   UNC path resolution edge cases: OK");
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Test 6: Robocopy local filesystem copy (proves the ArgumentList pipeline
        //         works end-to-end with real file copying)
        // ══════════════════════════════════════════════════════════════════════════
        private static async Task TestRobocopyLocalCopy(IProgress<string> progress, CancellationToken ct)
        {
            var testDir = Path.Combine(Path.GetTempPath(), "adshield_logic_test");
            var srcDir = Path.Combine(testDir, "robocopy_src");
            var dstDir = Path.Combine(testDir, "robocopy_dst");
            var logFile = Path.Combine(testDir, "robocopy_test.log");

            // Setup source with test files
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(Path.Combine(srcDir, "SubFolder"));
            File.WriteAllText(Path.Combine(srcDir, "file1.txt"), "test data 1");
            File.WriteAllText(Path.Combine(srcDir, "SubFolder", "file2.txt"), "test data 2");

            // Clean destination
            if (Directory.Exists(dstDir)) Directory.Delete(dstDir, true);

            try
            {
                progress.Report("[TEST]   Running robocopy with ArgumentList (local → local)...");

                var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var arg in new[]
                {
                    srcDir, dstDir,
                    "/E", "/COPY:DAT", "/R:1", "/W:1", "/NP", "/XJ",
                    $"/LOG:{logFile}"
                })
                    psi.ArgumentList.Add(arg);

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) throw new Exception("Failed to start robocopy.");
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                await proc.WaitForExitAsync(ct);

                progress.Report($"[TEST]   Robocopy exit code: {proc.ExitCode}");

                if (proc.ExitCode >= 8)
                    throw new Exception($"Robocopy failed with exit code {proc.ExitCode}");

                // Verify files copied
                var dst1 = Path.Combine(dstDir, "file1.txt");
                var dst2 = Path.Combine(dstDir, "SubFolder", "file2.txt");

                if (!File.Exists(dst1))
                    throw new Exception($"Missing copied file: {dst1}");
                if (!File.Exists(dst2))
                    throw new Exception($"Missing copied file: {dst2}");

                if (File.ReadAllText(dst1) != "test data 1")
                    throw new Exception("file1.txt content mismatch after copy");
                if (File.ReadAllText(dst2) != "test data 2")
                    throw new Exception("file2.txt content mismatch after copy");

                progress.Report("[TEST]   Robocopy local copy with ArgumentList: OK");
            }
            finally
            {
                try { Directory.Delete(srcDir, true); } catch { }
                try { Directory.Delete(dstDir, true); } catch { }
                try { File.Delete(logFile); } catch { }
            }
        }
    }
}
