using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.ServiceProcess;
using Newtonsoft.Json;

namespace ADShield.Core
{
    public class AgentConfig
    {
        public int Port { get; set; } = 9099;
        public string ApiKey { get; set; } = "ADShieldDefaultApiKeySecret_ChangeMe";
        public string AllowedServerIp { get; set; } = "";
    }

    public class AgentStatusResponse
    {
        [JsonProperty("status")]
        public string Status { get; set; } = "Idle"; // Idle, Running, Success, Failed

        [JsonProperty("exitCode")]
        public int ExitCode { get; set; } = 0;

        [JsonProperty("progressMessage")]
        public string ProgressMessage { get; set; } = "";

        [JsonProperty("usedGb")]
        public double UsedGb { get; set; } = 0;

        [JsonProperty("totalGb")]
        public double TotalGb { get; set; } = 0;
    }

    public class BackupRequestPayload
    {
        [JsonProperty("backupTarget")]
        public string BackupTarget { get; set; } = "";

        [JsonProperty("backupType")]
        public string BackupType { get; set; } = "Full";
    }

    public class AgentService : ServiceBase
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _listenerCts;
        private AgentConfig _config = new();
        private readonly string _configPath;
        
        // Backup execution state
        private readonly object _stateLock = new();
        private string _status = "Idle"; // Idle, Running, Success, Failed
        private int _exitCode = 0;
        private readonly StringBuilder _progressLogs = new();
        private Process? _activeProcess;
        private CancellationTokenSource? _backupCts;

        public AgentService()
        {
            ServiceName = "ADShieldAgent";
            
            // Resolve config path in the same directory as the executing executable
            var exeDir = AppContext.BaseDirectory;
            _configPath = Path.Combine(exeDir, "agent_config.json");
        }

