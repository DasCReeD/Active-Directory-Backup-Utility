# ADShield — Deployment & Operations Guide

## Overview

This guide covers everything needed to deploy, configure, and operate **ADShield** in a Windows Active Directory environment — from initial prerequisites through first backup and troubleshooting.

---

## Prerequisites

### Backup Server Requirements

| Requirement | Minimum | Notes |
|-------------|---------|-------|
| **OS** | Windows Server 2019 / Windows 10 (21H2+) | Must be domain-joined |
| **Role** | Domain Administrator privileges | Required for WMI remote access |
| **.NET Runtime** | .NET 8.0 Desktop Runtime | [Download](https://dotnet.microsoft.com/download/dotnet/8) |
| **VeraCrypt** | v1.26+ | Must be installed at configured path |
| **Free Disk Space** | Sufficient for `.hc` container | Depends on total backup size across all machines |
| **Run As** | Administrator (elevated) | Required for diskpart, WMI impersonation, SMB share creation |

### Target Client Requirements

| Requirement | Notes |
|-------------|-------|
| Domain member | Computer account must be in AD |
| WMI enabled | Default on Windows; requires firewall exception |
| No agent required | ADShield is 100% agentless |

> **WMI Firewall Rule** — If clients have firewall restrictions, enable:
> ```
> netsh advfirewall firewall set rule group="Windows Management Instrumentation (WMI)" new enable=yes
> ```

---

## Installation

### 1. Install .NET 8 Desktop Runtime

Download and install from Microsoft:
```
https://dotnet.microsoft.com/en-us/download/dotnet/8.0
```
Select **".NET Desktop Runtime 8.x"** for Windows x64.

### 2. Install VeraCrypt

Download from `https://www.veracrypt.fr` and install. Note the installation path (default: `C:\Program Files\VeraCrypt\VeraCrypt.exe`).

### 3. Copy ADShield

Copy the `ADShield` folder (or publish output) to the backup server. Recommended location:
```
C:\Tools\ADShield\
```

### 4. Build from Source (Optional)

```powershell
cd C:\Dev\Active-Directory-Backup-Utility-main\ADShield
dotnet build -c Release

# Or publish as a single self-contained executable:
dotnet publish -r win-x64 -c Release /p:PublishSingleFile=true /p:SelfContained=true
```

### 5. Run as Administrator

Right-click `ADShield.exe` → **Run as Administrator**

> ADShield requires elevation for:
> - `diskpart.exe` (VHDX partitioning)
> - WMI impersonation for remote access
> - SMB share creation via `Win32_Share`

---

## Initial Setup

### Step 1: Create the VeraCrypt Encrypted Vault

Before the first backup, create the encrypted container that will hold all backup data.

1. Launch ADShield as Administrator
2. Navigate to **⚙ System Config**
3. Under **VeraCrypt Encryption Store**:
   - Set **VeraCrypt Executable Path** (e.g. `C:\Program Files\VeraCrypt\VeraCrypt.exe`)
   - Set **Encrypted Container File (.hc)** — the path where the vault will be created (e.g. `E:\BackupVault.hc`)
   - Set **New Container Size** (e.g. `500G` for 500 GB, `2T` for 2 TB)
   - Set **Mount Drive Letter** (e.g. `V`)
4. Click **🔒 Create Encrypted Volume**
5. Enter the passphrase when prompted

> 💡 **Size planning:** Assume 1–2x the sum of all target machines' C: drive usage. The VHDX files inside are dynamically expanding, so actual space grows gradually.

### Step 2: Configure Backup Storage

1. Under **Backup Storage Location**:
   - **Backup Root Folder**: subdirectory inside the mounted vault (e.g. `backups`)
   - **VHDX Size per Machine (GB)**: fallback size if remote disk query fails (default: `1024`)
2. Click **Save Storage Config**

> The final backup path will be: `V:\backups\<ComputerName>\disk.vhdx`

### Step 3: Configure AD Targeting

1. Under **AD Targeting & Automation**:
   - **Search OU**: Leave blank to target entire domain, or enter an OU DN (e.g. `OU=Workstations,DC=corp,DC=local`)
   - **AD Security Group Filter**: Enter a group name to limit backups to that group's members, or leave blank for all computers
2. Click **Save AD Settings**

### Step 4: Discover Domain Computers

Click **↻ Sync Active Directory** in the top header. ADShield will:
- Execute an LDAP query against your domain
- Ping each discovered computer
- Display results in the **Domain Clients** inventory

---

## Running Backups

### Manual Backup

1. Navigate to **▦ Dashboard** (or **⊞ Domain Clients**)
2. Find the target computer (verify it shows **Online** status)
3. Click **Backup** in the Action column
4. In the **Backup Trigger** dialog:
   - Enter the **VeraCrypt vault passphrase**
   - Select backup type: **Incremental** or **Full**
5. Click **Start Remote Session**
6. Monitor progress in the **Operations Terminal** pane (right side of Dashboard)

### Automated Scheduling

1. Navigate to **⚙ System Config** → **AD Targeting & Automation**
2. Check **Enable Automated Backup Schedule**
3. Configure cron expressions:
   - **Nightly Incremental** — default `0 1 * * *` (01:00 AM daily)
   - **Weekly Full** — default `0 0 * * 0` (Sunday midnight)
4. Click **Save AD Settings**

> ⚠️ **Note:** The VeraCrypt passphrase must be pre-configured in memory for scheduled backups to work. Ensure the vault is mounted before scheduled jobs fire, or ADShield will attempt to mount it automatically (requiring a stored credential mechanism in future versions).

### Running the Self-Healing Diagnostic

Click **⚙ Run Self-Test** (header button) to execute the regression test:
- Creates a 10 MB VHDX in `V:\backups\`
- Validates that unformatted disks are correctly detected and auto-formatted
- Verifies write access after recovery
- Cleans up all test artifacts

This test does **not** require a target client computer.

---

## Backup Data Lifecycle

```
Backup Initiated
    │
    ▼
VeraCrypt vault mounted (V:\)
    │
    ▼
Per-machine VHDX created (V:\backups\PC-NAME\disk.vhdx)
    │  (sized to remote C: usage + 20% headroom)
    │
    ▼
VHDX attached locally as B:\
    │  (NTFS formatted, self-heals if RAW)
    │
    ▼
Hidden SMB share created (\\server\backup_PC-NAME$) → B:\
    │
    ▼
VSS shadow copy triggered on target PC via WMI
    │
    ▼
Robocopy runs on target PC, pushing from VSS shadow → SMB share
    │  (2-hour timeout, excludes: System Volume Information, $Recycle.Bin)
    │
    ▼
VSS shadow copy deleted
SMB share removed
VHDX detached
    │
    ▼
history.json updated: LastBackupStatus = "Success"
```

---

## Configuration Reference

### `config.json`

Location: `%AppData%\ADShield\config.json`

```json
{
  "VeraCryptExePath": "C:\\Program Files\\VeraCrypt\\VeraCrypt.exe",
  "VeraCryptContainer": "E:\\BackupVault.hc",
  "MountLetter": "V",
  "BackupStorageRoot": "backups",
  "VhdxSizeGb": 1024,
  "SearchOU": "",
  "AdGroup": "",
  "ScheduleActive": false,
  "NightlyCron": "0 1 * * *",
  "WeeklyCron": "0 0 * * 0",
  "DomainAdminContext": true
}
```

### `history.json`

Location: `%AppData%\ADShield\history.json`

```json
[
  {
    "ComputerName": "DESKTOP-001",
    "DnsHostName": "DESKTOP-001.corp.local",
    "OU": "OU=Workstations,DC=corp,DC=local",
    "OperatingSystem": "Windows 11 Pro",
    "IsOnline": true,
    "PingMs": 4,
    "LastBackupStatus": "Success",
    "LastBackupTime": "2025-05-22T01:03:47"
  }
]
```

---

## Troubleshooting

### VeraCrypt Mount Fails

| Symptom | Cause | Fix |
|---------|-------|-----|
| `VeraCrypt executable not found` | Wrong `VeraCryptExePath` | Update path in System Config |
| `Mount did not succeed` | Wrong passphrase | Re-enter the correct passphrase |
| `Mount did not succeed` | Container file missing | Verify the `.hc` file path is correct |
| Container on mapped drive fails | Elevated process can't see mapped drives | Use the UNC path directly (`\\server\share\vault.hc`) — ADShield auto-resolves this |

### Target Computer Unreachable

| Symptom | Cause | Fix |
|---------|-------|-----|
| `ICMP failed` | Computer offline or ICMP blocked | Verify ping from server; check Windows Firewall on client |
| `WMI not accessible` | WMI service or firewall | Enable WMI firewall rule on target; run `winrm quickconfig` |
| `Access denied` | Not running as Domain Admin | Run ADShield.exe as Administrator with a Domain Admin account |

### VHDX / Disk Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Failed to mount, format and write to local drive B:\` | diskpart permission issue | Ensure ADShield runs as Administrator |
| Drive `B:` already in use | Previous backup didn't cleanup | Open Disk Management, detach the disk manually |
| `HRESULT: 0x80070005` from VirtDisk | Access denied | Run as Administrator |

### Robocopy Errors

| Error Code | Meaning | Action |
|------------|---------|--------|
| `ERROR 53` | Network path not found | SMB share or path not accessible; check share creation logs |
| `ERROR 5` | Access denied | Check NTFS ACLs on the VHDX drive root |
| `ERROR 3` | Path not found | VSS shadow symlink may have failed; check earlier log steps |

> All robocopy output is captured in `C:\Windows\Temp\adshield_robocopy.log` on the **target client** machine and displayed in the Operations Terminal.

### Log Levels Reference

| Tag | Meaning |
|-----|---------|
| `[INFO]` | Normal operation step |
| `[SUCCESS]` | Step completed successfully |
| `[WARN]` | Non-fatal issue — backup continues |
| `[ERROR]` | Fatal failure — backup aborted |
| `[TEST]` | Output from Self-Healing Diagnostic |

---

## Security Hardening Recommendations

1. **Restrict ADShield access** — Only domain administrators should run ADShield. Consider using a dedicated service account with minimal required rights.

2. **VeraCrypt passphrase** — Use a strong passphrase (20+ characters). Never store it in any file. For scheduled backups, consider a hardware token.

3. **Container location** — Store the `.hc` container on a dedicated backup volume, separate from system drives. Consider RAID for the container's host volume.

4. **Network isolation** — The backup server should be on a management VLAN with controlled inbound access from target computers only.

5. **Audit logs** — Export the ADShield operation logs (CSV export in Logs page) periodically and retain for compliance.

6. **Physical security** — The backup server holding the VeraCrypt container should have physical access controls. An unmounted vault is useless without the passphrase.

---

## Bare-Metal Recovery

For full system restore procedures using WinPE bootable media, see:

📖 **[docs/winpe_recovery.md](winpe_recovery.md)**

This covers building a WinPE ISO with VeraCrypt Portable, mounting the vault on bare hardware, and restoring from the VHDX.
