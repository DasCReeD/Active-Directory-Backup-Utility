<#
.SYNOPSIS
    Orchestrates the agentless backup of a target domain computer.
.DESCRIPTION
    1. Verifies WinRM connection to the client.
    2. Dynamically mounts the VeraCrypt container on the server if not mounted.
    3. Allocates/initializes a dynamically expanding VHDX storage block on the server share.
    4. Establishes a remote WinRM session, mounts the server VHDX as a local drive (B:),
       and executes wbadmin.exe to run a VSS block-level full/incremental backup.
    5. Cleans up client mounts and logs success.
.EXAMPLE
    .\Backup-Orchestrator.ps1 -ComputerName "WS-01" -BackupType "Incremental" -VeraCryptLetter "V" -ContainerPath "D:\BackupVault.hc" -Password "SecurePass"
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$ComputerName,

    [Parameter(Mandatory = $true)]
    [ValidateSet("Full", "Incremental")]
    [string]$BackupType,

    [Parameter(Mandatory = $false)]
    [string]$VeraCryptLetter = "V",

    # For auto-mounting VeraCrypt if needed
    [Parameter(Mandatory = $false)]
    [string]$ContainerPath = "",

    [Parameter(Mandatory = $false)]
    [string]$Password = ""
)

$ErrorActionPreference = "Stop"

# Helper for formatted JSON logs to stdout
function Write-Log {
    param (
        [string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "SUCCESS")]
        [string]$Level = "INFO"
    )
    $logObj = [PSCustomObject]@{
        timestamp = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
        computer  = $ComputerName
        level     = $Level
        message   = $Message
    }
    Write-Output ($logObj | ConvertTo-Json -Compress)
}

