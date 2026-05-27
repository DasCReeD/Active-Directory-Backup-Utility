# ADShield — Code Reference

Complete API and class reference for all source files in the `ADShield` project.

---

## Namespace Overview

| Namespace | Purpose |
|-----------|---------|
| `ADShield.Core` | Business logic: backup orchestration, discovery, storage management |
| `ADShield.Forms` | WinForms UI: main window, dialogs, theme engine |
| `ADShield.Models` | Data models: settings, computer entries |

---

## `ADShield.Core`

### `BackupOrchestrator`

**File:** `ADShield/Core/BackupOrchestrator.cs`  
**Type:** `public class`

The primary entry point for executing an end-to-end agentless backup. Coordinates all subsystems in a strict 8-step pipeline.

#### Constructor

```csharp
public BackupOrchestrator(AppSettings settings)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `settings` | `AppSettings` | Application configuration including paths, vault location, and scheduling |

#### Methods

---

##### `RunAsync`

```csharp
public async Task RunAsync(
    string computerName,
    string backupType,
    string veraCryptPassword,
    IProgress<string> progress,
    CancellationToken ct = default)
```

Executes the full 8-step backup pipeline for a single domain computer.

| Parameter | Type | Description |
|-----------|------|-------------|
| `computerName` | `string` | NetBIOS name of the target domain computer |
| `backupType` | `string` | `"Incremental"` or `"Full"` |
| `veraCryptPassword` | `string` | VeraCrypt vault passphrase (held in memory only, never persisted) |
| `progress` | `IProgress<string>` | Receives `[LEVEL] message` status updates in real time |
| `ct` | `CancellationToken` | Cancellation support for graceful abort |

**Throws:** `Exception` at any failed step (WMI unreachable, ping failed, robocopy error, etc.)

**Pipeline Steps (Server-Pull Architecture):**
1. Verify/mount VeraCrypt vault
2. ICMP ping target computer
3. WMI connectivity check
4. Create per-machine VHDX on backup server (sized to remote C: + 20% headroom)
5. Mount VHDX locally and initialize NTFS (self-heals RAW partitions)
6. Enable R2L symlink evaluation on remote client (WMI registry write)
7. Create VSS shadow copy on remote client, create symlink to VSS, run server-pull robocopy
8. Cleanup: remove symlink, delete shadow copy, detach VHDX

---

##### `RunLocalDiskpart` *(internal)*

```csharp
internal static async Task RunLocalDiskpart(
    string diskpartScript,
    IProgress<string> progress,
    CancellationToken ct)
```

Writes a diskpart script to a temp file and executes it locally via `cmd.exe /c diskpart /s`. Output is captured and forwarded to `progress`.

> ⚠️ **Requires elevation.** ADShield must run as Administrator for diskpart to succeed.

---

##### `EnableRemoteSymlinkEvaluation` *(private static)*

```csharp
private static void EnableRemoteSymlinkEvaluation(string computerName, IProgress<string> progress)
```

Enables Remote-to-Local (R2L) symlink evaluation on the target machine via WMI `StdRegProv` registry write. Sets `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\SymlinkRemoteToLocalEvaluation = 1`. Falls back to `fsutil behavior set symlinkevaluation R2L:1` via WMI `Win32_Process` if the registry method fails. Idempotent — safe to call multiple times.

---

##### `CreateRemoteVssSymlink` *(private static)*

```csharp
private static async Task CreateRemoteVssSymlink(
    string computerName, string linkPath, string shadowDevicePath,
    IProgress<string> progress, CancellationToken ct)
```

Creates a directory symlink on the remote client (`mklink /d`) pointing to the VSS shadow device path. Cleans up any stale symlink before creating. Uses WMI `Win32_Process.Create`.

---

##### `RemoveRemoteVssSymlink` *(private static)*

```csharp
private static async Task RemoveRemoteVssSymlink(
    string computerName, string linkPath,
    IProgress<string> progress, CancellationToken ct)
