# ADShield Architecture Documentation

## Overview

ADShield is an **agentless, encrypted network backup orchestrator** for Windows Active Directory environments. It runs as a native Windows Forms application (.NET 8) on a Domain Controller or backup server, and coordinates full system-image and incremental VSS backups across all domain computers — without installing any software on target machines.

---

## System Architecture

### High-Level Component Diagram

```mermaid
graph TB
    subgraph "Backup Server / Domain Controller"
        UI["🖥️ ADShield WinForms UI\n(MainForm.cs)"]
        BO["⚙️ BackupOrchestrator\n(Core)"]
        AD["🔍 AdDiscovery\n(LDAP)"]
        VC["🔐 VeraCryptManager\n(Encrypted Vault)"]
        VSS["📸 VssManager\n(WMI Shadow Copy)"]
        VHDX["💾 VhdxManager\n(VirtDisk API)"]
        SMB["📁 SmbShareManager\n(WMI Win32_Share)"]
        SCHED["⏰ SchedulerService\n(System.Threading.Timer)"]
        CFG["🗂️ AppConfig\n(JSON persistence)"]
        VAULT["🔒 VeraCrypt Vault\n(.hc container)"]
        VHDX_FILE["📦 disk.vhdx\n(per-machine virtual disk)"]
    end

    subgraph "Active Directory"
        LDAP["LDAP Directory"]
    end

    subgraph "Target Client Workstation"
        CLIENT["💻 Domain Computer\n(WMI endpoint)"]
        VSS_CLIENT["VSS Shadow Copy\n(on C:\\)"]
        ROBOCOPY["Robocopy\n(network push)"]
    end

    UI -->|triggers| BO
    UI -->|triggers| AD
    UI -->|reads/writes| CFG
    AD -->|LDAP query| LDAP
    BO -->|mount/dismount| VC
    BO -->|create/attach| VHDX
    BO -->|create/remove| SMB
    BO -->|trigger| VSS
    VC --> VAULT
    VHDX --> VHDX_FILE
    VAULT -.->|contains| VHDX_FILE
    SCHED -->|fires event| BO
    BO -->|WMI remote| CLIENT
    CLIENT --> VSS_CLIENT
    CLIENT --> ROBOCOPY
    ROBOCOPY -->|SMB push| SMB
```

---

### Backup Sequence Flow

```mermaid
sequenceDiagram
    participant UI as ADShield UI
    participant BO as BackupOrchestrator
    participant VC as VeraCryptManager
    participant VX as VhdxManager
    participant SMB as SmbShareManager
    participant VSS as VssManager
    participant CLI as Target Client (WMI)

    UI->>BO: RunAsync(computerName, backupType, password)

    BO->>VC: IsMounted(mountLetter)?
    alt Not Mounted
        BO->>VC: Mount(settings, password)
    end

    BO->>CLI: Ping (ICMP check)
    BO->>VSS: TestWmiConnectivity(computerName)

    BO->>VX: VhdxExists(vhdxPath)?
    alt New VHDX
        BO->>CLI: GetRemoteDiskUsage() via WMI
        BO->>VX: CreateVhdx(path, sizeBytes)
    end

    BO->>BO: RunLocalDiskpart(attach vdisk)
    BO->>BO: RunLocalDiskpart(init partition + format NTFS)

    Note over BO: Self-heal check: verify B:\ is writable
    alt Drive not writable
        BO->>BO: RunLocalDiskpart(clean + reformat NTFS)
    end

    BO->>SMB: CreateShare(B:\, computerName)
    BO->>VSS: CreateRemoteShadowCopy(computerName, C:\)
    BO->>BO: GetShadowDevicePath(computerName, shadowId)
    BO->>CLI: RunRemoteDataCopy (mklink + robocopy push)
    CLI-->>SMB: robocopy data → \\server\backup_PC$

    BO->>VSS: DeleteShadowCopy(shadowId)
    BO->>SMB: RemoveShare(computerName)
    BO->>BO: RunLocalDiskpart(detach vdisk)
    BO->>BO: AppConfig.UpdateBackupResult(computerName, "Success")
    BO-->>UI: Progress complete
```

---

### Component Dependency Map

```mermaid
graph LR
    subgraph "Presentation Layer"
        MF[MainForm.cs]
        MFE[MainForm.Events.cs]
        BTF[BackupTriggerForm.cs]
        TH[Theme.cs]
    end

    subgraph "Core Services"
        BO[BackupOrchestrator]
        AD[AdDiscovery]
        VC[VeraCryptManager]
        VSS[VssManager]
        VX[VhdxManager]
        SMB[SmbShareManager]
        SC[SchedulerService]
        CFG[AppConfig]
        SHT[BackupSelfHealingTest]
    end

    subgraph "Models"
        AS[AppSettings]
        CE[ComputerEntry]
        SCI[ShadowCopyInfo]
    end

    subgraph "External APIs"
        WMI[WMI / Win32_*]
        LDAP2[System.DirectoryServices]
        VD[VirtDisk.dll P/Invoke]
        VCEXE[VeraCrypt.exe CLI]
        DP[diskpart.exe]
    end

    MF --> MFE
    MF --> BTF
    MF --> TH
    MF --> BO
    MF --> AD
    MF --> SC
    MF --> CFG

    BO --> VC
    BO --> VX
    BO --> SMB
    BO --> VSS
    BO --> CFG

    SHT --> BO
    SHT --> VX

    AD --> CFG
    AD --> CE

    SC --> AS

    CFG --> AS
    CFG --> CE

    VC --> AS
    VC --> VCEXE
    VC --> WMI

    VSS --> WMI
    VSS --> SCI

    VX --> VD

    SMB --> WMI

    AD --> LDAP2

    BO --> WMI
    BO --> DP
```

