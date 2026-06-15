using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ADShield.Models;

namespace ADShield.Core
{
    public static class BackupValidationSuite
    {
        public static async Task RunValidationAsync(IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("[VALIDATION] Starting ADShield Backup Environment Validation Suite...");

            // 1. Check local temp path write ability
            progress.Report("[VALIDATION] 1. Testing local temporary directory write access...");
            var tempFolder = Path.Combine(Path.GetTempPath(), "adshield_val_temp");
            try
            {
                Directory.CreateDirectory(tempFolder);
                var testFile = Path.Combine(tempFolder, "write_test.txt");
                await File.WriteAllTextAsync(testFile, "validation test", ct);
                var content = await File.ReadAllTextAsync(testFile, ct);
                File.Delete(testFile);
                if (content != "validation test")
                {
                    throw new Exception("Write/read test failed due to content mismatch.");
                }
                progress.Report("[VALIDATION] [PASS] Local temp directory write/read verified.");
            }
            catch (Exception ex)
            {
                progress.Report($"[VALIDATION] [FAIL] Temp directory check failed: {ex.Message}");
                throw;
            }
            finally
            {
                try { Directory.Delete(tempFolder, true); } catch { }
            }

            // 2. Check if diskpart.exe is accessible
            progress.Report("[VALIDATION] 2. Checking diskpart.exe availability...");
            try
            {
                var tempScript = Path.Combine(Path.GetTempPath(), $"adshield_val_{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(tempScript, "exit", ct);
                try
                {
                    var result = await ProcessRunner.RunAsync(
                        "diskpart.exe",
                        arguments: $"/s \"{tempScript}\"",
                        timeout: TimeSpan.FromSeconds(10),
                        ct: ct);

                    if (result.ExitCode != 0)
                    {
                        throw new Exception($"diskpart.exe returned non-zero exit code: {result.ExitCode}");
                    }
                    progress.Report("[VALIDATION] [PASS] diskpart.exe is available and functioning.");
                }
                finally
                {
                    try { File.Delete(tempScript); } catch { }
                }
            }
            catch (Exception ex)
            {
                progress.Report($"[VALIDATION] [FAIL] diskpart.exe check failed: {ex.Message}");
                throw;
            }

            // 3. Check if robocopy.exe is accessible
            progress.Report("[VALIDATION] 3. Checking robocopy.exe availability...");
            try
            {
                var result = await ProcessRunner.RunAsync(
                    "robocopy.exe",
                    arguments: "/?",
                    timeout: TimeSpan.FromSeconds(10),
                    ct: ct);

                if (result.ExitCode != 16 && result.ExitCode != 0)
                {
                    progress.Report($"[VALIDATION] Note: Robocopy returned exit code {result.ExitCode}. Proceeding anyway.");
                }
                progress.Report("[VALIDATION] [PASS] robocopy.exe is available.");
            }
            catch (Exception ex)
            {
                progress.Report($"[VALIDATION] [FAIL] robocopy.exe check failed: {ex.Message}");
                throw;
            }

            progress.Report("[VALIDATION] [ALL PASSED] Environment validation checks completed successfully.");
        }
    }
}
