using Xunit;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ADShield.Core;
using ADShield.Models;

namespace ADShield.Tests
{
    public class LogicTests
    {
        private static bool IsRunAsAdmin()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        [Fact]
        public async Task TestVhdxCreateAndFormat()
        {
            if (!IsRunAsAdmin())
            {
                return;
            }
            // Note: This test requires administrative privileges (diskpart attach/partition/format)
            var testDir = Path.Combine(Path.GetTempPath(), "adshield_logic_test");
            var vhdxPath = Path.Combine(testDir, "test_new.vhdx");
            var driveLetter = "T";

            Directory.CreateDirectory(testDir);
            try { File.Delete(vhdxPath); } catch { }

            try
            {
                // Create 20 MB VHDX
                await Task.Run(() => VhdxManager.CreateVhdx(vhdxPath, 20 * 1024 * 1024, progress: null));
                Assert.True(File.Exists(vhdxPath));

                // Attach + clean + GPT + partition + format + assign
                var script =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    "attach vdisk\r\n" +
                    "clean\r\n" +
                    "convert gpt\r\n" +
                    "create partition primary\r\n" +
                    $"format fs=ntfs label=\"LogicTest\" quick\r\n" +
                    $"assign letter={driveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(script, null, CancellationToken.None);

                // Verify drive is writable
                var testFile = Path.Combine($"{driveLetter}:\\", "logic_test.txt");
                File.WriteAllText(testFile, "ADShield backup logic test");
                var readBack = File.ReadAllText(testFile);
                File.Delete(testFile);

                Assert.Equal("ADShield backup logic test", readBack);
            }
            finally
            {
                // Cleanup: detach VHDX
                try
                {
                    var detach = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                    await BackupOrchestrator.RunLocalDiskpart(detach, null, CancellationToken.None);
                }
                catch { }
                try { File.Delete(vhdxPath); } catch { }
            }
        }

        [Fact]
        public async Task TestVhdxExistingMount()
        {
            if (!IsRunAsAdmin())
            {
                return;
            }
            // Note: This test requires administrative privileges
            var testDir = Path.Combine(Path.GetTempPath(), "adshield_logic_test");
            var vhdxPath = Path.Combine(testDir, "test_existing.vhdx");
            var driveLetter = "T";

            Directory.CreateDirectory(testDir);
            try { File.Delete(vhdxPath); } catch { }

            try
            {
                // Create and initialize a fresh GPT VHDX first
                await Task.Run(() => VhdxManager.CreateVhdx(vhdxPath, 20 * 1024 * 1024, progress: null));

                var initScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    "attach vdisk\r\n" +
                    "clean\r\n" +
                    "convert gpt\r\n" +
                    "create partition primary\r\n" +
                    "format fs=ntfs label=\"ExistTest\" quick\r\n";
                await BackupOrchestrator.RunLocalDiskpart(initScript, null, CancellationToken.None);

                // Write a marker file
                var assignScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    $"select partition 2\r\n" +
                    $"assign letter={driveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(assignScript, null, CancellationToken.None);

                var markerFile = Path.Combine($"{driveLetter}:\\", "existing_marker.txt");
                File.WriteAllText(markerFile, "marker_data_12345");

                // Detach
                var detachScript = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                await BackupOrchestrator.RunLocalDiskpart(detachScript, null, CancellationToken.None);

                // Re-mount using partition 2 sequence
                var remountScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    "attach vdisk\r\n";
                await BackupOrchestrator.RunLocalDiskpart(remountScript, null, CancellationToken.None);

                var mountScript =
                    $"select vdisk file=\"{vhdxPath}\"\r\n" +
                    "online disk NOERR\r\n" +
                    "select partition 2\r\n" +
                    $"assign letter={driveLetter} NOERR\r\n";
                await BackupOrchestrator.RunLocalDiskpart(mountScript, null, CancellationToken.None);

                // Verify the marker file survived
                Assert.True(File.Exists(markerFile));
                var marker = File.ReadAllText(markerFile);
                Assert.Equal("marker_data_12345", marker);
            }
            finally
            {
                try
                {
                    var detach = $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk";
                    await BackupOrchestrator.RunLocalDiskpart(detach, null, CancellationToken.None);
                }
                catch { }
                try { File.Delete(vhdxPath); } catch { }
            }
        }