```

Removes the VSS symlink from the remote client (`rmdir`) via WMI. Called during cleanup.

---

##### `RunLocalDataCopy` *(private)*

```csharp
private async Task RunLocalDataCopy(
    string uncSource, string localDest, string computerName,
    IProgress<string> progress, CancellationToken ct)
```

Runs `robocopy.exe` locally on the backup server, pulling data from the client's admin share (e.g. `\\CLIENT\C$\adshield_vss_link`) into the local VHDX mount (`B:\`). This is the core of the server-pull architecture — single network hop, no Kerberos double-hop.

**Robocopy flags:** `/E /COPY:DAT /B /R:1 /W:1 /NP /XJ`  
**Timeout:** 2 hours (cancellable)  
**Exit code handling:** 0–7 = success/warnings, ≥8 = failure (throws)

---

##### `RunRemoteWmiCommand` *(private static)*

```csharp
private static void RunRemoteWmiCommand(string computerName, string commandLine, int timeoutMs)
```

Executes a command on a remote machine via WMI `Win32_Process.Create` and polls for process completion. Used for lightweight operations (symlink create/delete, fsutil). NOT for long-running data copies.

---

### `AdDiscovery`

**File:** `ADShield/Core/AdDiscovery.cs`  
**Type:** `public static class`

Queries Active Directory using LDAP via `System.DirectoryServices`. No PowerShell or external AD modules required.

#### Methods

---

##### `Discover`

```csharp
public static List<ComputerEntry> Discover(
    string searchOU,
    string groupName,
    bool pingCheck,
    IProgress<string>? progress = null)
```

Returns a list of `ComputerEntry` objects from the domain, optionally filtered to a specific OU and/or AD security group.

| Parameter | Type | Description |
|-----------|------|-------------|
| `searchOU` | `string` | Distinguished name of the OU to search (e.g. `OU=Workstations,DC=corp,DC=local`). Empty string searches the entire domain. |
| `groupName` | `string` | AD security group name to filter computers (uses recursive `memberOf` via LDAP OID `1.2.840.113556.1.4.1941`). Empty string returns all computers. |
| `pingCheck` | `bool` | If `true`, sends an 800ms ICMP ping to each computer and records online/offline state |
| `progress` | `IProgress<string>?` | Optional progress reporting |

**Returns:** `List<ComputerEntry>` with backup history merged from `AppConfig`

**LDAP filter (group scope):**
```
(&(objectCategory=computer)(memberOf:1.2.840.113556.1.4.1941:=<groupDN>))
```

**LDAP filter (all computers):**
```
(objectCategory=computer)
```

**Attributes loaded:** `cn`, `dnshostname`, `distinguishedname`, `operatingSystem`

---

### `AppConfig`

**File:** `ADShield/Core/AppConfig.cs`  
**Type:** `public static class`

Manages JSON persistence for application settings and backup history. Files are stored in `%AppData%\ADShield\`.

#### Properties

| Property | Type | Value |
|----------|------|-------|
| `DataDir` | `string` | `%AppData%\ADShield\` |

#### Methods

---

##### `ReadSettings` / `SaveSettings`

```csharp
public static AppSettings ReadSettings()
public static void SaveSettings(AppSettings s)
```

Reads or writes `config.json`. Returns a default `AppSettings` instance if the file doesn't exist or is corrupt.

---

##### `ReadHistory` / `SaveHistory`

```csharp
public static List<ComputerEntry> ReadHistory()
public static void SaveHistory(List<ComputerEntry> computers)
```

Reads or writes `history.json` (the backup state database).

---

##### `MergeDiscovered`

```csharp
public static List<ComputerEntry> MergeDiscovered(List<ComputerEntry> discovered)
```

Merges freshly discovered computers into the saved history, **preserving** `LastBackupStatus` and `LastBackupTime` for machines that already have records.

---

##### `UpdateBackupResult`

```csharp
public static void UpdateBackupResult(string computerName, string status)
```

Updates a single machine's backup result in history. Sets `LastBackupTime` to `DateTime.Now`.

| Parameter | Type | Description |
|-----------|------|-------------|
| `computerName` | `string` | NetBIOS name to update |
| `status` | `string` | e.g. `"Success"`, `"Failed"` |

---

### `VeraCryptManager`

**File:** `ADShield/Core/VeraCryptManager.cs`  
**Type:** `public static class`

Manages the VeraCrypt encrypted container lifecycle. Uses `Process.Start` to invoke `VeraCrypt.exe` — the only permitted `Process.Start` usage in the codebase, justified because VeraCrypt has no COM or managed API.

#### Methods

---

##### `Mount`

```csharp
public static void Mount(AppSettings settings, string password, IProgress<string>? progress = null)
```

Mounts a VeraCrypt container to the configured drive letter. Uses `/silent /quit` flags. Waits up to 60 seconds (for network volumes).

**Throws:** `FileNotFoundException` if `VeraCryptExePath` is not found.  
**Throws:** `Exception` if mount fails (wrong passphrase, container missing).

---

##### `Dismount`

```csharp
public static void Dismount(AppSettings settings, IProgress<string>? progress = null)
```

Dismounts the VeraCrypt volume. No-ops if already unmounted.

---

##### `CreateContainer`

```csharp
public static void CreateContainer(AppSettings settings, string password, string sizeSpec,
    IProgress<string>? progress = null)