        private void LogServiceEvent(string msg)
        {
            try
            {
                var exeDir = AppContext.BaseDirectory;
                var logFile = Path.Combine(exeDir, "agent_service.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
            }
            catch { }
        }

        protected override void OnStart(string[] args)
        {
            LogServiceEvent("Service starting...");
            LoadConfig();

            _listenerCts = new CancellationTokenSource();
            _listener = new HttpListener();
            
            // Listen on HTTP on all interfaces on the configured port
            _listener.Prefixes.Add($"http://*:{_config.Port}/");
            
            try
            {
                _listener.Start();
                LogServiceEvent($"HTTP API listener started on port {_config.Port}.");
                
                // Start background request handling loop
                Task.Run(() => HandleIncomingRequestsAsync(_listenerCts.Token));
            }
            catch (Exception ex)
            {
                LogServiceEvent($"Failed to start HTTP listener: {ex.Message}\r\n{ex.StackTrace}");
                throw;
            }
        }

        protected override void OnStop()
        {
            LogServiceEvent("Service stopping...");
            
            _listenerCts?.Cancel();
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception ex)
            {
                LogServiceEvent($"Error closing HTTP listener: {ex.Message}");
            }

            // Cancel any running backup
            CancelActiveBackup();
            LogServiceEvent("Service stopped.");
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var loaded = JsonConvert.DeserializeObject<AgentConfig>(json);
                    if (loaded != null)
                    {
                        _config = loaded;
                        LogServiceEvent($"Loaded config from {_configPath}. Allowed Server IP: '{_config.AllowedServerIp}', Port: {_config.Port}");
                        return;
                    }
                }
                LogServiceEvent($"Config file {_configPath} not found. Using defaults.");
            }
            catch (Exception ex)
            {
                LogServiceEvent($"Failed to load configuration: {ex.Message}. Using default settings.");
            }
        }

        private async Task HandleIncomingRequestsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener!.GetContextAsync();
                    // Process context on threadpool
                    _ = Task.Run(() => ProcessRequestAsync(context), ct);
                }
                catch (HttpListenerException)
                {
                    // Occurs when listener is stopped, safe to break/ignore
                    break;
                }
                catch (Exception ex)
                {
                    LogServiceEvent($"Error accepting HTTP connection: {ex.Message}");
                    await Task.Delay(100, ct);
                }
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var resp = context.Response;

            // 1. IP Whitelisting check (security)
            if (!string.IsNullOrEmpty(_config.AllowedServerIp))
            {
                var remoteIp = req.RemoteEndPoint.Address.ToString();
                // Map IPv6 loopback/mapped addresses if running locally
                if (remoteIp == "::1") remoteIp = "127.0.0.1";
                if (remoteIp.StartsWith("::ffff:")) remoteIp = remoteIp.Substring(7);

                var allowedIp = _config.AllowedServerIp;
                if (allowedIp == "::1") allowedIp = "127.0.0.1";

                if (remoteIp != allowedIp && remoteIp != "127.0.0.1")
                {
                    LogServiceEvent($"Security Block: Unauthorized IP '{remoteIp}' tried to access agent (Allowed: '{_config.AllowedServerIp}')");
                    SendResponse(resp, HttpStatusCode.Forbidden, "Forbidden: IP address not whitelisted.");
                    return;
                }
            }

            // 2. API Key Header validation (security)
            var keyHeader = req.Headers["X-ADShield-Key"];
            if (keyHeader != _config.ApiKey)
            {
                LogServiceEvent($"Security Block: Invalid API Key from {req.RemoteEndPoint.Address}");
                SendResponse(resp, HttpStatusCode.Unauthorized, "Unauthorized: Invalid or missing X-ADShield-Key header.");
                return;
            }

            var path = req.Url?.AbsolutePath.ToLower() ?? "";
            var method = req.HttpMethod.ToUpper();

            try
            {
                if (path == "/status" && method == "GET")
                {
                    var statusResponse = new AgentStatusResponse();
                    try
                    {
                        var drive = new DriveInfo("C");
                        statusResponse.TotalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
                        statusResponse.UsedGb = statusResponse.TotalGb - (drive.AvailableFreeSpace / (1024.0 * 1024 * 1024));
                    }
                    catch { }

                    lock (_stateLock)
                    {
                        statusResponse.Status = _status;
                        statusResponse.ExitCode = _exitCode;
                        statusResponse.ProgressMessage = _progressLogs.ToString();
                    }
                    var json = JsonConvert.SerializeObject(statusResponse);
                    SendJsonResponse(resp, HttpStatusCode.OK, json);
                }
                else if (path == "/backup" && method == "POST")
                {
                    string body;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    var payload = JsonConvert.DeserializeObject<BackupRequestPayload>(body);
                    if (payload == null || string.IsNullOrWhiteSpace(payload.BackupTarget))
                    {
                        SendResponse(resp, HttpStatusCode.BadRequest, "Bad Request: Missing or invalid 'backupTarget' in body.");
                        return;
                    }

                    // Strict Input Sanitization & Validation (Defense-in-depth)
                    var target = payload.BackupTarget.Trim();
                    if (target.Contains("\"") || target.Contains("'") || target.Contains("\n") || target.Contains("\r"))
                    {
                        SendResponse(resp, HttpStatusCode.BadRequest, "Bad Request: backupTarget must not contain quotes or control characters.");
                        return;
                    }
                    if (!target.StartsWith(@"\\"))
                    {
                        SendResponse(resp, HttpStatusCode.BadRequest, "Bad Request: backupTarget must be a UNC share path starting with '\\\\'.");
                        return;
                    }
                    if (target.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    {
                        SendResponse(resp, HttpStatusCode.BadRequest, "Bad Request: backupTarget contains invalid path characters.");
                        return;
                    }

                    bool started = false;
                    lock (_stateLock)
                    {
                        if (_status == "Running")
                        {
                            SendResponse(resp, HttpStatusCode.Conflict, "Conflict: A backup job is already in progress.");
                            return;
                        }

                        _status = "Running";
                        _exitCode = 0;
                        _progressLogs.Clear();
                        _progressLogs.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Client backup session initialized by backup server.");
                        _progressLogs.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Staging Target: {payload.BackupTarget}");
                        
                        _backupCts = new CancellationTokenSource();
                        started = true;
                    }

                    if (started)
                    {
                        // Launch backup in the background
                        _ = Task.Run(() => RunWbadminBackupAsync(payload.BackupTarget, _backupCts.Token));
                        
                        // Return 202 Accepted immediately
                        SendResponse(resp, HttpStatusCode.Accepted, "Accepted: Backup session started.");
                    }
                }
                else if (path == "/cancel" && method == "POST")
                {
                    CancelActiveBackup();
                    SendResponse(resp, HttpStatusCode.OK, "OK: Cancel signal processed.");
                }
                else
                {
                    SendResponse(resp, HttpStatusCode.NotFound, "Not Found.");
                }
            }
            catch (Exception ex)
            {
                LogServiceEvent($"Error handling API request ({method} {path}): {ex.Message}");
                SendResponse(resp, HttpStatusCode.InternalServerError, $"Internal Server Error: {ex.Message}");
            }
        }

        private void RunWbadminBackupAsync(string backupTarget, CancellationToken ct)
        {
            LogServiceEvent($"Starting local backup to target: '{backupTarget}'");
            
            // Format wbadmin args
            var args = $"start backup -backuptarget:\"{backupTarget}\" -include:c: -allcritical -quiet";

            lock (_stateLock)
            {
                _progressLogs.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Executing command: wbadmin.exe {args}");
            }

            try
            {
                var psi = new ProcessStartInfo("wbadmin.exe", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var proc = new Process { StartInfo = psi };
                
                lock (_stateLock)
                {
                    _activeProcess = proc;
                }

                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (_stateLock)
                        {
                            _progressLogs.AppendLine(e.Data);
                        }
                    }
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (_stateLock)
                        {
                            _progressLogs.AppendLine($"[STDERR] {e.Data}");
                        }
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // Wait for exit or cancellation
                while (!proc.HasExited)
                {
                    if (ct.IsCancellationRequested)
                    {
                        LogServiceEvent("Cancellation requested. Terminating wbadmin process...");
                        try
                        {
                            proc.Kill(true); // Kill entire process tree
                        }
                        catch { }
                        break;
                    }
                    Thread.Sleep(500);
                }

                if (ct.IsCancellationRequested)
                {
                    lock (_stateLock)
                    {
                        _status = "Failed";
                        _exitCode = -1;
                        _progressLogs.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Backup session cancelled by server command.");
                    }
                    LogServiceEvent("Backup job cancelled successfully.");
                }
                else
                {
                    var exitCode = proc.ExitCode;
                    lock (_stateLock)
                    {
                        _exitCode = exitCode;
                        _status = (exitCode == 0) ? "Success" : "Failed";
                        _progressLogs.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] wbadmin process finished with exit code: {exitCode}");
                    }
                    LogServiceEvent($"Backup job completed. Status: {_status}, Exit Code: {exitCode}");
                }
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _status = "Failed";
                    _exitCode = 999;
                    _progressLogs.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Fatal backup engine error: {ex.Message}");
                    _progressLogs.AppendLine(ex.StackTrace ?? "");
                }
                LogServiceEvent($"Fatal error during backup run: {ex.Message}\r\n{ex.StackTrace}");
            }
            finally
            {
                lock (_stateLock)
                {
                    _activeProcess?.Dispose();
                    _activeProcess = null;
                }
            }
        }

        private void CancelActiveBackup()
        {
            lock (_stateLock)
            {
                if (_status == "Running")
                {
                    _backupCts?.Cancel();
                    
                    if (_activeProcess != null)
                    {
                        try
                        {
                            _activeProcess.Kill(true);
                        }
                        catch { }
                    }
                }
            }
        }

        private static void SendResponse(HttpListenerResponse resp, HttpStatusCode code, string message)
        {
            try
            {
                resp.StatusCode = (int)code;
                var buffer = Encoding.UTF8.GetBytes(message);
                resp.ContentLength64 = buffer.Length;
                using var output = resp.OutputStream;
                output.Write(buffer, 0, buffer.Length);
            }
            catch { }
        }

        private static void SendJsonResponse(HttpListenerResponse resp, HttpStatusCode code, string json)
        {
            try
            {
                resp.StatusCode = (int)code;
                resp.ContentType = "application/json";
                var buffer = Encoding.UTF8.GetBytes(json);
                resp.ContentLength64 = buffer.Length;
                using var output = resp.OutputStream;
                output.Write(buffer, 0, buffer.Length);
            }
            catch { }
        }
    }
}
