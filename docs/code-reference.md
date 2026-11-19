# ADShield — Code Reference

Complete API and class reference for all source files in the `ADShield` project.

> **AD & GPO Operational Guide:** See **[AGENTLESS_ROADMAP.md](AGENTLESS_ROADMAP.md)** for the Group Policy (GPO) prerequisites and security hardening checklist.

---

## Namespace Overview

| Namespace | Purpose |
|-----------|---------|
| `ADShield` | Dual-mode entry point (`Program.cs`): GUI and CLI |
| `ADShield.Core` | Business logic: agentless backup orchestration, discovery, VSS/WMI, storage, DPAPI secrets & process management |
| `ADShield.Forms` | WinForms UI: main window, dialogs, theme engine |
| `ADShield.Models` | Data models: settings, computer entries |

---

## `ADShield`

### `Program`

**File:** `ADShield/Program.cs`  
**Type:** `internal static class`

Dual-mode entry point for `ADShield.exe`:
- CLI arguments (`args.Length > 0`) → Attaches console (`AttachConsole(-1)`) and executes `RunCliAsync(args)`. Fallback bootstrap errors log to `%LocalAppData%\ADShield\bootstrap_error.log`.
- No arguments → Starts WinForms GUI (`Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` + `Application.Run(new MainForm())`).

**Supported CLI Verbs:** `--backup` / `-b`, `--mount`, `--dismount`, `--create`, `--discover` / `-ds`, `--test` / `-ts`, `--diagnostics` / `-dg`, `--help`.

---

## `ADShield.Core`

### `BackupOrchestrator`

**File:** `ADShield/Core/BackupOrchestrator.cs`  
**Type:** `public class`

Executes the 100% agentless, server-only backup pipeline using WMI/VSS on target computers and local `robocopy.exe` server-pull into a dynamically mounted VHDX inside the VeraCrypt vault.

#### Constructor

```csharp
public BackupOrchestrator(AppSettings settings)
```

#### Methods

##### `RunAsync`

```csharp
public async Task RunAsync(
    string computerName,
    string backupType,
    string veraCryptPassword,
    IProgress<string> progress,
    CancellationToken ct = default)
```

Executes the agentless backup pipeline for a single domain computer:
1. Verify or mount the VeraCrypt vault (`VeraCryptManager.IsMounted` / `MountAsync`)
2. ICMP ping the target computer (`Ping.SendPingAsync(computerName, 1500)`)
3. Verify remote WMI connectivity (`VssManager.TestWmiConnectivity`)
4. Prepare per-machine folder (`Path.Combine(_settings.BackupRootPath, computerName)`) with hardened ACLs (`BUILTIN\Administrators` and `NT AUTHORITY\SYSTEM` only) and auto-size/create `disk.vhdx` using `VssManager.QueryRemoteDiskSpace`
5. Dynamically allocate an unused drive letter (`Z` down to `M`), attach VHDX (`VhdxManager.AttachVhdx`), initialize GPT/NTFS or assign partition 2 via `RunLocalDiskpart`, and run a self-heal write probe
6. Create remote VSS shadow copy (`VssManager.CreateRemoteShadowCopy`), resolve `DeviceObject` (`VssManager.GetShadowCopyDeviceObject`), and create a temporary symlink (`C:\_adshield_vss_<id>`) via WMI `Win32_Process.Create`
7. Run local `robocopy.exe` (`/E /COPY:DAT /B /R:1 /W:1 /NP /XJ`) pulling from `\\{computerName}\C$\_adshield_vss_<id>` into `{driveLetter}:\{backupType}\`
8. Cleanup in `finally`: remove remote symlink via WMI, delete remote VSS shadow copy (`VssManager.DeleteRemoteShadowCopy`), and detach VHDX (`VhdxManager.DetachVhdx` with `diskpart` fallback)

---

##### `RunLocalDiskpart` *(internal)*

```csharp
internal static async Task RunLocalDiskpart(
    string diskpartScript,
    IProgress<string> progress,
    CancellationToken ct)
