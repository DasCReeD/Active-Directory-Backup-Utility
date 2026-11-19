using Xunit;
using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ADShield.Core;
using ADShield.Models;
using Newtonsoft.Json;

namespace ADShield.Tests
{
    public class UnitTests
    {
        private static bool IsRunAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        [Fact]
        public void AppSettings_DefaultsAndJsonRoundTrip()
        {
            var settings = new AppSettings();

            Assert.Equal(1, settings.MaxConcurrentBackups);
            Assert.False(settings.AllowUnattendedVaultMount);
            Assert.Equal(Path.Combine("V:\\", "backups"), settings.BackupRootPath);

            var json = JsonConvert.SerializeObject(settings);
            Assert.DoesNotContain("AgentPort", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AgentApiKey", json, StringComparison.OrdinalIgnoreCase);

            settings.MaxConcurrentBackups = 3;
            settings.AllowUnattendedVaultMount = true;
            settings.MountLetter = "W";
            settings.BackupStorageRoot = "ad_backups";

            var roundTripped = JsonConvert.DeserializeObject<AppSettings>(JsonConvert.SerializeObject(settings));
            Assert.NotNull(roundTripped);
            Assert.Equal(3, roundTripped!.MaxConcurrentBackups);
            Assert.True(roundTripped.AllowUnattendedVaultMount);
            Assert.Equal(Path.Combine("W:\\", "ad_backups"), roundTripped.BackupRootPath);

            // Legacy settings JSON with removed AgentPort/AgentApiKey should deserialize cleanly
            var legacyJson = "{\"MountLetter\":\"Z\",\"AgentPort\":9000,\"AgentApiKey\":\"legacy-key\"}";
            var fromLegacy = JsonConvert.DeserializeObject<AppSettings>(legacyJson);
            Assert.NotNull(fromLegacy);
            Assert.Equal("Z", fromLegacy!.MountLetter);
            Assert.Equal(1, fromLegacy.MaxConcurrentBackups);
            Assert.False(fromLegacy.AllowUnattendedVaultMount);
        }

        [Fact]
        public void SchedulerService_InactiveStartAndIdempotentDispose()
        {
            var settings = new AppSettings { ScheduleActive = false };
            var scheduler = new SchedulerService(settings);

            scheduler.Start();
            scheduler.Stop();
            scheduler.Dispose();
            scheduler.Dispose();
            scheduler.Stop();
        }

        [Fact]
        public void SchedulerService_ParseCronHM_ValidAndFallback()
        {
            Assert.Equal((1, 0), SchedulerService.ParseCronHM("0 1 * * *", defaultHour: 2));
            Assert.Equal((23, 45), SchedulerService.ParseCronHM("45 23 * * 0", defaultHour: 0));
            Assert.Equal((3, 0), SchedulerService.ParseCronHM("invalid", defaultHour: 3));
            Assert.Equal((3, 0), SchedulerService.ParseCronHM("60 12 * * *", defaultHour: 3));
            Assert.Equal((3, 0), SchedulerService.ParseCronHM("10 24 * * *", defaultHour: 3));
        }

        [Fact]
        public async Task BackupOrchestrator_RunLocalDiskpart_ThrowsOnNonZeroExitCode()
        {
            if (!IsRunAsAdmin())
            {
                // diskpart.exe requires elevation (manifest requireAdministrator)
                return;
            }

            await Assert.ThrowsAnyAsync<Exception>(() =>
                BackupOrchestrator.RunLocalDiskpart("invalid_command_xyz", null, CancellationToken.None));
        }

        [Fact]
        public async Task ProcessRunner_CapturesExitCodeAndStreams()
        {
            var ok = await ProcessRunner.RunAsync("cmd.exe", arguments: "/c exit 0");
            Assert.Equal(0, ok.ExitCode);

            var fail = await ProcessRunner.RunAsync("cmd.exe", arguments: "/c exit 42");
            Assert.Equal(42, fail.ExitCode);

            var output = await ProcessRunner.RunAsync("cmd.exe", arguments: "/c echo hello_out & echo hello_err 1>&2");
            Assert.Equal(0, output.ExitCode);
            Assert.Contains("hello_out", output.StandardOutput);
            Assert.Contains("hello_err", output.StandardError);
        }

        [Fact]
        public async Task ProcessRunner_RespectsCancellation()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                ProcessRunner.RunAsync(
                    "cmd.exe",
                    arguments: "/c ping -n 10 127.0.0.1 >nul",
                    ct: cts.Token));
        }

        [Fact]
        public async Task ProcessRunner_RespectsTimeout()
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                ProcessRunner.RunAsync(
                    "cmd.exe",
                    arguments: "/c ping -n 10 127.0.0.1 >nul",
                    timeout: TimeSpan.FromMilliseconds(150)));
        }
    }
}