```

Creates a new VeraCrypt encrypted container using `VeraCrypt Format.exe`. Waits up to 5 minutes for large volumes.

| Parameter | Type | Description |
|-----------|------|-------------|
| `sizeSpec` | `string` | Size in VeraCrypt CLI format (e.g. `"500G"`, `"2T"`) |

**Container parameters:** AES encryption, SHA-512 hash, NTFS filesystem

---

##### `IsMounted`

```csharp
public static bool IsMounted(string mountLetter) 
```

Returns `true` if a ready drive exists starting with the given letter (uses `DriveInfo.GetDrives()`).

---

##### `ResolveUncPath`

```csharp
public static string ResolveUncPath(string path)
```

Converts a mapped drive letter path (e.g. `Z:\vault.hc`) to its UNC equivalent (e.g. `\\server\share\vault.hc`). Required because elevated processes cannot see drive mappings from the non-elevated session.

**Resolution methods (in order):**
1. Registry: `HKCU\Network\<Letter>\RemotePath`
2. WMI: `Win32_LogicalDisk.ProviderName`

---

### `VssManager`

**File:** `ADShield/Core/VssManager.cs`  
**Type:** `public static class`

VSS (Volume Shadow Copy Service) management via WMI `Win32_ShadowCopy`. Works both locally and over remote WMI connections.

#### Methods

---

##### `CreateLocalShadowCopy`

```csharp
public static string CreateLocalShadowCopy(string volume, IProgress<string>? progress = null)
```

Creates a VSS shadow copy on the **local** machine.

**Returns:** Shadow copy GUID string (the `ShadowID`)

---

##### `CreateRemoteShadowCopy`

```csharp
public static string CreateRemoteShadowCopy(
    string computerName,
    string volume,
    string? username = null,
    string? password = null,
    IProgress<string>? progress = null)
```

Creates a VSS shadow copy on a **remote** machine via WMI impersonation.

| Parameter | Type | Description |
|-----------|------|-------------|
| `computerName` | `string` | Target machine NetBIOS/DNS name |
| `volume` | `string` | Volume to shadow (e.g. `C:\`) |
| `username` | `string?` | Optional explicit WMI credential (default: current user's domain context) |

**Returns:** Shadow copy GUID string

---

##### `ListShadowCopies`

```csharp
public static List<ShadowCopyInfo> ListShadowCopies(string? computerName = null)
```

Lists all VSS shadow copies, optionally on a remote machine.

---

##### `DeleteShadowCopy`

```csharp
public static void DeleteShadowCopy(string shadowId, IProgress<string>? progress = null)
```

Deletes a VSS shadow copy by GUID. Used during backup cleanup.

---

##### `TestWmiConnectivity`

```csharp
public static bool TestWmiConnectivity(string computerName, out string error)
```

Tests WMI connectivity to a remote machine with a 10-second timeout.

**Returns:** `true` if `scope.IsConnected` after connecting; `false` with error message otherwise.

---

### `VhdxManager`

**File:** `ADShield/Core/VhdxManager.cs`  
**Type:** `public static class`

Creates, attaches, and detaches VHDX virtual disk files using the native Windows `VirtDisk.dll` API via P/Invoke. No diskpart scripts, no external tools.

#### Methods

---

##### `CreateVhdx`

```csharp
public static void CreateVhdx(string vhdxPath, ulong sizeBytes = 1_099_511_627_776UL,
    IProgress<string>? progress = null)