```

Writes a diskpart script to a temp file and executes it locally via `ProcessRunner.RunAsync("diskpart.exe", ...)`. Checks `result.ExitCode != 0` and throws an `Exception` on failure. Requires Administrator elevation.

---

### `VssManager`

**File:** `ADShield/Core/VssManager.cs`  
**Type:** `public static class`

WMI `Win32_ShadowCopy`, `Win32_LogicalDisk`, and `Win32_OperatingSystem` manager for local and remote agentless operations.

| Method | Signature | Description |
|--------|-----------|-------------|
| `CreateLocalShadowCopy` | `public static string CreateLocalShadowCopy(string volume, IProgress<string>? progress = null)` | Creates a local VSS shadow copy |
| `CreateRemoteShadowCopy` | `public static string CreateRemoteShadowCopy(string computerName, string volume, string? username = null, string? password = null, IProgress<string>? progress = null)` | Creates a remote VSS shadow copy via WMI |
| `ListShadowCopies` | `public static List<ShadowCopyInfo> ListShadowCopies(string? computerName = null)` | Lists VSS shadow copies |
| `DeleteShadowCopy` | `public static void DeleteShadowCopy(string shadowId, IProgress<string>? progress = null)` | Deletes a local VSS shadow copy by GUID |
| `GetShadowCopyDeviceObject` | `public static string GetShadowCopyDeviceObject(string computerName, string shadowId)` | Queries `Win32_ShadowCopy` for the `DeviceObject` path (with trailing `\`) |
| `DeleteRemoteShadowCopy` | `public static void DeleteRemoteShadowCopy(string computerName, string shadowId, IProgress<string>? progress = null)` | Deletes a remote VSS shadow copy via WMI |
| `QueryRemoteDiskSpace` | `public static (double usedGb, double totalGb) QueryRemoteDiskSpace(string computerName, string drive = "C:")` | Queries `Win32_LogicalDisk` for used and total GB |
| `TestWmiConnectivity` | `public static bool TestWmiConnectivity(string computerName, out string error)` | Opens a `ManagementScope` on `\\{computerName}\root\cimv2` (10s timeout) and queries `Win32_OperatingSystem` |

---

### `VaultSecretStore`

**File:** `ADShield/Core/VaultSecretStore.cs`  
**Type:** `public static class`

DPAPI secret storage using `crypt32.dll` P/Invoke (`CryptProtectData` / `CryptUnprotectData`), storing the encrypted VeraCrypt passphrase in `Path.Combine(AppConfig.DataDir, "vault.dat")`.

| Method | Signature | Description |
|--------|-----------|-------------|
| `SavePassphrase` | `public static void SavePassphrase(string password)` | Encrypts and saves the passphrase with DPAPI |
| `LoadPassphrase` | `public static string? LoadPassphrase()` | Decrypts and returns the saved passphrase, or `null` if unavailable |
| `ClearPassphrase` | `public static void ClearPassphrase()` | Deletes `vault.dat` if present |
| `HasPassphrase` | `public static bool HasPassphrase()` | Returns `true` if `vault.dat` exists |

---

### `ProcessRunner`

**File:** `ADShield/Core/ProcessRunner.cs`  
**Type:** `public static class`

Centralized async child-process execution helper used across `BackupOrchestrator`, `VeraCryptManager`, `BackupValidationSuite`, and `Program`.

```csharp
public static async Task<ProcessRunnerResult> RunAsync(
    string fileName,
    string? arguments = null,
    IEnumerable<string>? argumentList = null,
    TimeSpan? timeout = null,
    IProgress<string>? progress = null,
    CancellationToken ct = default)
```

Streams stdout/stderr to `progress` and kills the entire process tree (`Process.Kill(entireProcessTree: true)`) on timeout or cancellation. Returns `ProcessRunnerResult` (`ExitCode`, `StandardOutput`, `StandardError`).

---

### `AdDiscovery`

**File:** `ADShield/Core/AdDiscovery.cs`  
**Type:** `public static class`

Queries Active Directory using LDAP via `System.DirectoryServices`.

#### `Discover`

```csharp
public static List<ComputerEntry> Discover(
    string searchOU,
    string groupName,
    bool pingCheck,
    IProgress<string>? progress = null)
