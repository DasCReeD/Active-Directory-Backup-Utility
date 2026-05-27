# ADShield Architecture Documentation

## Overview

ADShield is an **agentless, encrypted network backup orchestrator** for Windows Active Directory environments. It runs as a native Windows Forms application (.NET 8) on a Domain Controller or backup server, and coordinates full system-image and incremental VSS backups across all domain computers — without installing any software on target machines.

The backup engine uses a **server-pull architecture**: robocopy runs locally on the backup server and reads from each client's admin share (`\\CLIENT\C$`) through a VSS symlink. This eliminates the Kerberos double-hop authentication problem entirely.

---

## System Architecture

### High-Level Component Diagram

```mermaid
graph TB
    subgraph "Backup Server / Domain Controller"
        UI["🖥️ ADShield WinForms UI\n(MainForm.cs)"]
        BO["⚙️ BackupOrchestrator\n(Core — Server-Pull)"]
        AD["🔍 AdDiscovery\n(LDAP)"]
        VC["🔐 VeraCryptManager\n(Encrypted Vault)"]
        VSS["📸 VssManager\n(WMI Shadow Copy)"]
        VHDX["💾 VhdxManager\n(VirtDisk API)"]
        SCHED["⏰ SchedulerService\n(System.Threading.Timer)"]
        CFG["🗂️ AppConfig\n(JSON persistence)"]
        VAULT["🔒 VeraCrypt Vault\n(.hc container)"]
        VHDX_FILE["📦 disk.vhdx\n(per-machine virtual disk)"]
        ROBO["📋 robocopy.exe\n(runs LOCALLY on server)"]
    end

    subgraph "Active Directory"
        LDAP["LDAP Directory"]
    end

    subgraph "Target Client Workstation"
        CLIENT["💻 Domain Computer\n(WMI endpoint)"]
        VSS_CLIENT["VSS Shadow Copy\n(on C:\\)"]
        SYMLINK["Symlink\n(C:\\adshield_vss_link)"]
        ADMIN_SHARE["Admin Share\n(C$)"]
    end

    UI -->|triggers| BO
    UI -->|triggers| AD
    UI -->|reads/writes| CFG
    AD -->|LDAP query| LDAP
    BO -->|mount/dismount| VC
    BO -->|create/attach| VHDX
    BO -->|trigger| VSS
    VC --> VAULT
    VHDX --> VHDX_FILE
    VAULT -.->|contains| VHDX_FILE
    SCHED -->|fires event| BO
    BO -->|WMI: VSS + symlink| CLIENT
    CLIENT --> VSS_CLIENT
    VSS_CLIENT -->|device path| SYMLINK
    SYMLINK -->|exposed via| ADMIN_SHARE
    ROBO -->|reads from| ADMIN_SHARE
    ROBO -->|writes to| VHDX_FILE
```

---

### Backup Sequence Flow (Server-Pull)

```mermaid
sequenceDiagram
    participant UI as ADShield UI
    participant BO as BackupOrchestrator
    participant VC as VeraCryptManager
    participant VX as VhdxManager
    participant VSS as VssManager
    participant CLI as Target Client (WMI)
    participant ROBO as robocopy (LOCAL)

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

    BO->>CLI: EnableRemoteSymlinkEvaluation (WMI registry)
    BO->>VSS: CreateRemoteShadowCopy(computerName, C:\)
    BO->>BO: GetShadowDevicePath(computerName, shadowId)
    BO->>CLI: CreateRemoteVssSymlink (WMI Win32_Process)

    Note over ROBO: Server-pull: robocopy runs LOCALLY
    ROBO->>CLI: Read from \\CLIENT\C$\adshield_vss_link
    ROBO->>BO: Write to B:\ (local VHDX)

    BO->>CLI: RemoveRemoteVssSymlink (WMI)
    BO->>VSS: DeleteShadowCopy(shadowId)
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
        SC[SchedulerService]
        CFG[AppConfig]
        SHT[BackupSelfHealingTest]
        SPT[BackupServerPullTest]
    end

    subgraph "Models"
        AS[AppSettings]
        CE[ComputerEntry]
        SCI[ShadowCopyInfo]
    end

    subgraph "External APIs"
        WMI["WMI / Win32_*\nStdRegProv"]
        LDAP2[System.DirectoryServices]
        VD[VirtDisk.dll P/Invoke]
        VCEXE[VeraCrypt.exe CLI]
        DP[diskpart.exe]
        RC[robocopy.exe]
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
    BO --> VSS
    BO --> CFG
    BO --> RC

    SHT --> BO
    SHT --> VX

    SPT --> BO
    SPT --> VSS
    SPT --> VX

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

    subgraph "Server-Pull Data Path"
        AdminShare["\\\\CLIENT\\C$\\adshield_vss_link"]
        LocalRobocopy["robocopy.exe (runs on server)"]
    end

    VaultRoot --> BackupsDir
    BackupsDir --> PC1Dir
    BackupsDir --> PC2Dir
    PC1Dir --> PC1VHDX
    PC2Dir --> PC2VHDX

    PC1VHDX -->|attached locally| VHDXPart
    VHDXPart --> BackupData
    VHDXPart --> IncrData

    AdminShare -->|reads from| LocalRobocopy
    LocalRobocopy -->|writes to| VHDXPart
```

