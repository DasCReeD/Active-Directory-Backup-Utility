// Frontend Logic Orchestration for AD Shield Dashboard

document.addEventListener('DOMContentLoaded', () => {
  // Navigation & Tabs State Binding
  const navItems = document.querySelectorAll('.nav-item');
  const tabContents = document.querySelectorAll('.tab-content');
  const pageTitle = document.getElementById('page-title');

  navItems.forEach(item => {
    item.addEventListener('click', (e) => {
      e.preventDefault();
      const tab = item.getAttribute('data-tab');
      
      navItems.forEach(nav => nav.classList.remove('active'));
      tabContents.forEach(content => content.classList.remove('active'));
      
      item.classList.add('active');
      const targetTab = document.getElementById(`tab-${tab}`);
      if (targetTab) targetTab.classList.add('active');
      
      // Dynamic Title
      pageTitle.innerText = item.textContent.trim();
    });
  });

  // Real-time Console Log Binding
  const terminalConsole = document.getElementById('terminal-console');
  const btnClearConsole = document.getElementById('btn-clear-console');
  const historicalLogsContainer = document.getElementById('historical-logs-container');
  const filterLogLevel = document.getElementById('filter-log-level');

  let activeLogsRegistry = [];

  function appendTerminalLine(computer, level, message, timestamp) {
    const timeStr = timestamp ? new Date(timestamp).toLocaleTimeString() : new Date().toLocaleTimeString();
    const line = document.createElement('div');
    line.className = `terminal-line ${level.toLowerCase()}`;
    line.textContent = `[${timeStr}] [${computer}] [${level}] - ${message}`;
    
    terminalConsole.appendChild(line);
    terminalConsole.scrollTop = terminalConsole.scrollHeight;

    // Save to local log list
    activeLogsRegistry.unshift({ computer, level, message, timestamp: timestamp || new Date().toISOString() });
    renderHistoricalLogs();
  }

  btnClearConsole.addEventListener('click', () => {
    terminalConsole.innerHTML = '<div class="terminal-line system">[SYSTEM] Terminal logs cleared. Active socket running.</div>';
  });

  function renderHistoricalLogs() {
    const levelFilter = filterLogLevel.value;
    historicalLogsContainer.innerHTML = '';
    
    const filtered = activeLogsRegistry.filter(log => {
      if (levelFilter === 'ALL') return true;
      return log.level === levelFilter;
    });

    if (filtered.length === 0) {
      historicalLogsContainer.innerHTML = '<div class="table-empty">No operational logs recorded matching filters.</div>';
      return;
    }

    filtered.forEach(log => {
      const row = document.createElement('div');
      row.className = 'log-row';
      
      const timeStr = new Date(log.timestamp).toLocaleString();
      const levelClass = log.level.toLowerCase();
      
      row.innerHTML = `
        <div class="log-meta">
          <span class="log-time">${timeStr}</span>
          <span class="log-comp">${log.computer}</span>
          <span class="status-result ${levelClass}">${log.level}</span>
        </div>
        <div class="log-msg">${log.message}</div>
      `;
      historicalLogsContainer.appendChild(row);
    });
  }

  filterLogLevel.addEventListener('change', renderHistoricalLogs);

  // WebSockets Setup
  let ws;
  function connectWebSocket() {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    ws = new WebSocket(`${protocol}//${window.location.host}`);

    ws.onmessage = (event) => {
      const payload = JSON.parse(event.data);
      if (payload.type === 'log') {
        const { computer, level, message, timestamp } = payload.data;
        appendTerminalLine(computer, level, message, timestamp);
      } else if (payload.type === 'list_update') {
        renderComputerTables(payload.data);
      } else if (payload.type === 'sys') {
        appendTerminalLine('SYSTEM', 'INFO', payload.message);
      }
    };

    ws.onclose = () => {
      appendTerminalLine('SYSTEM', 'WARN', 'WebSocket disconnected. Retrying connection in 5 seconds...');
      setTimeout(connectWebSocket, 5000);
    };

    ws.onerror = (err) => {
      console.error('WebSocket Error:', err);
    };
  }

  connectWebSocket();

  // Load and Render Computer Data tables
  const computerTableBody = document.getElementById('computer-table-body');
  const inventoryTableBody = document.getElementById('inventory-table-body');
  const searchComputersInput = document.getElementById('search-computers');
  
  let localComputerCache = [];

  function renderComputerTables(computers) {
    localComputerCache = computers;
    
    // Update KPI panels dynamically
    document.getElementById('kpi-discovered-count').innerText = computers.length;
    
    const onlineCount = computers.filter(c => c.isOnline).length;
    document.getElementById('kpi-online-count').innerText = onlineCount;

    const successfulBackups = computers.filter(c => c.lastBackupStatus === 'Success').length;
    const completedBackups = computers.filter(c => c.lastBackupStatus === 'Success' || c.lastBackupStatus === 'Failed').length;
    const rate = completedBackups > 0 ? Math.round((successfulBackups / completedBackups) * 100) : 0;
    document.getElementById('kpi-success-rate').innerText = `${rate}%`;

    // Filter list by search query if any
    const searchQuery = searchComputersInput.value.toLowerCase().trim();
    const filteredComputers = computers.filter(c => {
      return c.computerName.toLowerCase().includes(searchQuery) ||
             c.operatingSystem.toLowerCase().includes(searchQuery);
    });

    // Populate Active Dashboard Table
    if (filteredComputers.length === 0) {
      computerTableBody.innerHTML = '<tr><td colspan="6" class="table-empty">No target domain computers match search criteria.</td></tr>';
      inventoryTableBody.innerHTML = '<tr><td colspan="6" class="table-empty">No target domain computers match search criteria.</td></tr>';
      return;
    }

    computerTableBody.innerHTML = '';
    inventoryTableBody.innerHTML = '';

    filteredComputers.forEach(c => {
      // 1. Render main dashboard orchestrator table
      const trMain = document.createElement('tr');
      
      const lastBackupStr = c.lastBackupTime ? new Date(c.lastBackupTime).toLocaleString() : 'Never';
      const statusPillClass = c.isOnline ? 'online' : 'offline';
      const statusText = c.isOnline ? 'Online' : 'Offline';
      
      let backupStatusClass = 'failed';
      if (c.lastBackupStatus === 'Success') backupStatusClass = 'success';
      if (c.lastBackupStatus === 'In Progress') backupStatusClass = 'in-progress';
      if (c.lastBackupStatus === 'Never Backed Up') backupStatusClass = 'system';
      
      trMain.innerHTML = `
        <td style="font-weight: 600; font-family: var(--font-mono);">${c.computerName}</td>
        <td>${c.operatingSystem}</td>
        <td><span class="status-pill ${statusPillClass}">${statusText}</span></td>
        <td><span class="status-result ${backupStatusClass}">${c.lastBackupStatus}</span></td>
        <td style="font-family: var(--font-mono); font-size: 13px;">${lastBackupStr}</td>
        <td>
          <button class="btn btn-secondary btn-sm btn-run-backup" data-name="${c.computerName}" ${!c.isOnline ? 'disabled' : ''}>
            Trigger Backup
          </button>
        </td>
      `;
      computerTableBody.appendChild(trMain);

      // 2. Render inventory detailed tab
      const trInv = document.createElement('tr');
      const pingVal = c.isOnline ? `${c.responseTimeMs}ms` : '—';
      trInv.innerHTML = `
        <td style="font-weight: 600; font-family: var(--font-mono);">${c.computerName}</td>
        <td style="font-family: var(--font-mono);">${c.dnsHostName}</td>
        <td style="font-size: 12px; color: var(--text-muted);">${c.ou}</td>
        <td>${c.operatingSystem}</td>
        <td><span style="font-family: var(--font-mono);">${pingVal}</span></td>
        <td><span class="status-pill ${statusPillClass}">${statusText}</span></td>
      `;
      inventoryTableBody.appendChild(trInv);
    });

    // Add Trigger Backup Buttons event listeners
    document.querySelectorAll('.btn-run-backup').forEach(button => {
      button.addEventListener('click', () => {
        const targetName = button.getAttribute('data-name');
        openBackupModal(targetName);
      });
    });
  }

  searchComputersInput.addEventListener('input', () => renderComputerTables(localComputerCache));

  // Sync Domain Directory Actions
  const btnDiscoverAd = document.getElementById('btn-discover-ad');
  btnDiscoverAd.addEventListener('click', async () => {
    btnDiscoverAd.disabled = true;
    btnDiscoverAd.innerText = 'Synchronizing...';
    try {
      const response = await fetch('/api/discover', { method: 'POST' });
      const result = await response.json();
      if (result.success) {
        appendTerminalLine('SYSTEM', 'SUCCESS', `Discovered and pinged AD hosts: ${result.count} entries loaded.`);
      } else {
        appendTerminalLine('SYSTEM', 'ERROR', `Sync error: ${result.error}`);
      }
    } catch (err) {
      appendTerminalLine('SYSTEM', 'ERROR', `HTTP Request failed: ${err.message}`);
    } finally {
      btnDiscoverAd.disabled = false;
      btnDiscoverAd.innerHTML = `<svg viewBox="0 0 24 24" style="width:16px;height:16px;fill:currentColor;"><path d="M12 6v6h6v2h-6v6h-2v-6H4v-2h6V6h2z"/></svg> Sync Active Directory`;
    }
  });

  // Modal Control Box
  const backupModal = document.getElementById('backup-modal');
  const btnCloseModal = document.getElementById('btn-close-modal');
  const btnCancelBackup = document.getElementById('btn-cancel-backup');
  const modalComputerName = document.getElementById('modal-computer-name');
  const modalHiddenComputer = document.getElementById('modal-hidden-computer');
  const formTriggerBackup = document.getElementById('form-trigger-backup');
  const modalVcPassword = document.getElementById('modal-vc-password');

  function openBackupModal(computerName) {
    modalComputerName.innerText = computerName;
    modalHiddenComputer.value = computerName;
    modalVcPassword.value = '';
    backupModal.classList.add('active');
  }

  function closeModal() {
    backupModal.classList.remove('active');
  }

  btnCloseModal.addEventListener('click', closeModal);
  btnCancelBackup.addEventListener('click', closeModal);

  formTriggerBackup.addEventListener('submit', async (e) => {
    e.preventDefault();
    const computerName = modalHiddenComputer.value;
    const backupType = document.querySelector('input[name="backupType"]:checked').value;
    const password = modalVcPassword.value;

    closeModal();
    appendTerminalLine(computerName, 'INFO', `Requesting manual ${backupType} Backup Remote Session...`);

    try {
      const response = await fetch('/api/backups/trigger', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ computerName, backupType, password })
      });
      const data = await response.json();
      if (data.error) {
        appendTerminalLine(computerName, 'ERROR', `Activation failed: ${data.error}`);
      } else {
        appendTerminalLine(computerName, 'INFO', `Job remote sequence dispatched successfully.`);
      }
    } catch (err) {
      appendTerminalLine(computerName, 'ERROR', `Trigger API HTTP error: ${err.message}`);
    }
  });

  // Config tab form actions
  const formVeraCrypt = document.getElementById('form-veracrypt');
  const formAD = document.getElementById('form-ad');

  // Load config data
  async function loadConfig() {
    try {
      const response = await fetch('/api/config');
      const config = await response.json();
      
      // Update form values
      document.getElementById('input-vc-container').value = config.veraCryptContainer;
      document.getElementById('input-mount-letter').value = config.mountLetter;
      document.getElementById('input-search-ou').value = config.searchOU || '';
      document.getElementById('input-ad-group').value = config.adGroup;
      document.getElementById('check-schedule-active').checked = config.scheduleActive;
      document.getElementById('input-nightly-cron').value = config.nightlyTime;
      document.getElementById('input-weekly-cron').value = config.weeklyTime;

      // Update KPI container card
      const kpiVolume = document.getElementById('kpi-vc-volume');
      kpiVolume.innerText = `Vault (${config.mountLetter}:)`;
      kpiVolume.style.color = 'var(--color-amber)';
    } catch (err) {
      console.error('Failed to load system config details:', err);
    }
  }

  loadConfig();

  formVeraCrypt.addEventListener('submit', async (e) => {
    e.preventDefault();
    const veraCryptContainer = document.getElementById('input-vc-container').value;
    const mountLetter = document.getElementById('input-mount-letter').value.toUpperCase();
    
    try {
      const response = await fetch('/api/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ veraCryptContainer, mountLetter })
      });
      if (response.ok) {
        appendTerminalLine('SYSTEM', 'SUCCESS', 'VeraCrypt disk configuration settings updated successfully.');
        loadConfig();
      } else {
        const data = await response.json();
        alert(`Config Error: ${data.error}`);
      }
    } catch (err) {
      appendTerminalLine('SYSTEM', 'ERROR', `Failed to update VeraCrypt config: ${err.message}`);
    }
  });

  formAD.addEventListener('submit', async (e) => {
    e.preventDefault();
    const searchOU = document.getElementById('input-search-ou').value;
    const adGroup = document.getElementById('input-ad-group').value;
    const scheduleActive = document.getElementById('check-schedule-active').checked;
    const nightlyTime = document.getElementById('input-nightly-cron').value;
    const weeklyTime = document.getElementById('input-weekly-cron').value;

    try {
      const response = await fetch('/api/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ searchOU, adGroup, scheduleActive, nightlyTime, weeklyTime })
      });
      if (response.ok) {
        appendTerminalLine('SYSTEM', 'SUCCESS', 'Active Directory and scheduler settings updated successfully.');
        loadConfig();
      } else {
        const data = await response.json();
        alert(`Config Error: ${data.error}`);
      }
    } catch (err) {
      appendTerminalLine('SYSTEM', 'ERROR', `Failed to update Active Directory config: ${err.message}`);
    }
  });

  // Export Logs Handler
  const btnExportLogs = document.getElementById('btn-export-logs');
  btnExportLogs.addEventListener('click', () => {
    if (activeLogsRegistry.length === 0) {
      alert('No logs recorded to export.');
      return;
    }
    const dataStr = "data:text/json;charset=utf-8," + encodeURIComponent(JSON.stringify(activeLogsRegistry, null, 2));
    const downloadAnchor = document.createElement('a');
    downloadAnchor.setAttribute("href",     dataStr);
    downloadAnchor.setAttribute("download", `ad_backup_logs_${new Date().toISOString().slice(0,10)}.json`);
    document.body.appendChild(downloadAnchor);
    downloadAnchor.click();
    downloadAnchor.remove();
  });
});
