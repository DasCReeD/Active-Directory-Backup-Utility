const express = require('express');
const http = require('http');
const WebSocket = require('ws');
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const cron = require('node-cron');

const app = express();
const server = http.createServer(app);
const wss = new WebSocket.Server({ server });

const PORT = process.env.PORT || 3000;
const DATA_DIR = path.join(__dirname, 'config');
const CONFIG_FILE = path.join(DATA_DIR, 'app_config.json');
const HISTORY_FILE = path.join(DATA_DIR, 'backup_history.json');

// Ensure config directories exist
if (!fs.existsSync(DATA_DIR)) {
  fs.mkdirSync(DATA_DIR, { recursive: true });
}

// Default Configuration State (Immutable Pattern defaults)
const DEFAULT_CONFIG = {
  veraCryptContainer: 'C:\\BackupVault.hc',
  mountLetter: 'V',
  searchOU: '',
  adGroup: 'Backup-Targets',
  scheduleActive: true,
  nightlyTime: '0 1 * * *',  // 1:00 AM every night (Incremental)
  weeklyTime: '0 0 * * 0',   // 12:00 AM Sunday (Full)
  domainAdminContext: true
};

// Default History State
const DEFAULT_HISTORY = {
  computers: []
};

// Helper: Safely read configuration
function readConfig() {
  try {
    if (!fs.existsSync(CONFIG_FILE)) {
      fs.writeFileSync(CONFIG_FILE, JSON.stringify(DEFAULT_CONFIG, null, 2));
      return DEFAULT_CONFIG;
    }
    const data = fs.readFileSync(CONFIG_FILE, 'utf8');
    return { ...DEFAULT_CONFIG, ...JSON.parse(data) };
  } catch (error) {
    console.error('Failed to read config file, falling back to defaults:', error);
    return DEFAULT_CONFIG;
  }
}

// Helper: Safely write configuration (Immutable-friendly)
function writeConfig(newConfig) {
  try {
    const configToWrite = { ...readConfig(), ...newConfig };
    fs.writeFileSync(CONFIG_FILE, JSON.stringify(configToWrite, null, 2));
    setupScheduler(); // Re-initialize scheduler on config change
    return configToWrite;
  } catch (error) {
    console.error('Failed to save config:', error);
    throw new Error('Save configuration failed');
  }
}

// Helper: Safely read backup history
function readHistory() {
  try {
    if (!fs.existsSync(HISTORY_FILE)) {
      fs.writeFileSync(HISTORY_FILE, JSON.stringify(DEFAULT_HISTORY, null, 2));
      return DEFAULT_HISTORY;
    }
    const data = fs.readFileSync(HISTORY_FILE, 'utf8');
    return { ...DEFAULT_HISTORY, ...JSON.parse(data) };
  } catch (error) {
    console.error('Failed to read backup history:', error);
    return DEFAULT_HISTORY;
  }
}

// Helper: Safely write history (Immutable update patterns)
function writeHistory(updaterFn) {
  try {
    const current = readHistory();
    const updated = updaterFn(current);
    fs.writeFileSync(HISTORY_FILE, JSON.stringify(updated, null, 2));
    return updated;
  } catch (error) {
    console.error('Failed to write backup history:', error);
    throw new Error('Update backup history failed');
  }
}

// Middleware
app.use(express.json());
app.use(express.static(path.join(__dirname, 'public')));

// Active backup task registry to prevent concurrent runs on the same machine
const activeBackupProcesses = new Map();

// WebSocket: Broadcast helper for real-time console updates
function broadcastLog(computer, level, message, timestamp = new Date().toISOString()) {
  const payload = JSON.stringify({
    type: 'log',
    data: { computer, level, message, timestamp }
  });
  wss.clients.forEach(client => {
    if (client.readyState === WebSocket.OPEN) {
      client.send(payload);
    }
  });
}

// WebSocket: Broadcast list update to dashboard
function broadcastListUpdate() {
  const history = readHistory();
  const payload = JSON.stringify({
    type: 'list_update',
    data: history.computers
  });
  wss.clients.forEach(client => {
    if (client.readyState === WebSocket.OPEN) {
      client.send(payload);
    }
  });
}

