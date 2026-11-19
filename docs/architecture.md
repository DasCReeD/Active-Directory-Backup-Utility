# ADShield Architecture Documentation

## Overview

ADShield is a **100% agentless, encrypted network backup orchestrator** for Windows Active Directory environments. It compiles to a single .NET 8 executable (`ADShield.exe`) that runs exclusively on the backup server as a Windows Forms GUI or CLI automation tool, coordinating VSS shadow copy creation via WMI and server-pull `robocopy.exe` transfers over SMB admin shares (`C$`) into per-machine VHDX disks inside an AES-256 VeraCrypt vault.

> **AD & GPO Operational Guide:** See **[AGENTLESS_ROADMAP.md](AGENTLESS_ROADMAP.md)** for the Group Policy (GPO) firewall rules, R2L symlink evaluation policy, and least-privilege AD service account / gMSA setup.

---

## System Architecture

### High-Level Component Diagram

```mermaid
graph TB
    subgraph "Backup Server / Domain Controller"
        PRG["Program.cs\n(GUI / CLI)"]
        UI["ADShield WinForms UI\n(MainForm.cs + MainForm.Events.cs)"]
        BO["BackupOrchestrator\n(Agentless Server-Pull)"]
        AD["AdDiscovery\n(LDAP + Ping)"]
        VC["VeraCryptManager\n(Encrypted Vault)"]
        VSS_STORE["VaultSecretStore\n(DPAPI crypt32.dll)"]
        VHDX["VhdxManager + diskpart\n(VirtDisk API, Dynamic Z..M)"]
        VSS["VssManager\n(WMI Win32_ShadowCopy)"]
        PR["ProcessRunner\n(Async Process Tree)"]
        SCHED["SchedulerService\n(Re-entrancy Guarded)"]
        CFG["AppConfig\n(JSON persistence)"]
        VAULT["VeraCrypt Vault\n(.hc container)"]
        VHDX_FILE["disk.vhdx\n(per-machine virtual disk)"]
        ROBO["robocopy.exe\n(runs LOCALLY on server)"]
    end

    subgraph "Active Directory"
        LDAP["LDAP Directory"]
    end

    subgraph "Target Client Workstation (Zero Agent)"
        CLIENT["Domain Computer\n(WMI / DCOM)"]
        VSS_CLIENT["VSS Shadow Copy\n(on C:\\)"]
        SYMLINK["Temp Symlink\n(C:\\_adshield_vss_<id>)"]
        ADMIN_SHARE["Admin Share\n(C$)"]
    end

    PRG --> UI
    PRG --> BO
    UI -->|SemaphoreSlim + triggers| BO
    UI -->|triggers| AD
    UI -->|reads/writes| CFG
    UI -->|unattended passphrase| VSS_STORE
    AD -->|LDAP query| LDAP
    BO -->|mount/dismount| VC
    BO -->|create/attach/detach| VHDX
    BO -->|WMI probe, disk space, VSS| VSS
    VC --> VAULT
    VHDX --> VHDX_FILE
    VAULT -.->|contains| VHDX_FILE
    SCHED -->|fires event| UI
    BO -->|WMI Win32_Process: mklink/rmdir| CLIENT
    VSS -->|WMI: Win32_ShadowCopy| CLIENT
    CLIENT --> VSS_CLIENT
    VSS_CLIENT -->|DeviceObject| SYMLINK
    SYMLINK -->|exposed via| ADMIN_SHARE
    ROBO -->|reads from| ADMIN_SHARE
    ROBO -->|writes to| VHDX_FILE
    BO -->|runs via ProcessRunner| ROBO
```

---

### Backup Sequence Flow (100% Agentless Server-Pull)