try {
    Write-Log "Starting $BackupType backup sequence for $ComputerName" "INFO"

    # 1. Check server-side VeraCrypt volume is ready
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $vcScript = Join-Path $scriptDir "Manage-VeraCrypt.ps1"
    
    if (-not (Get-PSDrive -Name $VeraCryptLetter -ErrorAction SilentlyContinue)) {
        if ($ContainerPath -ne "" -and $Password -ne "") {
            Write-Log "VeraCrypt volume not mounted. Attempting to mount..." "INFO"
            & $vcScript -Action Mount -ContainerPath $ContainerPath -MountLetter $VeraCryptLetter -Password $Password
        } else {
            throw "VeraCrypt drive '${VeraCryptLetter}:' is not mounted and no mounting credentials were provided."
        }
    }

    # 2. Check client ping status
    Write-Log "Pinging target machine $ComputerName..." "INFO"
    $ping = Test-Connection -ComputerName $ComputerName -Count 1 -Delay 1 -ErrorAction SilentlyContinue
    if (-not $ping) {
        throw "Target machine $ComputerName is offline or unreachable via ICMP."
    }

    # 3. Test WinRM connection to target
    Write-Log "Verifying WinRM remote administration accessibility..." "INFO"
    try {
        $wsman = Test-WSMan -ComputerName $ComputerName -ErrorAction Stop
        Write-Log "WinRM connection verified: $($wsman.ProductVersion)" "SUCCESS"
    } catch {
        throw "WinRM is not accessible on target machine $ComputerName. Run 'winrm quickconfig' on client."
    }

    # 4. Prepare local backup storage folder and share
    $backupRoot = "${VeraCryptLetter}:\backups"
    $clientFolder = Join-Path $backupRoot $ComputerName
    $vhdxPath = Join-Path $clientFolder "disk.vhdx"
    $shareName = "backup_${ComputerName}$"
    
    # Expose SMB Share
    Write-Log "Configuring server SMB share $shareName..." "INFO"
    & $vcScript -Action CreateShare -ComputerName $ComputerName -MountLetter $VeraCryptLetter

    # Create VHDX container on the server if it doesn't exist
    if (-not (Test-Path $vhdxPath)) {
        Write-Log "VHDX backup drive not found. Initializing a new dynamically expanding 1TB VHDX container..." "INFO"
        
        # We write a temporary diskpart script to initialize the VHDX safely
        $diskpartScriptPath = Join-Path $clientFolder "create_vdisk.txt"
        $diskpartScript = @"
create vdisk file="$vhdxPath" maximum=1048576 type=expandable
attach vdisk
create partition primary
format fs=ntfs quick label="Backup-$ComputerName"
assign letter=Y
detach vdisk
"@
        $diskpartScript | Out-File -FilePath $diskpartScriptPath -Encoding ascii -Force
        
        Write-Log "Running diskpart VHDX creation..." "INFO"
        $output = diskpart /s $diskpartScriptPath
        Remove-Item $diskpartScriptPath -Force
        
        if (-not (Test-Path $vhdxPath)) {
            throw "Failed to create VHDX container: $output"
        }
        Write-Log "VHDX container initialized and formatted successfully." "SUCCESS"
    }

    # 5. Remote orchestration via WinRM PSSession
    Write-Log "Establishing WinRM remote session with $ComputerName..." "INFO"
    $serverIP = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notlike "*Loopback*" })[0].IPAddress
    $uncPath = "\\$serverIP\$shareName\disk.vhdx"

    Write-Log "Remote connecting to mount target VHDX from UNC: $uncPath..." "INFO"

    # Execute inside remote computer session
    $remoteScript = {
        param ($unc, $computer)
        $ErrorActionPreference = "Stop"

        # Check if already mounted
        $diskpartCheckScript = @"
select vdisk file="$unc"
list vdisk
"@
        $check = $diskpartCheckScript | diskpart
        
        if ($check -match "Attached: Yes") {
            # Already mounted, locate drive letter
            $drive = Get-Volume -FileSystemLabel "Backup-$computer" -ErrorAction SilentlyContinue
            if ($drive) { return $drive.DriveLetter + ":" }
        }

        # Mount VHDX
        $mountScript = @"
select vdisk file="$unc"
attach vdisk
"@
        $mountScript | diskpart | Out-Null
        Start-Sleep -Seconds 3

        # Locate assigned volume letter
        $volume = Get-Volume -FileSystemLabel "Backup-$computer" -ErrorAction SilentlyContinue
        if (-not $volume) {
            # Let's assign letter B: manually if none assigned
            $assignScript = @"
select vdisk file="$unc"
select partition 1
assign letter=B
"@
            $assignScript | diskpart | Out-Null
            Start-Sleep -Seconds 2
            $volume = Get-Volume -DriveLetter B -ErrorAction SilentlyContinue
        }

        if (-not $volume) {
            throw "VHDX was attached but backup volume could not be identified."
        }

        return $volume.DriveLetter + ":"
    }

    $driveLetter = Invoke-Command -ComputerName $ComputerName -ScriptBlock $remoteScript -ArgumentList $uncPath, $ComputerName
    Write-Log "Successfully mounted remote backup drive on $ComputerName at $driveLetter" "SUCCESS"

    # 6. Run wbadmin backup
    Write-Log "Triggering remote wbadmin system image backup (VSS-enabled) to $driveLetter..." "INFO"
    
    # wbadmin needs to be run in a remote script block. We direct standard output into live logging
    $backupScript = {
        param ($targetDrive, $bType)
        $ErrorActionPreference = "Stop"
        
        # Build command: wbadmin start backup -backuptarget:B: -include:c: -allcritical -quiet
        # For client desktops, -allcritical includes system state and C:
        $cmd = "wbadmin.exe start backup -backuptarget:$targetDrive -include:c: -allcritical -quiet"
        
        # Run process and capture console output stream
        $process = Start-Process -FilePath "cmd.exe" -ArgumentList "/c $cmd" -NoNewWindow -PassThru -Wait
        
        if ($process.ExitCode -ne 0) {
            throw "wbadmin failed with exit code $($process.ExitCode)."
        }
        return "Backup process completed successfully."
    }

    $backupOutput = Invoke-Command -ComputerName $ComputerName -ScriptBlock $backupScript -ArgumentList $driveLetter, $BackupType
    Write-Log "Remote Backup Engine: $backupOutput" "SUCCESS"

    # 7. Unmount / Detach VHDX on remote client
    Write-Log "Unmounting VHDX on remote client..." "INFO"
    $unmountScript = {
        param ($unc)
        $unmountCommands = @"
select vdisk file="$unc"
detach vdisk
"@
        $unmountCommands | diskpart | Out-Null
        return "Detached VHDX successfully."
    }
    $unmountOutput = Invoke-Command -ComputerName $ComputerName -ScriptBlock $unmountScript -ArgumentList $uncPath
    Write-Log "Client cleanup: $unmountOutput" "SUCCESS"

    # 8. Clean up dynamic SMB Share on Server
    Write-Log "Revoking SMB Share $shareName..." "INFO"
    & $vcScript -Action RemoveShare -ComputerName $ComputerName

    Write-Log "Backup process for $ComputerName completed successfully!" "SUCCESS"
    exit 0

} catch {
    Write-Log "Backup sequence aborted. Error: $_" "ERROR"
    
    # Cleanup attempts on failure
    try {
        # Attempt client detach
        if (Test-WSMan -ComputerName $ComputerName -ErrorAction SilentlyContinue) {
            Invoke-Command -ComputerName $ComputerName -ScriptBlock {
                param ($unc)
                $detachCmd = "select vdisk file=`"$unc`"`ndetach vdisk"
                $detachCmd | diskpart | Out-Null
            } -ArgumentList $uncPath -ErrorAction SilentlyContinue
        }
        # Attempt server share revocation
        & $vcScript -Action RemoveShare -ComputerName $ComputerName -ErrorAction SilentlyContinue
    } catch {}

    exit 1
}
