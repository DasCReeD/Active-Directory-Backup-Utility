# AD Shield — Agentless Network Backup & Dashboard

**ADShield** is an enterprise-grade, agentless backup orchestrator for Windows Active Directory environments. It runs as a native Windows application (.NET 8 WinForms) on a Domain Controller or backup server, and coordinates encrypted VSS-based system backups across all domain computers — **zero software installed on target machines**.

---

## Key Features

- **100% Agentless** — Remote execution via WMI only; no client-side agents, scripts, or services installed
- **VSS Block-Level Backups** — Uses Windows Volume Shadow Copy for consistent, open-file safe backups
- **AES-256 Encrypted Storage** — All backup data lives inside a VeraCrypt encrypted container at rest
- **Per-Machine VHDX Isolation** — Each computer gets its own dynamically-sized virtual disk (`.vhdx`) inside the vault
- **LDAP AD Discovery** — Queries Active Directory directly via `System.DirectoryServices`; no AD PowerShell module needed
- **Automated Scheduling** — Built-in nightly incremental / weekly full cron-style scheduling
- **Self-Healing Storage** — Automatically detects and reformats uninitialized VHDX partitions mid-backup
- **Dark Enterprise UI** — Modern glassmorphism-inspired WinForms dashboard with real-time terminal log

---

## How It Works

```mermaid
sequenceDiagram
    participant UI as ADShield UI
    participant BO as Backup Engine
    participant VC as VeraCrypt Vault (V:\)
    participant VHDX as disk.vhdx (B:\)
    participant SMB as Hidden Share (\\server\backup_PC$)
    participant CLI as Target Client (WMI)

    UI->>BO: Trigger backup for DESKTOP-001
    BO->>VC: Mount encrypted vault
    BO->>VHDX: Create & attach 120 GB VHDX
    BO->>SMB: Create hidden SMB share → B:\
    BO->>CLI: Create VSS shadow copy (C:\) via WMI
    CLI->>SMB: robocopy VSS shadow → \\server\backup_PC$
    BO->>CLI: Delete VSS shadow
    BO->>SMB: Remove SMB share
    BO->>VHDX: Detach VHDX
    BO->>UI: "Backup sequence completed!"
```

The backup engine automatically sizes each VHDX to the remote machine's actual disk usage + 20% headroom, and self-heals any unformatted disk state.

---

## Project Structure

```
Active-Directory-Backup-Utility-main/
├── ADShield/                           # .NET 8 WinForms application
│   ├── Core/
│   │   ├── BackupOrchestrator.cs       # 8-step agentless backup pipeline
│   │   ├── AdDiscovery.cs              # LDAP AD computer discovery
│   │   ├── AppConfig.cs                # JSON config & history persistence
│   │   ├── VeraCryptManager.cs         # Encrypted vault mount/dismount
│   │   ├── VssManager.cs               # WMI VSS shadow copy management
│   │   ├── VhdxManager.cs              # Native VirtDisk.dll VHDX API
│   │   ├── SmbShareManager.cs          # WMI Win32_Share dynamic shares
│   │   ├── SchedulerService.cs         # Cron-style backup scheduler
│   │   └── BackupSelfHealingTest.cs    # VHDX regression test suite
│   ├── Forms/
│   │   ├── MainForm.cs                 # Application shell & 4-page nav
│   │   ├── MainForm.Events.cs          # UI event handlers
│   │   ├── BackupTriggerForm.cs        # Passphrase + backup type modal
│   │   └── Theme.cs                    # Design token system (colors, fonts)
│   ├── Models/
│   │   ├── AppSettings.cs              # Configuration model
│   │   └── ComputerEntry.cs            # AD computer + backup status model
│   └── ADShield.csproj
├── backend/
│   └── powershell/
│       ├── Backup-Orchestrator.ps1     # Legacy PowerShell orchestrator
│       ├── Discover-DomainComputers.ps1
│       └── Manage-VeraCrypt.ps1
├── docs/
│   ├── architecture.md                 # System diagrams & component maps
│   ├── code-reference.md               # Full API reference
│   ├── deployment-guide.md             # Setup & operations guide
│   └── winpe_recovery.md               # Bare-metal recovery procedure
├── public/                             # Legacy web dashboard (Node.js era)
├── server.js                           # Legacy Express server
└── README.md
```

---

## Quick Start

### Prerequisites

| Requirement | Details |
|-------------|---------|
| Windows Server 2019+ or Windows 10 21H2+ | Domain-joined |
| .NET 8 Desktop Runtime | [Download](https://dotnet.microsoft.com/download/dotnet/8) |
| VeraCrypt v1.26+ | [Download](https://www.veracrypt.fr) |
| Domain Administrator account | Required for WMI remote access |
| **Run as Administrator** | Required for diskpart, WMI impersonation |

### Setup

1. **Install** .NET 8 Desktop Runtime and VeraCrypt on the backup server
2. **Build or deploy** ADShield to the backup server
3. **Run** `ADShield.exe` as Administrator
4. **Create** the VeraCrypt encrypted vault in **System Config**
5. **Sync** Active Directory to discover computers
6. **Trigger** your first backup from the Dashboard

For full instructions, see 📖 **[docs/deployment-guide.md](docs/deployment-guide.md)**

---

## Documentation

| Document | Description |
|----------|-------------|
| [architecture.md](docs/architecture.md) | System diagrams, component maps, data flows, security model |
| [code-reference.md](docs/code-reference.md) | Complete class/method API reference |
| [deployment-guide.md](docs/deployment-guide.md) | Installation, configuration, operations, troubleshooting |
| [winpe_recovery.md](docs/winpe_recovery.md) | Bare-metal recovery with WinPE + VeraCrypt Portable |

---

## Technical Architecture

### Storage Architecture

```
VeraCrypt Vault (V:\)           — AES-256 encrypted container
└── backups\
    ├── DESKTOP-001\
    │   └── disk.vhdx           — 120 GB NTFS virtual disk (dynamically expanding)
    │       └── VSS backup data
    ├── LAPTOP-002\
    │   └── disk.vhdx
    └── SERVER-03\
        └── disk.vhdx
```

### Technology Stack

| Component | Technology |
|-----------|-----------|
| UI | Windows Forms (.NET 8) |
| AD Integration | `System.DirectoryServices` (LDAP) |
| Remote Execution | WMI `Win32_Process`, `Win32_ShadowCopy` |
| Virtual Disk | `VirtDisk.dll` (P/Invoke native API) |
| Encryption | VeraCrypt AES-256 / SHA-512 |
| File Copy | `robocopy.exe` (invoked remotely via WMI) |
| SMB Shares | WMI `Win32_Share` |
| Persistence | `Newtonsoft.Json` |

---

## Security Design

- **Passphrase never stored** — VeraCrypt passphrase is entered at runtime and held in memory only
- **Dynamic share isolation** — Hidden SMB shares (`backup_PC$`) are created only during backup and immediately removed
- **Per-machine ACLs** — Each share and VHDX root grants access only to the specific target machine's domain account (`DOMAIN\ComputerName$`)
- **Encrypted at rest** — All VHDX files reside inside the AES-256 VeraCrypt container; the container is locked when not actively backing up

---

## License

See [LICENSE](LICENSE) for details.