```mermaid
sequenceDiagram
    autonumber
    participant Caller as MainForm / CLI
    participant BO as BackupOrchestrator (Server)
    participant VC as VeraCryptManager (V:\)
    participant VX as VhdxManager + diskpart (Z..M)
    participant VSS as VssManager (WMI)
    participant CLI as Target Client (WMI + C$)
    participant ROBO as robocopy.exe (Local Server)

    Caller->>BO: RunAsync(computerName, backupType, password, progress, ct)
    BO->>VC: IsMounted(V:) / MountAsync()
    BO->>CLI: Ping.SendPingAsync (1500ms)
    BO->>VSS: TestWmiConnectivity(computerName, out error)

    Note over BO,VX: Hardened ACLs (Administrators + SYSTEM) & Dynamic Sizing (UsedGb * 1.2)
    BO->>VSS: QueryRemoteDiskSpace(computerName, "C:")
    BO->>VX: CreateVhdx(V:\backups\{computer}\disk.vhdx) [if new]
    BO->>VX: Allocate free drive letter (Z..M) + AttachVhdx + diskpart init/assign
    BO->>VX: Self-heal write probe (clean + GPT + quick NTFS if RAW/unwritable)

    BO->>VSS: CreateRemoteShadowCopy(computerName, "C:\\")
    BO->>VSS: GetShadowCopyDeviceObject(computerName, shadowId)
    BO->>CLI: WMI Win32_Process.Create (cmd.exe /c mklink /d C:\_adshield_vss_{id} DeviceObject)

    BO->>ROBO: robocopy \\{computer}\C$\_adshield_vss_{id} -> {drive}:\{backupType}\
    BO->>BO: AppConfig.UpdateBackupResult(computerName, "Success")

    Note over BO,CLI: finally block (always runs on success, failure, or cancellation)
    BO->>CLI: WMI Win32_Process.Create (cmd.exe /c if exist ... rmdir ...)
    BO->>VSS: DeleteRemoteShadowCopy(computerName, shadowId)
    BO->>VX: DetachVhdx (with diskpart fallback)
```

---

### Component Dependency Map

```mermaid
graph LR
    subgraph "Entry & Presentation"
        PRG[Program.cs]
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
        PR[ProcessRunner]
        SC[SchedulerService]
        CFG[AppConfig]
        VST[VaultSecretStore]
        VAL[BackupValidationSuite]
        SHD[SelfHealingDiagnostics]
    end

    subgraph "Models"
        AS[AppSettings]
        CE[ComputerEntry]
        SCI[ShadowCopyInfo]
    end

    subgraph "External APIs & Binaries"
        WMI["WMI / Win32_ShadowCopy\nWin32_Process / Win32_LogicalDisk"]
        LDAP2[System.DirectoryServices]
        VD[VirtDisk.dll P/Invoke]
        CR[crypt32.dll DPAPI]
        VCEXE[VeraCrypt.exe CLI]
        DP[diskpart.exe]
        RC[robocopy.exe]
    end

    PRG --> MF
    PRG --> BO
    PRG --> VC
    PRG --> AD
    PRG --> VAL
    PRG --> SHD

    MF --> MFE
    MF --> BTF
    MF --> TH
    MF --> SC
    MF --> CFG
    MFE --> BO
    MFE --> AD
    MFE --> VC
    MFE --> VST
    MFE --> VAL
    MFE --> SHD

    BO --> VC
    BO --> VX
    BO --> VSS
    BO --> CFG
    BO --> PR

    PR --> VCEXE
    PR --> DP
    PR --> RC

    SHD --> BO
    SHD --> VX
    VAL --> PR
    VST --> CR
    VST --> CFG

    AD --> CFG
    AD --> CE
    AD --> LDAP2

    SC --> AS
    CFG --> AS
    CFG --> CE

    VC --> WMI
    SMB --> WMI
    VSS --> WMI
    VSS --> SCI
    VX --> VD
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

    subgraph "VHDX Virtual Disk (dynamically mounted Z..M)"
        VHDXPart["NTFS Partition (ADShield label)"]
        BackupData["Incremental / Full Backup Data"]
    end

    subgraph "Server-Pull Data Path"
        AdminShare["\\\\CLIENT\\C$\\_adshield_vss_<id>"]
        LocalRobocopy["robocopy.exe (runs on server)"]
    end

    VaultRoot --> BackupsDir
    BackupsDir --> PC1Dir
    BackupsDir --> PC2Dir
    PC1Dir --> PC1VHDX
    PC2Dir --> PC2VHDX

    PC1VHDX -->|attached locally| VHDXPart
    VHDXPart --> BackupData

    AdminShare -->|reads from| LocalRobocopy
    LocalRobocopy -->|writes to| VHDXPart
```

---

## Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| **UI Framework** | Windows Forms (.NET 8) | Desktop GUI for backup management |
| **Language** | C# 12 | Core application logic and CLI |
| **AD Integration** | `System.DirectoryServices` | LDAP queries against Active Directory |
| **Remote Management** | WMI (`System.Management`) | Agentless `Win32_ShadowCopy`, `Win32_LogicalDisk`, `Win32_Process` |
| **Virtual Disk** | `VirtDisk.dll` (P/Invoke) | Native VHDX creation, attach, detach |
| **Disk Partitioning** | `diskpart.exe` (via `ProcessRunner`) | NTFS format, dynamic drive letter assignment (`Z`–`M`) |
| **Encryption** | `VeraCrypt.exe` (via `ProcessRunner`) | AES-256 container mount/dismount/create |
| **Secret Store** | `crypt32.dll` DPAPI (`VaultSecretStore`) | Optional encrypted unattended passphrase (`vault.dat`) |
| **File Copy** | `robocopy.exe` (local, `/B` mode) | Server-pull data transfer with backup privilege |
| **Persistence** | `Newtonsoft.Json` | Config and history serialization |
| **Scheduling** | `System.Threading.Timer` | Re-entrancy-guarded nightly/weekly triggers |

---

## Security Architecture

- **Zero client attack surface:** No agent, service, or HTTP listener runs on target machines.
- **Least-privilege GPO model:** Target OUs are governed by GPO firewall rules (scoped to the backup server IP), R2L symlink evaluation policy, and a dedicated AD backup service account / gMSA.
- **Hardened VHDX folder ACLs:** Per-machine backup folders grant `FullControl` only to `BUILTIN\Administrators` (`*S-1-5-32-544`) and `NT AUTHORITY\SYSTEM` (`*S-1-5-18`) — never `Everyone`, `Authenticated Users`, or `Domain Computers`.
- **VeraCrypt passphrase protection:** Held in memory only for interactive runs, or optionally stored via Windows DPAPI (`CryptProtectData`) in `%AppData%\ADShield\vault.dat` when `AllowUnattendedVaultMount` is explicitly enabled.
- **All backup data at rest** resides inside an **AES-256 encrypted** VeraCrypt container.

---

## Scheduler & Concurrency Architecture

```mermaid
stateDiagram-v2
    [*] --> Stopped
    Stopped --> Active: ScheduleActive = true\nStart() called
    Active --> NightlyFired: Cron time reached\n(default: 01:00 daily)
    Active --> WeeklyFired: Cron time reached\n(default: Sunday 00:00)
    NightlyFired --> Active: Interlocked guard prevents overlap\nFire "Incremental"
    WeeklyFired --> Active: Interlocked guard prevents overlap\nFire "Full"
    Active --> Stopped: Stop() / Dispose()
```

In `MainForm.Events.cs`, scheduled and manual backups share `ExecuteBackupAsync`, gated by a `SemaphoreSlim` (`MaxConcurrentBackups`, minimum `1`) and a `ConcurrentDictionary<string, bool>` active-job tracker to prevent duplicate concurrent backups for the same host.

---

## Test & Diagnostic Suites

### `ADShield.Tests` (xUnit)

| Test Class | File | What It Validates |
|------------|------|-------------------|
| `UnitTests` | `ADShield.Tests/UnitTests.cs` | `AppSettings` defaults & JSON round-trip, `SchedulerService` inactive start & idempotent `Dispose`, `ParseCronHM`, `RunLocalDiskpart` non-zero exit code exception (admin-gated), `ProcessRunner` streams/cancellation/timeout |
| `LogicTests` | `ADShield.Tests/LogicTests.cs` | `TestVhdxCreateAndFormat` (admin), `TestVhdxExistingMount` (admin), `TestRobocopyArgumentQuoting`, `TestVeraCryptMountDetection`, `TestUncPathResolutionEdgeCases`, `TestRobocopyLocalCopy` |
| `SelfHealingTests` | `ADShield.Tests/SelfHealingTests.cs` | `TestSelfHealingDiagnostics` — admin-gated wrapper around `SelfHealingDiagnostics.RunDiagnosticsAsync` |

### Built-in Runtime Diagnostics (`ADShield/Core/`)

| Class | Trigger | What It Validates |
|-------|---------|-------------------|
| `BackupValidationSuite` | UI (`Run Tests` → Validation) or `--test` | Temp directory R/W, `diskpart.exe /s`, and `robocopy.exe /?` |
| `SelfHealingDiagnostics` | UI (`Run Tests` → Diagnostics) or `--diagnostics` | Creates a 10 MB RAW VHDX in `AppConfig.ReadSettings().BackupRootPath`, allocates a free drive letter (`Z`..`M`), verifies raw write fails, runs GPT/NTFS recovery, and verifies write succeeds |