```

Creates a new dynamically expanding VHDX at the specified path.

| Parameter | Type | Description |
|-----------|------|-------------|
| `vhdxPath` | `string` | Absolute path for the new `.vhdx` file |
| `sizeBytes` | `ulong` | Maximum size in bytes (default: 1 TB) |

**Implementation:** Calls `VirtDisk.CreateVirtualDisk` with VHDX device type, Microsoft vendor GUID, 512-byte sectors.

---

##### `AttachVhdx`

```csharp
public static void AttachVhdx(string vhdxPath, IProgress<string>? progress = null)
```

Attaches (mounts) an existing VHDX as a local disk using `VirtDisk.AttachVirtualDisk`. The disk appears in Disk Management without a drive letter until assigned by diskpart.

---

##### `DetachVhdx`

```csharp
public static void DetachVhdx(string vhdxPath, IProgress<string>? progress = null)
```

Detaches (unmounts) a VHDX disk. Reopens the file handle then calls `VirtDisk.DetachVirtualDisk`.

---

##### `VhdxExists`

```csharp
public static bool VhdxExists(string path)
```

Returns `true` if the VHDX file exists at the given path.

---

### `SmbShareManager` *(Legacy — retained for compatibility)*

**File:** `ADShield/Core/SmbShareManager.cs`  
**Type:** `public static class`

> ⚠️ **Legacy module.** The server-pull architecture no longer uses dynamic SMB shares during backups. This module is retained for potential future use cases or manual share management but is not called by `BackupOrchestrator`.

Creates and removes dynamic, hidden per-machine SMB shares via WMI `Win32_Share`. No `net.exe` or external tools required.

#### Methods

---

##### `CreateShare`

```csharp
public static void CreateShare(
    string sharePath,
    string computerName,
    string mountLetter,
    IProgress<string>? progress = null)
