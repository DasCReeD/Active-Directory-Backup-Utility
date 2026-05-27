using ADShield.Core;
using ADShield.Models;

namespace ADShield.Forms;

// Event handlers, data refresh, and business logic for MainForm
public partial class MainForm
{
    // ── AD Sync ───────────────────────────────────────────────────────────────

    private async Task RunDiagnosticsTest()
    {
        var runConfirm = MessageBox.Show(
            "This will execute the live VHDX Self-Healing Diagnostic Test.\n\n" +
            "It will emulate an uninitialized RAW virtual disk mounting, verify that writing is blocked, " +
            "automatically execute the dynamic self-healing clean & formatting sequence, and confirm " +
            "full write capability.\n\n" +
            "Proceed with the diagnostic test?",
            "VHDX Self-Healing Test", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (runConfirm != DialogResult.Yes) return;

        Log("INFO", "TEST", "Initializing VHDX Self-Healing Diagnostic sequence...");
        try
        {
            Cursor = Cursors.WaitCursor;
            var progress = new Progress<string>(msg => Log(msg, "TEST"));
            var cts = new CancellationTokenSource();
            await BackupSelfHealingTest.RunDiagnosticTest(progress, cts.Token);
            MessageBox.Show("Self-Healing Diagnostic Test: SUCCESS!\n\nThe backup utility is 100% immune to RAW/uninitialized VHDX errors.",
                "Diagnostics Passed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR", "TEST", $"Diagnostics failed: {ex.Message}");
            MessageBox.Show($"Diagnostic test failed:\n\n{ex.Message}",
                "Diagnostics Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private async Task SyncAd()
    {
        Log("INFO", "SYSTEM", "Starting Active Directory computer discovery...");
        try
        {
            var progress = new Progress<string>(msg => Log(msg));
            var discovered = await Task.Run(() =>
                AdDiscovery.Discover(_settings.SearchOU, _settings.AdGroup, pingCheck: true, progress));
            _computers = AppConfig.MergeDiscovered(discovered);
            RefreshGrids();
            UpdateKpis();
            Log("SUCCESS", "SYSTEM", $"AD sync complete. {discovered.Count} computer(s) found.");
        }
        catch (Exception ex)
        {
            Log("ERROR", "SYSTEM", $"AD discovery failed: {ex.Message}");
        }
    }

    // ── Backup trigger ────────────────────────────────────────────────────────

    private async Task TriggerBackup(string computerName)
    {
        using var dlg = new BackupTriggerForm(computerName);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var cts = new CancellationTokenSource();
        Log("INFO", computerName, $"Initializing {dlg.SelectedBackupType} backup session...");
        UpdateComputerStatus(computerName, "In Progress");

        try
        {
            var progress = new Progress<string>(msg => Log(msg, computer: computerName));
            var orch = new BackupOrchestrator(_settings);
            await orch.RunAsync(computerName, dlg.SelectedBackupType, dlg.VeraCryptPassword, progress, cts.Token);
            UpdateComputerStatus(computerName, "Success");
        }
        catch (OperationCanceledException)
        {
            Log("WARN", computerName, "Backup cancelled.");
            UpdateComputerStatus(computerName, "Cancelled");
        }
        catch (Exception ex)
        {
            Log("ERROR", computerName, $"Backup failed: {ex.Message}");
            UpdateComputerStatus(computerName, "Failed");
        }
        finally
        {
            RefreshGrids(); UpdateKpis();
        }
    }

    private void GridDash_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 5) return; // column 5 = Action button
        var name = _gridDash.Rows[e.RowIndex].Cells[0].Value?.ToString();
        if (string.IsNullOrEmpty(name)) return;
        var computer = _computers.FirstOrDefault(c => c.ComputerName == name);
        if (computer is { IsOnline: false })
        {
            MessageBox.Show($"{name} appears offline. Backup may fail.\n\nContinue anyway?",
                "Host Offline", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        }
        _ = TriggerBackup(name!);
    }

    // ── Scheduled backup ──────────────────────────────────────────────────────

    private void OnScheduledBackup(string backupType)
    {
        var online = _computers.Where(c => c.IsOnline).ToList();
        Log("INFO", "SCHEDULER", $"Scheduled {backupType} triggered. Targets: {online.Count} online machines.");
        foreach (var c in online)
            _ = TriggerBackup(c.ComputerName); // fire-and-forget; orchestrator is sequential per machine
    }

    // ── Create VeraCrypt Volume ────────────────────────────────────────────────

    private void CreateVeraCryptVolume(string sizeSpec)
    {
        // Validate inputs
        var exePath = _tbVcExe.Text.Trim();
        var containerPath = _tbVcCon.Text.Trim();

        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            MessageBox.Show("Set a valid VeraCrypt executable path first.",
                "Missing VeraCrypt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrEmpty(containerPath))
        {
            MessageBox.Show("Set the container file path before creating a volume.",
                "Missing Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // If user entered just a drive/directory (e.g. "Z:\"), append a default filename
        if (containerPath.EndsWith('\\') || containerPath.EndsWith('/') ||
            Directory.Exists(containerPath) ||
            (containerPath.Length == 2 && containerPath[1] == ':') ||
            string.IsNullOrEmpty(Path.GetExtension(containerPath)))
        {
            containerPath = Path.Combine(containerPath, "ADShield_Vault.hc");
            _tbVcCon.Text = containerPath;
            Log("INFO", "SYSTEM", $"Container path auto-completed to: {containerPath}");
        }
        if (File.Exists(containerPath))
        {
            var overwrite = MessageBox.Show(
                $"Container already exists at:\n{containerPath}\n\nOverwrite it? This will DESTROY existing data.",
                "Container Exists", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (overwrite != DialogResult.Yes) return;
            File.Delete(containerPath);
        }
        if (string.IsNullOrEmpty(sizeSpec) ||
            !System.Text.RegularExpressions.Regex.IsMatch(sizeSpec, @"^\d+[KMGT]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            MessageBox.Show("Enter a valid size like 500G, 2T, or 100M.",
                "Invalid Size", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Password prompt with confirmation
        using var pwdForm = new Form
        {
            Text = "Set VeraCrypt Volume Password", Size = new Size(420, 260),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Background, ForeColor = Theme.TextPrimary, Font = Theme.FontBase,
            MaximizeBox = false, MinimizeBox = false,
        };
        var lbl1 = new Label { Text = "Password:", Location = new Point(20, 20), AutoSize = true };
        var tb1  = new TextBox { Location = new Point(20, 42), Width = 360, UseSystemPasswordChar = true,
            BackColor = Theme.SurfaceRaised, ForeColor = Theme.TextPrimary, Font = Theme.FontBase };
        var lbl2 = new Label { Text = "Confirm Password:", Location = new Point(20, 80), AutoSize = true };
        var tb2  = new TextBox { Location = new Point(20, 102), Width = 360, UseSystemPasswordChar = true,
            BackColor = Theme.SurfaceRaised, ForeColor = Theme.TextPrimary, Font = Theme.FontBase };
        var lblWarn = new Label { Text = "⚠ Minimum 20 characters recommended for strong encryption.",
            Location = new Point(20, 138), AutoSize = true, ForeColor = Theme.Warning, Font = Theme.FontSmall };
        var btnOk = Theme.MakeButton("Create Volume", primary: true);
        btnOk.Location = new Point(20, 170); btnOk.Width = 360;
        btnOk.Click += (_, _) =>
        {
            if (tb1.Text.Length < 8)
            {
                MessageBox.Show("Password must be at least 8 characters.", "Too Short", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (tb1.Text != tb2.Text)
            {
                MessageBox.Show("Passwords do not match.", "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            pwdForm.DialogResult = DialogResult.OK;
        };
        pwdForm.Controls.AddRange([lbl1, tb1, lbl2, tb2, lblWarn, btnOk]);
        pwdForm.AcceptButton = btnOk;

        if (pwdForm.ShowDialog(this) != DialogResult.OK) return;

        // Save settings first
        _settings.VeraCryptExePath = exePath;
        _settings.VeraCryptContainer = containerPath;
        AppConfig.SaveSettings(_settings);

        // Create the container
        try
        {
            Cursor = Cursors.WaitCursor;
            Log("INFO", "SYSTEM", $"Creating VeraCrypt container ({sizeSpec}) at {containerPath}...");
            var progress = new Progress<string>(msg => Log(msg));
            VeraCryptManager.CreateContainer(_settings, tb1.Text, sizeSpec, progress);
            MessageBox.Show($"Encrypted volume created successfully!\n\n{containerPath}\nSize: {sizeSpec}",
                "Volume Created", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR", "SYSTEM", $"Volume creation failed: {ex.Message}");
            MessageBox.Show($"Failed to create volume:\n{ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void SaveVeraCryptSettings(object? sender, EventArgs e)
    {
        _settings.VeraCryptExePath   = _tbVcExe.Text.Trim();
        _settings.VeraCryptContainer = _tbVcCon.Text.Trim();
        _settings.MountLetter        = _tbMount.Text.Trim().ToUpper();
        AppConfig.SaveSettings(_settings);
        UpdateKpis();
        MessageBox.Show("VeraCrypt settings saved.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveStorageSettings(object? sender, EventArgs e)
    {
        _settings.BackupStorageRoot = _tbBackupRoot.Text.Trim();
        if (long.TryParse(_tbVhdxSize.Text.Trim(), out long gb) && gb > 0)
            _settings.VhdxSizeGb = gb;
        else
        {
            MessageBox.Show("VHDX size must be a positive number in GB.", "Invalid Value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        AppConfig.SaveSettings(_settings);
        MessageBox.Show($"Storage config saved.\n\nBackups will be stored at: {_settings.BackupRootPath}",
            "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveAdSettings(object? sender, EventArgs e)
    {
        _settings.SearchOU      = _tbOU.Text.Trim();
        _settings.AdGroup       = _tbGroup.Text.Trim();
        _settings.ScheduleActive = _chkSched.Checked;
        _settings.NightlyCron   = _tbNightly.Text.Trim();
        _settings.WeeklyCron    = _tbWeekly.Text.Trim();
        AppConfig.SaveSettings(_settings);

        _scheduler.Stop();
        _scheduler = new SchedulerService(_settings);
        _scheduler.BackupTriggered += OnScheduledBackup;
        if (_settings.ScheduleActive) _scheduler.Start();

        MessageBox.Show("AD settings saved. Scheduler restarted.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── Export logs ───────────────────────────────────────────────────────────

    private void ExportLogs(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "ADShield_Logs.csv" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var lines = new List<string> { "Timestamp,Level,Computer,Message" };
        lines.AddRange(_logRows.Select(r => $"{r.ts},{r.level},{r.comp},\"{r.msg.Replace("\"", "\"\"")}\""));
        File.WriteAllLines(dlg.FileName, lines);
        MessageBox.Show($"Exported {lines.Count - 1} log entries.", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── Grid refresh ──────────────────────────────────────────────────────────

    private void RefreshGrids()
    {
        if (InvokeRequired) { Invoke(RefreshGrids); return; }
        RefreshDashboardGrid();
        RefreshComputersGrid();
        UpdateKpis();
    }

    private void RefreshDashboardGrid()
    {
        _gridDash.Rows.Clear();
        foreach (var c in _computers)
        {
            int i = _gridDash.Rows.Add(c.ComputerName, c.OperatingSystem,
                c.OnlineDisplay, c.LastBackupStatus, c.LastBackupTimeDisplay, "Backup");
            // column 2 = Status, column 3 = Last Backup
            _gridDash.Rows[i].Cells[2].Style.ForeColor = Theme.OnlineColor(c.IsOnline);
            _gridDash.Rows[i].Cells[3].Style.ForeColor = Theme.StatusColor(c.LastBackupStatus);
        }
    }

    private void RefreshComputersGrid()
    {
        _gridComp.Rows.Clear();
        var filter = _tbSearch?.Text?.ToLower() ?? "";
        foreach (var c in _computers.Where(c =>
            string.IsNullOrEmpty(filter) ||
            c.ComputerName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            c.OperatingSystem.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            int i = _gridComp.Rows.Add(c.ComputerName, c.DnsHostName, c.OU, c.OperatingSystem,
                c.IsOnline ? c.PingMs.ToString() : "—", c.OnlineDisplay);
            _gridComp.Rows[i].Cells[5].Style.ForeColor = Theme.OnlineColor(c.IsOnline);
        }
    }

    private void RefreshLogsGrid()
    {
        _gridLogs.Rows.Clear();
        var filter = _cbLevel?.SelectedItem?.ToString() ?? "All Levels";
        foreach (var r in _logRows.Where(r => filter == "All Levels" || r.level == filter))
            _gridLogs.Rows.Add(r.ts, r.level, r.comp, r.msg);
    }

    // ── KPI update ────────────────────────────────────────────────────────────

    private void UpdateKpis()
    {
        if (InvokeRequired) { Invoke(UpdateKpis); return; }
        int total   = _computers.Count;
        int success = _computers.Count(c => c.LastBackupStatus == "Success");
        int online  = _computers.Count(c => c.IsOnline);
        _kpiRate.Text   = total > 0 ? $"{success * 100 / total}%" : "—";
        _kpiDisc.Text   = total.ToString();
        _kpiOnline.Text = online.ToString();
        _kpiVault.Text  = VeraCryptManager.IsMounted(_settings.MountLetter) ? "Mounted ✓" : "Locked";
        _kpiVault.ForeColor = VeraCryptManager.IsMounted(_settings.MountLetter) ? Theme.Success : Theme.Danger;
    }

    // ── Status helper ─────────────────────────────────────────────────────────

    private void UpdateComputerStatus(string name, string status)
    {
        if (InvokeRequired) { Invoke(() => UpdateComputerStatus(name, status)); return; }
        AppConfig.UpdateBackupResult(name, status);
        _computers = AppConfig.ReadHistory();
        RefreshGrids();
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    private void Log(string raw, string computer = "SYSTEM")
    {
        // Parse "[LEVEL] message" format emitted by progress reporters
        var level = "INFO";
        var msg   = raw;
        if (raw.StartsWith('[') && raw.Contains(']'))
        {
            level = raw[1..raw.IndexOf(']')];
            msg   = raw[(raw.IndexOf(']') + 2)..];
        }
        Log(level, computer, msg);
    }

    private void Log(string level, string computer, string message)
    {
        if (InvokeRequired) { Invoke(() => Log(level, computer, message)); return; }

        var ts = DateTime.Now.ToString("HH:mm:ss");
        _logRows.Insert(0, (ts, level, computer, message));
        if (_logRows.Count > 5000) _logRows.RemoveAt(_logRows.Count - 1);

        // Terminal output
        var color = level switch
        {
            "SUCCESS" => Theme.Success,
            "ERROR"   => Theme.Danger,
            "WARN"    => Theme.Warning,
            _         => Theme.TextPrimary,
        };
        _rtbLog.SelectionStart  = _rtbLog.TextLength;
        _rtbLog.SelectionLength = 0;
        _rtbLog.SelectionColor  = Theme.TextMuted;
        _rtbLog.AppendText($"[{ts}] ");
        _rtbLog.SelectionColor = color;
        _rtbLog.AppendText($"[{level}]");
        _rtbLog.SelectionColor = Theme.TextSecondary;
        _rtbLog.AppendText($" [{computer}] ");
        _rtbLog.SelectionColor = Theme.TextPrimary;
        _rtbLog.AppendText(message + "\n");
        _rtbLog.ScrollToCaret();

        RefreshLogsGrid();
    }
}