```

Returns a list of `ComputerEntry` objects from the domain, optionally filtered to a specific OU and/or AD security group (recursive `memberOf` via LDAP OID `1.2.840.113556.1.4.1941`), with 800ms ICMP ping and IP resolution, merged with saved backup history.

---

### `AppConfig`

**File:** `ADShield/Core/AppConfig.cs`  
**Type:** `public static class`

Manages JSON persistence for application settings and backup history in `%AppData%\ADShield\`.

| Method | Description |
|--------|-------------|
| `ReadSettings()` / `SaveSettings(AppSettings s)` | Reads/writes `%AppData%\ADShield\config.json` |
| `ReadHistory()` / `SaveHistory(List<ComputerEntry> computers)` | Reads/writes `%AppData%\ADShield\history.json` |
| `MergeDiscovered(List<ComputerEntry> discovered)` | Merges newly discovered hosts while preserving `LastBackupStatus` and `LastBackupTime` |
| `UpdateBackupResult(string computerName, string status)` | Updates a machine's `LastBackupStatus` and sets `LastBackupTime` to `DateTime.Now` |

---

### `VeraCryptManager`

**File:** `ADShield/Core/VeraCryptManager.cs`  
**Type:** `public static class`

Manages the VeraCrypt encrypted container lifecycle via `ProcessRunner`.

| Method | Signature | Description |
|--------|-----------|-------------|
| `MountAsync` | `public static async Task MountAsync(AppSettings settings, string password, IProgress<string>? progress = null, CancellationToken ct = default)` | Mounts the `.hc` container to `MountLetter` (`/silent /quit`, 60s timeout) |
| `DismountAsync` | `public static async Task DismountAsync(AppSettings settings, IProgress<string>? progress = null, CancellationToken ct = default)` | Dismounts the VeraCrypt volume |
| `CreateContainerAsync` | `public static async Task CreateContainerAsync(AppSettings settings, string password, string sizeSpec, IProgress<string>? progress = null, CancellationToken ct = default)` | Creates an AES-256 / SHA-512 / NTFS `.hc` container via `VeraCrypt Format.exe` |
| `IsMounted` | `public static bool IsMounted(string mountLetter)` | Checks `DriveInfo.GetDrives()` for a ready drive matching `mountLetter` |
| `VeraCryptInstalled` | `public static bool VeraCryptInstalled(string exePath)` | Returns `File.Exists(exePath)` |
| `ResolveUncPath` | `public static string ResolveUncPath(string path)` | Resolves mapped drive letters to UNC paths via `HKCU\Network` or WMI `Win32_LogicalDisk` |

---

### `VhdxManager`

**File:** `ADShield/Core/VhdxManager.cs`  
**Type:** `public static class`

Creates, attaches, and detaches VHDX virtual disk files using native `virtdisk.dll` P/Invoke.

| Method | Signature | Description |
|--------|-----------|-------------|
| `CreateVhdx` | `public static void CreateVhdx(string vhdxPath, ulong sizeBytes = 1_099_511_627_776UL, IProgress<string>? progress = null)` | Creates a dynamically expanding VHDX |
| `AttachVhdx` | `public static void AttachVhdx(string vhdxPath, IProgress<string>? progress = null)` | Attaches an existing VHDX via `AttachVirtualDisk` |
| `DetachVhdx` | `public static void DetachVhdx(string vhdxPath, IProgress<string>? progress = null)` | Detaches a mounted VHDX via `DetachVirtualDisk` |
| `VhdxExists` | `public static bool VhdxExists(string path)` | Returns `File.Exists(path)` |

---

### `SmbShareManager`

**File:** `ADShield/Core/SmbShareManager.cs`  
**Type:** `public static class`

Minimal WMI `Win32_Share` query helper.

| Method | Signature | Description |
|--------|-----------|-------------|
| `ShareExists` | `public static bool ShareExists(string shareName)` | Queries `Win32_Share` to check if a share exists |

---

### `SchedulerService`

**File:** `ADShield/Core/SchedulerService.cs`  
**Type:** `public class : IDisposable`

Re-entrancy-guarded daily/weekly scheduler using `System.Threading.Timer` and `Interlocked.Exchange` overlap protection.

| Member | Description |
|--------|-------------|
| `BackupTriggered` (`event Action<string>?`) | Fired with `"Incremental"` (nightly) or `"Full"` (weekly) |
| `Start()` | Enables scheduling based on `AppSettings.ScheduleActive` (thread-safe via `_lock`) |
| `Stop()` / `Dispose()` | Stops and disposes timers idempotently |
| `ParseCronHM(string cron, int defaultH, int defaultM)` | `public static` helper parsing and bounds-validating hour/minute from 5-field cron strings |

---

### `BackupValidationSuite`

**File:** `ADShield/Core/BackupValidationSuite.cs`  
**Type:** `public static class`

Pre-flight validation suite triggered from the UI or CLI (`--test`).

```csharp
public static async Task RunValidationAsync(IProgress<string> progress, CancellationToken ct)
```

Verifies temp folder read/write access, `diskpart.exe /s`, and `robocopy.exe /?`.

---

### `SelfHealingDiagnostics`

**File:** `ADShield/Core/SelfHealingDiagnostics.cs`  
**Type:** `public static class`

Diagnostic regression test triggered from the UI, CLI (`--diagnostics`), or `ADShield.Tests/SelfHealingTests.cs`.

```csharp
public static async Task RunDiagnosticsAsync(IProgress<string> progress, CancellationToken ct)
```

Creates a 10 MB RAW VHDX in `AppConfig.ReadSettings().BackupRootPath`, dynamically selects a free drive letter (`Z`..`M`), verifies raw write fails, triggers GPT/NTFS recovery via `BackupOrchestrator.RunLocalDiskpart`, verifies write succeeds, and cleans up in `finally`.

---

## `ADShield.Models`

### `AppSettings`

**File:** `ADShield/Models/AppSettings.cs`  
**Persisted to:** `%AppData%\ADShield\config.json`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `VeraCryptExePath` | `string` | `C:\Program Files\VeraCrypt\VeraCrypt.exe` | Path to VeraCrypt executable |
| `VeraCryptContainer` | `string` | `C:\BackupVault.hc` | Path to the encrypted `.hc` container |
| `MountLetter` | `string` | `V` | Drive letter for the mounted vault |
| `BackupStorageRoot` | `string` | `backups` | Subfolder inside mounted vault |
| `VhdxSizeGb` | `long` | `1024` | Fallback VHDX size in GB |
| `SearchOU` | `string` | `""` | LDAP OU path (empty = entire domain) |
| `AdGroup` | `string` | `""` | AD group filter (empty = all computers) |
| `ScheduleActive` | `bool` | `false` | Whether automated scheduling is enabled |
| `NightlyCron` | `string` | `0 1 * * *` | Nightly incremental schedule |
| `WeeklyCron` | `string` | `0 0 * * 0` | Weekly full schedule |
| `DomainAdminContext` | `bool` | `true` | Domain admin context flag |
| `MaxConcurrentBackups` | `int` | `1` | Maximum concurrent backup jobs (`SemaphoreSlim`) |
| `AllowUnattendedVaultMount` | `bool` | `false` | Allows scheduled backups to mount the vault using `VaultSecretStore` |
| `BackupRootPath` | `string` *(computed)* | `Path.Combine($"{MountLetter}:\\", BackupStorageRoot)` | Full path to backup root (e.g. `V:\backups`) |

---

### `ComputerEntry`

**File:** `ADShield/Models/ComputerEntry.cs`  
**Persisted to:** `%AppData%\ADShield\history.json`

| Property | Type | Description |
|----------|------|-------------|
| `ComputerName` | `string` | NetBIOS computer name (from AD `cn`) |
| `DnsHostName` | `string` | FQDN (from AD `dnshostname`) |
| `OU` | `string` | Distinguished name of the containing OU |
| `OperatingSystem` | `string` | OS string from AD |
| `IsOnline` | `bool` | ICMP ping result |
| `PingMs` | `int` | Round-trip time in milliseconds |
| `LastBackupStatus` | `string` | `"Never Backed Up"`, `"Success"`, `"Failed"`, etc. |
| `LastBackupTime` | `DateTime?` | Timestamp of last completed backup |
| `IPAddress` | `string` | Resolved IPv4 address |
| `OnlineDisplay` | `string` *(computed)* | `"Online  (12 ms)"` or `"Offline"` |
| `LastBackupTimeDisplay` | `string` *(computed)* | Formatted timestamp or `"—"` |

---

### `ShadowCopyInfo`

**File:** `ADShield/Core/VssManager.cs`  
**Type:** `public record`

| Property | Type | Description |
|----------|------|-------------|
| `ID` | `string` | VSS shadow copy GUID |
| `VolumeName` | `string` | Source volume |
| `DeviceObject` | `string` | Shadow device path |
| `InstallDate` | `string` | WMI `InstallDate` string |

---

## `ADShield.Forms`

### `MainForm`

**File:** `ADShield/Forms/MainForm.cs` + `ADShield/Forms/MainForm.Events.cs`  
**Type:** `public partial class : Form`

Application shell hosting 4 navigation pages (`_pgDashboard`, `_pgComputers`, `_pgLogs`, `_pgSettings`), KPI cards, data grids, terminal log, CSV export, `MaxConcurrentBackups` / `AllowUnattendedVaultMount` / DPAPI passphrase controls, and `SemaphoreSlim` + `ConcurrentDictionary<string, bool>` concurrency control (`ExecuteBackupAsync`).

### `Theme`

**File:** `ADShield/Forms/Theme.cs`  
**Type:** `public static class`

Centralized dark-theme color tokens, fonts, and WinForms control factory methods.

### `BackupTriggerForm`

**File:** `ADShield/Forms/BackupTriggerForm.cs`  
**Type:** `public class : Form`

Modal dialog collecting `SelectedBackupType` (`"Incremental"` or `"Full"`) and `VeraCryptPassword` before invoking `ExecuteBackupAsync`.
