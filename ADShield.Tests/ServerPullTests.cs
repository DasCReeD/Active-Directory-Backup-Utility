using Xunit;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ADShield.Core;

namespace ADShield.Tests
{
    public class ServerPullTests
    {
        [Theory(Skip = "Requires a live remote client computer configured with WMI/DCOM permissions")]
        [InlineData("CLIENT-PC")]
        public async Task TestR2LSymlinkEvaluation(string computerName)
        {
            var progress = new Progress<string>(msg => Console.WriteLine(msg));
            
            // Query current R2L state on the remote machine
            string? currentValue = null;
            var scope = new System.Management.ManagementScope($@"\\{computerName}\root\default");
            scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
            scope.Options.EnablePrivileges = true;
            scope.Connect();

            using (var regClass = new System.Management.ManagementClass(scope,
                new System.Management.ManagementPath("StdRegProv"), null))
            {
                uint hklm = 0x80000002;
                using var inParams = regClass.GetMethodParameters("GetDWORDValue");
                inParams["hDefKey"] = hklm;
                inParams["sSubKeyName"] = @"SYSTEM\CurrentControlSet\Control\FileSystem";
                inParams["sValueName"] = "SymlinkRemoteToLocalEvaluation";

                using var outParams = regClass.InvokeMethod("GetDWORDValue", inParams, null);
                var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
                if (retVal == 0)
                    currentValue = outParams["uValue"]?.ToString();
            }

            // Re-write to enable
            using (var regClass = new System.Management.ManagementClass(scope,
                new System.Management.ManagementPath("StdRegProv"), null))
            {
                uint hklm = 0x80000002;
                using var inParams = regClass.GetMethodParameters("SetDWORDValue");
                inParams["hDefKey"] = hklm;
                inParams["sSubKeyName"] = @"SYSTEM\CurrentControlSet\Control\FileSystem";
                inParams["sValueName"] = "SymlinkRemoteToLocalEvaluation";
                inParams["uValue"] = (uint)1;

                using var outParams = regClass.InvokeMethod("SetDWORDValue", inParams, null);
                var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
                Assert.Equal(0u, retVal);
            }

            // Verify
            using (var regClass = new System.Management.ManagementClass(scope,
                new System.Management.ManagementPath("StdRegProv"), null))
            {
                uint hklm = 0x80000002;
                using var inParams = regClass.GetMethodParameters("GetDWORDValue");
                inParams["hDefKey"] = hklm;
                inParams["sSubKeyName"] = @"SYSTEM\CurrentControlSet\Control\FileSystem";
                inParams["sValueName"] = "SymlinkRemoteToLocalEvaluation";

                using var outParams = regClass.InvokeMethod("GetDWORDValue", inParams, null);
                var retVal = Convert.ToUInt32(outParams["ReturnValue"]);
                var value = Convert.ToUInt32(outParams["uValue"]);

                Assert.Equal(0u, retVal);
                Assert.Equal(1u, value);
            }
        }

        [Theory(Skip = "Requires a live remote client computer configured with WMI/DCOM permissions")]
        [InlineData("CLIENT-PC")]
        public async Task TestVssSymlinkAccess(string computerName)
        {
            var progress = new Progress<string>(msg => Console.WriteLine(msg));
            string? shadowId = null;
            bool symlinkCreated = false;
            bool shareCreated = false;
            var tempLinkPath = @"C:\adshield_test_link";
            var hiddenShareName = "adshield_test_share$";

            try
            {
                Assert.True(VssManager.TestWmiConnectivity(computerName, out var wmiError), $"WMI not accessible: {wmiError}");

                shadowId = VssManager.CreateRemoteShadowCopy(computerName, @"C:\", progress: progress);
                Assert.NotNull(shadowId);

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
                Assert.NotEmpty(shadowDevicePath);

                // Clean up any stale link
                try { RunTestWmiCommand(computerName, scope, $"cmd.exe /c if exist {tempLinkPath} rmdir {tempLinkPath}", 10000); }
                catch { }

                RunTestWmiCommand(computerName, scope, $"cmd.exe /c mklink /d {tempLinkPath} {shadowDevicePath}", 15000);
                symlinkCreated = true;

                try { RunTestWmiCommand(computerName, scope, $"cmd.exe /c net share {hiddenShareName} /DELETE /Y", 10000); }
                catch { }

                RunTestWmiCommand(computerName, scope, $"cmd.exe /c net share {hiddenShareName}={tempLinkPath} /GRANT:Everyone,READ", 15000);
                shareCreated = true;

                var uncPath = $@"\\{computerName}\{hiddenShareName}";
                Assert.True(Directory.Exists(uncPath));
                var entries = Directory.GetDirectories(uncPath);
                Assert.NotEmpty(entries);
            }
            finally
            {
                var scope = new System.Management.ManagementScope($@"\\{computerName}\root\cimv2");
                scope.Options.Impersonation = System.Management.ImpersonationLevel.Impersonate;
                scope.Options.EnablePrivileges = true;
                scope.Connect();

                if (shareCreated)
                {
                    try { RunTestWmiCommand(computerName, scope, $"cmd.exe /c net share {hiddenShareName} /DELETE /Y", 10000); }
                    catch { }
                }

                if (symlinkCreated)
                {
                    try { RunTestWmiCommand(computerName, scope, $"cmd.exe /c rmdir {tempLinkPath}", 10000); }
                    catch { }
                }

                if (shadowId != null)
                {
                    try { VssManager.DeleteShadowCopy(shadowId, progress); }
                    catch { }
                }
            }
        }

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
