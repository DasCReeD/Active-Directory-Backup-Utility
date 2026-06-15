using Xunit;
using System;
using System.Threading;
using System.Threading.Tasks;
using ADShield.Core;

namespace ADShield.Tests
{
    public class SelfHealingTests
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
        public async Task TestSelfHealingDiagnostics()
        {
            if (!IsRunAsAdmin())
            {
                return;
            }

            // Note: This test requires administrative privileges (diskpart operations)
            var progress = new Progress<string>(msg => Console.WriteLine(msg));
            await SelfHealingDiagnostics.RunDiagnosticsAsync(progress, CancellationToken.None);
        }
    }
}
