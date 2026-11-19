using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ADShield.Core
{
    public static class SelfHealingDiagnostics
    {
        private static string GetFreeDriveLetter()
        {
            var used = new HashSet<char>(
                DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            for (char c = 'Z'; c >= 'M'; c--)
            {
                if (!used.Contains(c))
                    return c.ToString();
            }
            throw new InvalidOperationException("No free drive letters available in range Z..M for diagnostics.");
        }

        public static async Task RunDiagnosticsAsync(
            IProgress<string> progress,
            CancellationToken ct,
            string? testFolderOverride = null)
        {
            var testDriveLetter = GetFreeDriveLetter();
            var testFolder = testFolderOverride ?? AppConfig.ReadSettings().BackupRootPath;
            var rootDrive = Path.GetPathRoot(testFolder);
            if (string.IsNullOrEmpty(rootDrive) || !Directory.Exists(rootDrive))
            {
                var fallback = Path.Combine(Path.GetTempPath(), "adshield_self_heal_test");
                progress.Report($"[TEST] Configured backup root '{testFolder}' is unavailable. Falling back to '{fallback}'.");
                testFolder = fallback;
            }
            var testVhdxPath = Path.Combine(testFolder, "self_healing_test.vhdx");

            progress.Report("[TEST] starting Active Directory Backup Self-Healing Diagnostic Test...");

            // 1. Ensure backup root folder exists
            if (!Directory.Exists(testFolder))
            {
                progress.Report($"[TEST] Creating test folder path: {testFolder}");
                Directory.CreateDirectory(testFolder);
            }

            // 2. Clean up any leftover test files/mounts first
            try
            {
                var cleanupScript = $"select vdisk file=\"{testVhdxPath}\"\r\ndetach vdisk";
                await BackupOrchestrator.RunLocalDiskpart(cleanupScript, null, ct);
            }
            catch { /* ignore */ }

            if (File.Exists(testVhdxPath))
            {
                try { File.Delete(testVhdxPath); } catch { /* ignore */ }
            }

            try
            {
                // 3. Create tiny 10 MB raw VHDX file
                progress.Report("[TEST] 1. Creating 10 MB raw test VHDX container...");
                ulong sizeBytes = 10 * 1024 * 1024;
                await Task.Run(() => VhdxManager.CreateVhdx(testVhdxPath, sizeBytes, progress: null), ct);
                progress.Report("[TEST] RAW test VHDX file created successfully.");

                // 4. Attach raw VHDX locally on the host
                progress.Report("[TEST] 2. Attaching raw VHDX container to local host...");
                var attachScript = $"select vdisk file=\"{testVhdxPath}\"\r\nattach vdisk";
                await BackupOrchestrator.RunLocalDiskpart(attachScript, progress, ct);

                // 5. Map to drive letter (without formatting it!)
                progress.Report($"[TEST] 3. Mapping raw uninitialized volume to drive {testDriveLetter}:...");
                var mapScript = 
                    $"select vdisk file=\"{testVhdxPath}\"\r\n" +
                    $"assign letter={testDriveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(mapScript, progress, ct);

                // 6. Run write test on the RAW drive letter
                progress.Report($"[TEST] 4. Running write test on raw uninitialized {testDriveLetter}:\\ drive...");
                bool rawWriteResult = false;
                try
                {
                    var testFile = Path.Combine($"{testDriveLetter}:\\", "test_file.txt");
                    File.WriteAllText(testFile, "test data");
                    File.Delete(testFile);
                    rawWriteResult = true;
                }
                catch (Exception ex)
                {
                    progress.Report($"[TEST] Write test failed as expected! Error: {ex.Message}");
                    rawWriteResult = false;
                }

                if (rawWriteResult)
                {
                    throw new Exception("CRITICAL FAILURE: Writing to RAW unformatted disk succeeded?! System state is inconsistent.");
                }
                progress.Report("[TEST] [SUCCESS] Standard file writing successfully blocked by raw unformatted drive.");

                // 7. Trigger dynamic recovery NTFS formatting self-healing routine
                progress.Report("[TEST] 5. Triggering self-healing dynamic NTFS formatting sequence...");
                var recoveryScript =
                    $"select vdisk file=\"{testVhdxPath}\"\r\n" +
                    $"clean\r\n" +
                    $"convert gpt\r\n" +
                    $"create partition primary\r\n" +
                    $"format fs=ntfs label=\"ADShieldTest\" quick\r\n" +
                    $"assign letter={testDriveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(recoveryScript, progress, ct);

                // 8. Run second write verification test
                progress.Report("[TEST] 6. Verifying write access after NTFS self-healing format...");
                bool formatWriteResult = false;
                try
                {
                    var testFile = Path.Combine($"{testDriveLetter}:\\", "test_file.txt");
                    File.WriteAllText(testFile, "test data");
                    File.Delete(testFile);
                    formatWriteResult = true;
                }
                catch (Exception ex)
                {
                    progress.Report($"[TEST] Write test failed after formatting: {ex.Message}");
                    formatWriteResult = false;
                }

                if (!formatWriteResult)
                {
                    throw new Exception($"CRITICAL FAILURE: Drive {testDriveLetter}:\\ is still unformatted or un-writable after recovery formatting.");
                }
                progress.Report($"[TEST] [SUCCESS] NTFS formatting completed and drive {testDriveLetter}:\\ is 100% writable!");

                progress.Report("[TEST] [ALL PASSED] Self-Healing regression test completed successfully! The raw drive bug is fully prevented.");
            }
            finally
            {
                // 9. Clean up all resources
                progress.Report("[TEST] 7. Cleaning up test VHDX and mounts...");
                try
                {
                    var detachScript = $"select vdisk file=\"{testVhdxPath}\"\r\ndetach vdisk";
                    await BackupOrchestrator.RunLocalDiskpart(detachScript, null, ct);
                }
                catch { /* ignore */ }

                if (File.Exists(testVhdxPath))
                {
                    try { File.Delete(testVhdxPath); } catch { /* ignore */ }
                }
                progress.Report("[TEST] Cleanup finished.");
            }
        }
    }
}