---

## Network Authentication Model

```mermaid
graph LR
    subgraph "Single-Hop Authentication"
        SERVER["Backup Server\n(david.snyder)"]
        CLIENT["Target Client\n(LOCALVM)"]

        SERVER -->|"WMI (hop 1): VSS, symlink, registry"| CLIENT
        SERVER -->|"robocopy /B (hop 1): \\CLIENT\\C$"| CLIENT
    end

    subgraph "Why This Works"
        NOTE["✅ Single network hop\n✅ Domain admin credentials\n✅ No impersonation delegation\n✅ No double-hop"]
    end
```

### Previous Architecture (Deprecated)

The old client-push model ran robocopy ON the client, which required a second authentication hop back to the server's SMB share. This caused `ERROR 5 (Access Denied)` due to the Kerberos double-hop restriction:

```
Server → Client (hop 1) → Server SMB share (hop 2) ❌ BLOCKED
```

The server-pull model eliminates this:

```
Server → Client admin share (hop 1 only) ✅
```

---

## Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| **UI Framework** | Windows Forms (.NET 8) | Desktop GUI for backup management |
| **Language** | C# 12 | Core application logic |
| **AD Integration** | `System.DirectoryServices` | LDAP queries against Active Directory |
| **Remote Management** | WMI (`System.Management`) | Agentless remote VSS, symlinks, registry |
| **Virtual Disk** | `VirtDisk.dll` (P/Invoke) | Native VHDX creation, attach, detach |
| **Disk Partitioning** | `diskpart.exe` (local shell) | NTFS format, partition assignment |
| **Encryption** | `VeraCrypt.exe` (CLI) | AES-256 container mount/dismount |
| **File Copy** | `robocopy.exe` (local, `/B` mode) | Server-pull data transfer with backup privilege |
| **Persistence** | `Newtonsoft.Json` | Config and history serialization |
| **Scheduling** | `System.Threading.Timer` | Cron-style nightly/weekly triggers |

---

## Security Architecture

```mermaid
graph TB
    subgraph "Encryption Boundary"
        VC_Container["VeraCrypt .hc Container\n(AES-256 / SHA-512)"]
        VHDX_Inside["disk.vhdx files\n(inside encrypted vault)"]
    end

    subgraph "Network Access"
        AdminShare["Admin Share (C$)\n(built-in, pre-existing)"]
        R2L["R2L Symlink Evaluation\n(enabled per-client via WMI)"]
    end

    subgraph "Credential Management"
        PassMem["VeraCrypt passphrase\n(in-memory only, never stored)"]
        DomainAuth["Domain Admin credentials\n(single-hop WMI + admin share)"]
    end

    VC_Container -->|decrypted on mount| VHDX_Inside
    DomainAuth -->|authenticates| AdminShare
    R2L -->|allows traversal of| AdminShare
    PassMem -->|used once| VC_Container
```

**Security Principles:**
- The VeraCrypt passphrase is **never persisted** to disk; it is entered at runtime per-backup session
- No dynamic SMB shares are created — the server reads from the **pre-existing admin share** (`C$`)
- R2L symlink evaluation is enabled via **WMI registry write** (`StdRegProv`), not manual configuration
- All backup data at rest is inside an **AES-256 encrypted** VeraCrypt volume
- Robocopy runs with `/B` (backup mode) using `SeBackupPrivilege` for ACL bypass on source files

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

---

## Test Suites

### BackupSelfHealingTest

Tests the VHDX lifecycle and self-healing format recovery:

| Test | What It Validates |
|------|-------------------|
| `TestVhdxCreateAndFormat` | VHDX creation via VirtDisk API + diskpart NTFS format |
| `TestSelfHealDetection` | Detects unwritable/RAW partitions and triggers reformat |
| `TestQuickFormatRecovery` | Full clean → GPT → NTFS recovery pipeline |

### BackupServerPullTest

Tests the server-pull backup architecture end-to-end:

| Test | What It Validates |
|------|-------------------|
| `TestR2LSymlinkEvaluation` | WMI registry write to enable R2L on remote client |
| `TestVssSymlinkAccess` | VSS shadow + symlink creation → server `Directory.GetDirectories()` via admin share |
| `TestEndToEndServerPull` | Full pipeline: VHDX → VSS → symlink → robocopy pull → verify copied data |
