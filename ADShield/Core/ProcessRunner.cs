using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ADShield.Core
{
    public class ProcessRunnerResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
    }

    public static class ProcessRunner
    {
        public static async Task<ProcessRunnerResult> RunAsync(
            string fileName,
            string? arguments = null,
            IEnumerable<string>? argumentList = null,
            TimeSpan? timeout = null,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (arguments != null)
            {
                psi.Arguments = arguments;
            }

            if (argumentList != null)
            {
                foreach (var arg in argumentList)
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            using var proc = new Process { StartInfo = psi };

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            proc.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    lock (stdoutBuilder)
                    {
                        stdoutBuilder.AppendLine(e.Data);
                    }
                    progress?.Report(e.Data);
                }
            };

            proc.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    lock (stderrBuilder)
                    {
                        stderrBuilder.AppendLine(e.Data);
                    }
                    progress?.Report($"[ERROR] {e.Data}");
                }
            };

            if (!proc.Start())
            {
                throw new Exception($"Failed to start process: {fileName}");
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout.HasValue)
            {
                linkedCts.CancelAfter(timeout.Value);
            }

            try
            {
                await proc.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch { }

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException($"Process {fileName} execution cancelled.", ct);
                }
                else
                {
                    throw new TimeoutException($"Process {fileName} execution timed out after {timeout}.");
                }
            }

            // Ensure output streams are fully flushed and events are finished processing
            proc.WaitForExit();

            string stdout;
            string stderr;
            lock (stdoutBuilder) { stdout = stdoutBuilder.ToString(); }
            lock (stderrBuilder) { stderr = stderrBuilder.ToString(); }

            return new ProcessRunnerResult
            {
                ExitCode = proc.ExitCode,
                StandardOutput = stdout,
                StandardError = stderr
            };
        }
    }
}