// REST APIs
// Get Config
app.get('/api/config', (req, res) => {
  try {
    const config = readConfig();
    res.json(config);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// Update Config
app.post('/api/config', (req, res) => {
  try {
    // Basic boundary validation
    const { mountLetter, adGroup, veraCryptContainer } = req.body;
    if (mountLetter && !/^[A-Z]$/i.test(mountLetter)) {
      return res.status(400).json({ error: 'Mount letter must be a single alphabetical character.' });
    }
    
    const updated = writeConfig(req.body);
    res.json(updated);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// Get Computers List
app.get('/api/computers', (req, res) => {
  try {
    const history = readHistory();
    res.json(history.computers);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// Run Active Directory Discovery
app.post('/api/discover', async (req, res) => {
  try {
    const config = readConfig();
    broadcastLog('SYSTEM', 'INFO', 'Starting AD Computer Discovery process...');
    
    // Spawn PowerShell AD Discovery Script
    const psScript = path.join(__dirname, 'backend', 'powershell', 'Discover-DomainComputers.ps1');
    const args = [
      '-File', psScript,
      '-SearchOU', config.searchOU,
      '-GroupName', config.adGroup,
      '-PingCheck', '$true'
    ];
    
    const ps = spawn('powershell.exe', args);
    let outputData = '';
    let errorData = '';

    ps.stdout.on('data', (data) => {
      outputData += data.toString();
    });

    ps.stderr.on('data', (data) => {
      errorData += data.toString();
    });

    ps.on('close', (code) => {
      if (code !== 0) {
        broadcastLog('SYSTEM', 'ERROR', `AD Discovery failed: ${errorData}`);
        return res.status(500).json({ error: 'AD query execution failure', details: errorData });
      }
      
      try {
        const discovered = JSON.parse(outputData.trim());
        const discoveredList = Array.isArray(discovered) ? discovered : [discovered];
        
        // Merge with existing history (preserving past backup details using immutable update)
        writeHistory(current => {
          const merged = discoveredList.map(discItem => {
            const existing = current.computers.find(c => c.computerName === discItem.computerName);
            if (existing) {
              return {
                ...discItem,
                lastBackupStatus: existing.lastBackupStatus,
                lastBackupTime: existing.lastBackupTime
              };
            }
            return discItem;
          });
          return { ...current, computers: merged };
        });
        
        broadcastLog('SYSTEM', 'SUCCESS', `AD Discovery completed. Found ${discoveredList.length} computer(s).`);
        broadcastListUpdate();
        res.json({ success: true, count: discoveredList.length });
      } catch (parseErr) {
        broadcastLog('SYSTEM', 'ERROR', `Failed parsing discovery output: ${outputData}`);
        res.status(500).json({ error: 'Failed to parse AD search output', details: outputData });
      }
    });

  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// Trigger Backup Manually
app.post('/api/backups/trigger', (req, res) => {
  const { computerName, backupType, password } = req.body;
  if (!computerName || !backupType) {
    return res.status(400).json({ error: 'computerName and backupType are required.' });
  }

  if (activeBackupProcesses.has(computerName)) {
    return res.status(429).json({ error: `A backup is already running for machine: ${computerName}` });
  }

  const config = readConfig();
  const psScript = path.join(__dirname, 'backend', 'powershell', 'Backup-Orchestrator.ps1');
  
  const args = [
    '-File', psScript,
    '-ComputerName', computerName,
    '-BackupType', backupType,
    '-VeraCryptLetter', config.mountLetter,
    '-ContainerPath', config.veraCryptContainer,
    '-Password', password || '' // Passed securely for mounting
  ];

  broadcastLog(computerName, 'INFO', `Initializing administrative WinRM connection...`);

  // Update machine status in history to "In Progress"
  writeHistory(current => {
    const updatedComputers = current.computers.map(c => 
      c.computerName === computerName 
        ? { ...c, lastBackupStatus: 'In Progress', lastBackupTime: new Date().toISOString() } 
        : c
    );
    return { ...current, computers: updatedComputers };
  });
  broadcastListUpdate();

  const ps = spawn('powershell.exe', args);
  activeBackupProcesses.set(computerName, ps);

  ps.stdout.on('data', (data) => {
    const rawLine = data.toString().trim();
    rawLine.split('\n').forEach(line => {
      if (!line) return;
      try {
        // Attempt parsing structured JSON log from the PowerShell output
        const logObj = JSON.parse(line);
        broadcastLog(logObj.computer, logObj.level, logObj.message, logObj.timestamp);
      } catch {
        // Fallback for simple outputs
        broadcastLog(computerName, 'INFO', line);
      }
    });
  });

  ps.stderr.on('data', (data) => {
    broadcastLog(computerName, 'ERROR', data.toString().trim());
  });

  ps.on('close', (code) => {
    activeBackupProcesses.delete(computerName);
    const success = (code === 0);
    const status = success ? 'Success' : 'Failed';
    
    broadcastLog(
      computerName,
      success ? 'SUCCESS' : 'ERROR',
      `Orchestrator shutdown. Backup ${status.toUpperCase()} (Exit code ${code})`
    );

    // Update historical backup records
    writeHistory(current => {
      const updatedComputers = current.computers.map(c => 
        c.computerName === computerName 
          ? { ...c, lastBackupStatus: status, lastBackupTime: new Date().toISOString() } 
          : c
      );
      return { ...current, computers: updatedComputers };
    });
    broadcastListUpdate();
  });

  res.json({ message: `Backup initiated for ${computerName}`, status: 'In Progress' });
});

// WebSocket Handler
wss.on('connection', (ws) => {
  ws.send(JSON.stringify({ type: 'sys', message: 'Connected to Active Directory Backup WebSocket' }));
  
  // Stream initial computer list
  const history = readHistory();
  ws.send(JSON.stringify({ type: 'list_update', data: history.computers }));
});

// Scheduler Configuration
let nightlyTask = null;
let weeklyTask = null;

function setupScheduler() {
  const config = readConfig();
  
  if (nightlyTask) nightlyTask.stop();
  if (weeklyTask) weeklyTask.stop();

  if (!config.scheduleActive) {
    console.log('Orchestration backup scheduler is currently disabled.');
    return;
  }

  // Cron Scheduler for Nightly Incrementals
  nightlyTask = cron.schedule(config.nightlyTime, async () => {
    console.log('Initiating scheduled Nightly Incremental Backup for all targets...');
    await triggerGlobalBackup('Incremental');
  });

  // Cron Scheduler for Weekly Fulls
  weeklyTask = cron.schedule(config.weeklyTime, async () => {
    console.log('Initiating scheduled Weekly Full Backup for all targets...');
    await triggerGlobalBackup('Full');
  });

  console.log(`Scheduler active. Nightly Incremental: [${config.nightlyTime}], Weekly Full: [${config.weeklyTime}]`);
}

async function triggerGlobalBackup(backupType) {
  const history = readHistory();
  const onlineComputers = history.computers.filter(c => c.isOnline);
  
  broadcastLog('SYSTEM', 'INFO', `Scheduled task started. Triggering ${backupType} backups for ${onlineComputers.length} online machines.`);
  
  for (const computer of onlineComputers) {
    try {
      // Trigger a manual-like backup endpoint call programmatically
      // (Utilizes the active backup triggers internally, sequential triggers to avoid DC disk bottlenecking)
      await new Promise((resolve) => {
        const config = readConfig();
        const psScript = path.join(__dirname, 'backend', 'powershell', 'Backup-Orchestrator.ps1');
        const args = [
          '-File', psScript,
          '-ComputerName', computer.computerName,
          '-BackupType', backupType,
          '-VeraCryptLetter', config.mountLetter
        ];
        
        broadcastLog(computer.computerName, 'INFO', `Scheduled ${backupType} Backup Starting...`);
        
        writeHistory(current => {
          const updatedComputers = current.computers.map(c => 
            c.computerName === computer.computerName 
              ? { ...c, lastBackupStatus: 'In Progress', lastBackupTime: new Date().toISOString() } 
              : c
          );
          return { ...current, computers: updatedComputers };
        });
        broadcastListUpdate();

        const ps = spawn('powershell.exe', args);
        activeBackupProcesses.set(computer.computerName, ps);

        ps.stdout.on('data', (data) => {
          try {
            const logObj = JSON.parse(data.toString().trim());
            broadcastLog(logObj.computer, logObj.level, logObj.message, logObj.timestamp);
          } catch {
            broadcastLog(computer.computerName, 'INFO', data.toString().trim());
          }
        });

        ps.stderr.on('data', (data) => {
          broadcastLog(computer.computerName, 'ERROR', data.toString().trim());
        });

        ps.on('close', (code) => {
          activeBackupProcesses.delete(computer.computerName);
          const success = (code === 0);
          const status = success ? 'Success' : 'Failed';
          
          writeHistory(current => {
            const updatedComputers = current.computers.map(c => 
              c.computerName === computer.computerName 
                ? { ...c, lastBackupStatus: status, lastBackupTime: new Date().toISOString() } 
                : c
            );
            return { ...current, computers: updatedComputers };
          });
          broadcastListUpdate();
          resolve(); // Resolve promise to move to the next computer sequence
        });
      });
    } catch (e) {
      broadcastLog(computer.computerName, 'ERROR', `Scheduled run hit error: ${e.message}`);
    }
  }
}

// Boot setup
setupScheduler();

server.listen(PORT, () => {
  console.log(`Active Directory Backup Dashboard Backend running at http://localhost:${PORT}`);
});