```

Creates a hidden SMB share named `backup_<computerName>$` pointing to the specified path. Recreates the share if it already exists (to refresh permissions).

**DACL includes:**
- `BUILTIN\Administrators` — Full Control
- `Everyone` — Full Control (SMB layer; NTFS ACLs provide final restriction)
- `NT AUTHORITY\Authenticated Users` — Full Control
- `DOMAIN\ComputerName$` — Full Control (machine account)
- `DOMAIN\Domain Computers` — Full Control

---

##### `RemoveShare`

```csharp
public static void RemoveShare(string computerName, IProgress<string>? progress = null)
```

Removes the hidden SMB share for a computer by invoking `Win32_Share.Delete()`.

---

##### `ShareExists`

```csharp
public static bool ShareExists(string shareName)
```

Returns `true` if the named share exists via a WMI query.

---

### `SchedulerService`

**File:** `ADShield/Core/SchedulerService.cs`  
**Type:** `public class : IDisposable`

Simple daily/weekly scheduler using `System.Threading.Timer`. Parses hour/minute from a 5-field cron string.

#### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `BackupTriggered` | `Action<string>?` | Fired with `"Incremental"` (nightly) or `"Full"` (weekly) |

#### Methods

| Method | Description |
|--------|-------------|
| `Start()` | Enables scheduling based on `AppSettings.ScheduleActive` |
| `Stop()` | Disposes both timers |
| `Dispose()` | Calls `Stop()` |

**Cron parsing:** Only hour and minute fields are used from the 5-field cron expression (e.g. `"0 1 * * *"` → 01:00). No full cron engine dependency.

---

### `BackupSelfHealingTest`

**File:** `ADShield/Core/BackupSelfHealingTest.cs`  
**Type:** `public static class`

Diagnostic regression test that validates the VHDX self-healing logic without involving a real target computer.

#### Methods

---

##### `RunDiagnosticTest`

```csharp
public static async Task RunDiagnosticTest(IProgress<string> progress, CancellationToken ct)
```

Runs a 7-step local regression test:

1. Ensure backup root folder exists
2. Clean up leftover test artifacts
3. Create a 10 MB raw VHDX file
4. Attach it via diskpart (no format)
5. Assign drive letter `T:` — verify write fails (expected on unformatted disk)
6. Trigger NTFS self-healing format sequence
7. Verify write succeeds after recovery

All artifacts are cleaned up in `finally` regardless of outcome.

---

### `BackupServerPullTest`

**File:** `ADShield/Core/BackupServerPullTest.cs`  
**Type:** `public static class`

Integration test suite for the server-pull backup architecture. Validates end-to-end that the server can pull data from a remote client's admin share through a VSS symlink.

#### Methods

---

##### `RunAllTests`

```csharp
public static async Task RunAllTests(string computerName, IProgress<string> progress, CancellationToken ct)
```

Runs all three server-pull tests in sequence, reporting pass/fail for each.

---

##### `TestR2LSymlinkEvaluation`

```csharp
public static async Task TestR2LSymlinkEvaluation(string computerName, IProgress<string> progress, CancellationToken ct)
```

Validates that the R2L symlink evaluation registry key can be enabled on a remote machine via WMI `StdRegProv`. Reads current value → sets to 1 → verifies persistence.

---

##### `TestVssSymlinkAccess`

```csharp
public static async Task TestVssSymlinkAccess(string computerName, IProgress<string> progress, CancellationToken ct)
```

Creates a VSS shadow copy and symlink on the remote client, then attempts a `Directory.GetDirectories()` call from the server through the admin share (`\\CLIENT\C$\adshield_test_link`). Validates the full R2L + symlink traversal path.

---

##### `TestEndToEndServerPull`

```csharp
public static async Task TestEndToEndServerPull(string computerName, IProgress<string> progress, CancellationToken ct)
```

Full end-to-end test: creates a 50 MB test VHDX, mounts it, creates VSS + symlink on the client, runs a limited robocopy pull (`/LEV:1` — one level deep in Windows directory for speed), and verifies files were copied to the VHDX. All artifacts cleaned up in `finally`.

---

## `ADShield.Models`

### `AppSettings`

**File:** `ADShield/Models/AppSettings.cs`  
**Persisted to:** `%AppData%\ADShield\config.json`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `VeraCryptExePath` | `string` | `C:\Program Files\VeraCrypt\VeraCrypt.exe` | Path to VeraCrypt executable |
| `VeraCryptContainer` | `string` | `C:\BackupVault.hc` | Path to the encrypted `.hc` container |
| `MountLetter` | `string` | `V` | Single drive letter for the mounted vault |
| `BackupStorageRoot` | `string` | `backups` | Subfolder inside mounted vault |
| `VhdxSizeGb` | `long` | `1024` | Fallback VHDX size if remote disk query fails |
| `SearchOU` | `string` | `""` | LDAP OU path (empty = entire domain) |
| `AdGroup` | `string` | `""` | AD group filter (empty = all computers) |
| `ScheduleActive` | `bool` | `false` | Whether automated scheduling is enabled |
| `NightlyCron` | `string` | `0 1 * * *` | Nightly incremental schedule (cron format) |
| `WeeklyCron` | `string` | `0 0 * * 0` | Weekly full schedule (cron format) |
| `DomainAdminContext` | `bool` | `true` | WMI runs under domain admin context |

#### Computed Property

```csharp
public string BackupRootPath => Path.Combine($"{MountLetter}:\\", BackupStorageRoot);
// e.g. "V:\\backups"
```

---

### `ComputerEntry`

**File:** `ADShield/Models/ComputerEntry.cs`  
**Persisted to:** `%AppData%\ADShield\history.json`

| Property | Type | Description |
|----------|------|-------------|
| `ComputerName` | `string` | NetBIOS computer name (from AD `cn`) |
| `DnsHostName` | `string` | FQDN (from AD `dnshostname`) |
| `OU` | `string` | Distinguished name of the containing OU |
| `OperatingSystem` | `string` | OS string from AD (e.g. `Windows 10 Pro`) |
| `IsOnline` | `bool` | ICMP ping result at time of discovery |
| `PingMs` | `int` | Round-trip time in milliseconds |
| `LastBackupStatus` | `string` | `"Never Backed Up"`, `"Success"`, `"Failed"`, etc. |
| `LastBackupTime` | `DateTime?` | Timestamp of last completed backup |

#### Computed Properties

```csharp
public string OnlineDisplay   // "Online  (12 ms)" or "Offline"
public string LastBackupTimeDisplay   // "2025-05-22 01:03" or "—"
```

---

### `ShadowCopyInfo`

**File:** `ADShield/Core/VssManager.cs`  
**Type:** `public record`

| Property | Type | Description |
|----------|------|-------------|
| `ID` | `string` | VSS shadow copy GUID |
| `VolumeName` | `string` | Source volume (e.g. `\\?\Volume{...}\`) |
| `DeviceObject` | `string` | Shadow device path (e.g. `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\`) |
| `InstallDate` | `string` | WMI InstallDate string |

---

## `ADShield.Forms`

### `MainForm`

**File:** `ADShield/Forms/MainForm.cs` + `MainForm.Events.cs`  
**Type:** `public partial class : Form`

The application shell. Hosts a 4-page navigation UI with sidebar, header, KPI cards, data grids, terminal log, and settings panel.

#### Pages

| Page Panel | Nav Label | Content |
|------------|-----------|---------|
| `_pgDashboard` | ▦ Dashboard | KPI cards + computer grid + terminal log |
| `_pgComputers` | ⊞ Domain Clients | Full computer inventory with search filter |
| `_pgLogs` | ≡ Operation Logs | Chronological log registry with CSV export |
| `_pgSettings` | ⚙ System Config | VeraCrypt, storage, AD, and schedule config |

#### KPI Cards (Dashboard)

| Label | Metric |
|-------|--------|
| Success Rate | % of computers with `"Success"` last status |
| Total Discovered | Total computers in history |
| Active Remotes | Computers currently `IsOnline` |
| VeraCrypt Vault | `"Locked"` or `"Unlocked (V:)"` |

### `Theme`

**File:** `ADShield/Forms/Theme.cs`  
**Type:** `public static class`

Centralized design token class. All colors, fonts, and control factory methods live here.

**Color palette (dark enterprise UI):**
- `Background`: `#0D1117`
- `SidebarBg`: `#111820`
- `Surface`: `#161D28`
- `SurfaceRaised`: `#1E2A38`
- `Accent`: `#00BFFF` (cyan blue)
- `Success` / `Warning` / `Danger`: standard semantic colors
- `TextPrimary`: `#E8F0FE`
- `TextSecondary`: `#8892A4`

**Factory methods:** `MakeButton`, `MakeTextBox`, `MakeGrid`, `MakeSeparator`

### `BackupTriggerForm`

**File:** `ADShield/Forms/BackupTriggerForm.cs`

Modal dialog that collects:
- VeraCrypt vault passphrase (password field)
- Backup type selection: `Incremental` / `Full`

Returns selections to the caller for use in `BackupOrchestrator.RunAsync()`.

---

## Error Handling Patterns

All core methods follow this pattern for progress-aware error handling:

```csharp
// Report status updates via IProgress<string> with tagged levels:
progress.Report("[INFO] Starting operation...");
progress.Report("[SUCCESS] Operation completed.");
progress.Report("[WARN] Non-fatal issue encountered.");
progress.Report("[ERROR] Fatal failure message.");  // followed by throw
```

`CancellationToken` is checked at every major pipeline step with `ct.ThrowIfCancellationRequested()`, ensuring the UI can cancel long-running backup sessions cleanly.