        [Fact]
        public async Task TestRobocopyArgumentQuoting()
        {
            var uncSource = @"\\TESTSERVER\adshield_backup$";
            var localDest = @"B:\";
            var logFile = Path.Combine(Path.GetTempPath(), "test_robocopy.log");

            var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe");
            foreach (var arg in new[]
            {
                uncSource, localDest,
                "/E", "/COPY:DAT", "/R:1", "/W:1", "/NP", "/XJ",
                "/XD", "System Volume Information", "$Recycle.Bin", "$WinREAgent", "Recovery",
                $"/LOG:{logFile}"
            })
                psi.ArgumentList.Add(arg);

            Assert.Equal(14, psi.ArgumentList.Count);
            Assert.Equal(uncSource, psi.ArgumentList[0]);
            Assert.Equal(localDest, psi.ArgumentList[1]);

            var result = await ProcessRunner.RunAsync("robocopy.exe", arguments: "/?");
            Assert.NotNull(result);
        }

        [Fact]
        public void TestVeraCryptMountDetection()
        {
            bool cMounted = VeraCryptManager.IsMounted("C");
            Assert.True(cMounted);

            var uncPath = @"\\filesvr\share\file.hc";
            var resolved = VeraCryptManager.ResolveUncPath(uncPath);
            Assert.Equal(uncPath, resolved);

            var localPath = @"C:\test\file.hc";
            var resolvedLocal = VeraCryptManager.ResolveUncPath(localPath);
            Assert.Equal(localPath, resolvedLocal);
        }

        [Fact]
        public void TestUncPathResolutionEdgeCases()
        {
            Assert.Equal("", VeraCryptManager.ResolveUncPath(""));
            Assert.Null(VeraCryptManager.ResolveUncPath(null!));
            Assert.Equal("X", VeraCryptManager.ResolveUncPath("X"));

            var unc = @"\\server\share\path";
            Assert.Equal(unc, VeraCryptManager.ResolveUncPath(unc));
        }

        [Fact]
        public async Task TestRobocopyLocalCopy()
        {
            var testDir = Path.Combine(Path.GetTempPath(), "adshield_logic_test");
            var srcDir = Path.Combine(testDir, "robocopy_src");
            var dstDir = Path.Combine(testDir, "robocopy_dst");
            var logFile = Path.Combine(testDir, "robocopy_test.log");

            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(Path.Combine(srcDir, "SubFolder"));
            File.WriteAllText(Path.Combine(srcDir, "file1.txt"), "test data 1");
            File.WriteAllText(Path.Combine(srcDir, "SubFolder", "file2.txt"), "test data 2");

            if (Directory.Exists(dstDir)) Directory.Delete(dstDir, true);

            try
            {
                var argumentList = new[]
                {
                    srcDir, dstDir,
                    "/E", "/COPY:DAT", "/R:1", "/W:1", "/NP", "/XJ",
                    $"/LOG:{logFile}"
                };

                var result = await ProcessRunner.RunAsync("robocopy.exe", argumentList: argumentList);
                Assert.True(result.ExitCode < 8);

                var dst1 = Path.Combine(dstDir, "file1.txt");
                var dst2 = Path.Combine(dstDir, "SubFolder", "file2.txt");

                Assert.True(File.Exists(dst1));
                Assert.True(File.Exists(dst2));
                Assert.Equal("test data 1", File.ReadAllText(dst1));
                Assert.Equal("test data 2", File.ReadAllText(dst2));
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