---

## Data Flow: Backup Storage Architecture

```mermaid
graph TD
    subgraph "Encrypted VeraCrypt Vault (e.g. V:\\)"
        VaultRoot["V:\\ (mount point)"]
        BackupsDir["V:\\backups\\"]
        PC1Dir["V:\\backups\\DESKTOP-001\\"]
        PC1VHDX["V:\\backups\\DESKTOP-001\\disk.vhdx"]
        PC2Dir["V:\\backups\\SERVER-02\\"]
        PC2VHDX["V:\\backups\\SERVER-02\\disk.vhdx"]
    end

    subgraph "VHDX Virtual Disk (mounted locally as B:\\)"
        VHDXPart["NTFS Partition (ADShield label)"]
        BackupData["VSS Backup Data"]
        IncrData["Incremental Versions"]
    end

    subgraph "SMB Exposure"
        HiddenShare["\\\\server\\backup_DESKTOP-001$"]
    end

    VaultRoot --> BackupsDir
    BackupsDir --> PC1Dir
    BackupsDir --> PC2Dir
    PC1Dir --> PC1VHDX
    PC2Dir --> PC2VHDX

    PC1VHDX -->|attached locally| VHDXPart
    VHDXPart --> BackupData
    VHDXPart --> IncrData

    VHDXPart -->|exposed via| HiddenShare
```

---

## Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| **UI Framework** | Windows Forms (.NET 8) | Desktop GUI for backup management |
| **Language** | C# 12 | Core application logic |
| **AD Integration** | `System.DirectoryServices` | LDAP queries against Active Directory |
| **Remote Management** | WMI (`System.Management`) | Agentless remote execution, VSS, process control |
| **Virtual Disk** | `VirtDisk.dll` (P/Invoke) | Native VHDX creation, attach, detach |
| **Disk Partitioning** | `diskpart.exe` (local shell) | NTFS format, partition assignment |
| **Encryption** | `VeraCrypt.exe` (CLI) | AES-256 container mount/dismount |
| **File Copy** | `robocopy.exe` (via WMI) | VSS shadow-to-share data transfer |
| **Persistence** | `Newtonsoft.Json` | Config and history serialization |
| **Scheduling** | `System.Threading.Timer` | Cron-style nightly/weekly triggers |
| **SMB Shares** | WMI `Win32_Share` | Dynamic hidden share creation/removal |

---

## Security Architecture

```mermaid
graph TB
    subgraph "Encryption Boundary"
        VC_Container["VeraCrypt .hc Container\n(AES-256 / SHA-512)"]
        VHDX_Inside["disk.vhdx files\n(inside encrypted vault)"]
    end

    subgraph "Network Access Control"
        Hidden["Hidden SMB Share\n(backup_PC$)"]
        ACL_Share["Share ACL:\n• BUILTIN\\Administrators\n• DOMAIN\\ComputerName$\n• Authenticated Users"]
        ACL_NTFS["NTFS ACL on B:\\:\n• Everyone (Full)\n• Authenticated Users\n• Domain Computers\n• DOMAIN\\ComputerName$"]
    end

    subgraph "Credential Management"
        PassMem["VeraCrypt passphrase\n(in-memory only, never stored)"]
        WMI_Auth["WMI Impersonation\n(domain admin context)"]
    end

    VC_Container -->|decrypted on mount| VHDX_Inside
    VHDX_Inside -->|exposed via| Hidden
    Hidden --> ACL_Share
    ACL_NTFS --> Hidden
    PassMem -->|used once| VC_Container
    WMI_Auth -->|remote auth| ACL_Share
```

**Security Principles:**
- The VeraCrypt passphrase is **never persisted** to disk; it is entered at runtime per-backup session
- Backup SMB shares are **dynamically created and destroyed** — they only exist during an active backup window
- Shares are **hidden** (trailing `$`) and restricted to the target machine's AD computer account
- All backup data at rest is inside an **AES-256 encrypted** VeraCrypt volume

---

## Scheduler Architecture

```mermaid
stateDiagram-v2
    [*] --> Stopped
    Stopped --> Active: ScheduleActive = true\nStart() called
    Active --> NightlyFired: Cron time reached\n(default: 01:00 daily)
    Active --> WeeklyFired: Cron time reached\n(default: Sunday 00:00)
    NightlyFired --> Active: Reschedule next day\nFire "Incremental"
    WeeklyFired --> Active: Reschedule next week\nFire "Full"
    Active --> Stopped: Stop() / Dispose()
```
