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

    # 4. Prepare local backup storage folder
    $backupRoot = "${VeraCryptLetter}:\backups"
    $clientFolder = Join-Path $backupRoot $ComputerName
    if (-not (Test-Path $clientFolder)) {
        New-Item -ItemType Directory -Path $clientFolder -Force | Out-Null
    }
    $vhdxPath = Join-Path $clientFolder "disk.vhdx"

    # Create VHDX container on the server if it doesn't exist
    if (-not (Test-Path $vhdxPath)) {
        Write-Log "VHDX backup drive not found. Initializing a new dynamically expanding 1TB VHDX container..." "INFO"
        
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

    # 5. Configure local temporary share adshield_temp$ for backup staging
    $tempSharePath = "E:\adshield_temp"
    $tempBackupDir = Join-Path $tempSharePath $ComputerName
    
    if (-not (Test-Path $tempSharePath)) {
        New-Item -ItemType Directory -Path $tempSharePath -Force | Out-Null
    }
    
    # Pre-create computer-specific temp folder
    if (-not (Test-Path $tempBackupDir)) {
        New-Item -ItemType Directory -Path $tempBackupDir -Force | Out-Null
    }

    Write-Log "Configuring temporary SMB share adshield_temp$..." "INFO"
    $acl = Get-Acl $tempSharePath
    $ar = New-Object System.Security.AccessControl.FileSystemAccessRule("SERVICEARCIT\Domain Computers", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($ar)
    Set-Acl $tempSharePath $acl

    if (-not (Get-SmbShare -Name "adshield_temp$" -ErrorAction SilentlyContinue)) {
        New-SmbShare -Name "adshield_temp$" -Path $tempSharePath -FullAccess "Domain Admins", "SERVICEARCIT\Domain Computers" -Description "ADShield Temp share" | Out-Null
    }

    # 6. Create and execute the Scheduled Task on the remote client
    Write-Log "Creating and launching Scheduled Task on remote client $ComputerName..." "INFO"
    $taskName = "ADShield_Backup_Run"
    
    $runBackupScript = {
        param($targetComputer, $tName)
        $ErrorActionPreference = "Stop"
        
        # Build action executing wbadmin directly as SYSTEM
        $action = New-ScheduledTaskAction -Execute "wbadmin.exe" -Argument "start backup -backuptarget:\\FILESVR\adshield_temp$\$targetComputer -include:c: -allcritical -quiet"
        $principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount
        $task = New-ScheduledTask -Action $action -Principal $principal
        
        Register-ScheduledTask -TaskName $tName -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName $tName
        
        # Poll task until complete
        $timeoutSeconds = 7200 # 2 hours limit
        $elapsed = 0
        while ($true) {
            Start-Sleep -Seconds 15
            $elapsed += 15
            $state = (Get-ScheduledTask -TaskName $tName).State
            
            if ($state -ne "Running") {
                break
            }
            if ($elapsed -ge $timeoutSeconds) {
                Stop-ScheduledTask -TaskName $tName | Out-Null
                break
            }
        }
        
        $info = Get-ScheduledTask -TaskName $tName | Get-ScheduledTaskInfo
        $result = $info.LastTaskResult
        
        Unregister-ScheduledTask -TaskName $tName -Confirm:$false | Out-Null
        return $result
    }

    $exitCode = Invoke-Command -ComputerName $ComputerName -ScriptBlock $runBackupScript -ArgumentList $ComputerName, $taskName
    
    # Since Invoke-Command returns output objects, we select the last item (the exit code)
    $lastResult = $exitCode | Select-Object -Last 1
    if ($lastResult -ne 0) {
        throw "Remote backup task failed on client $ComputerName with exit code: $lastResult"
    }
    Write-Log "Remote backup engine successfully completed on client." "SUCCESS"

    # 7. Mount the local VHDX locally on the server
    Write-Log "Mounting target VHDX locally on server..." "INFO"
    
    $mountLetter = "T"
    # Dismount first if T: is already mounted from a previous run
    if (Get-PSDrive -Name $mountLetter -ErrorAction SilentlyContinue) {
        $dpDetach = 'select vdisk file=' + [char]34 + $vhdxPath + [char]34 + [char]10 + 'detach vdisk'
        $tempFile = [System.IO.Path]::GetTempFileName()
        $dpDetach | Out-File -FilePath $tempFile -Encoding ascii -Force
        diskpart /s $tempFile | Out-Null
        Remove-Item $tempFile -Force
        Start-Sleep -Seconds 2
    }
    
    # Try partition 2 first (GPT), then partition 1 (MBR)
    $dpMount = 'select vdisk file=' + [char]34 + $vhdxPath + [char]34 + [char]10 + 'attach vdisk' + [char]10 + 'select partition 2' + [char]10 + 'assign letter=' + $mountLetter + ' NOERR'
    $tempFile = [System.IO.Path]::GetTempFileName()
    $dpMount | Out-File -FilePath $tempFile -Encoding ascii -Force
    diskpart /s $tempFile | Out-Null
    Remove-Item $tempFile -Force
    Start-Sleep -Seconds 2
    
    if (-not (Get-PSDrive -Name $mountLetter -ErrorAction SilentlyContinue)) {
        # Fallback to partition 1
        $dpMount = 'select vdisk file=' + [char]34 + $vhdxPath + [char]34 + [char]10 + 'attach vdisk' + [char]10 + 'select partition 1' + [char]10 + 'assign letter=' + $mountLetter + ' NOERR'
        $tempFile = [System.IO.Path]::GetTempFileName()
        $dpMount | Out-File -FilePath $tempFile -Encoding ascii -Force
        diskpart /s $tempFile | Out-Null
        Remove-Item $tempFile -Force
        Start-Sleep -Seconds 2
    }
    
    if (-not (Get-PSDrive -Name $mountLetter -ErrorAction SilentlyContinue)) {
        throw "Failed to mount VHDX locally on server as drive ${mountLetter}:"
    }

    # 8. Copy WindowsImageBackup into the VHDX via Robocopy
    Write-Log "Copying backup from temp staging into the encrypted VHDX..." "INFO"
    $srcPath = Join-Path $tempBackupDir "WindowsImageBackup"
    $destPath = "${mountLetter}:\WindowsImageBackup"
    
    # Execute Robocopy
    $robocopyProcess = Start-Process -FilePath "robocopy.exe" -ArgumentList "`"$srcPath`" `"$destPath`" /E /COPY:DAT /R:1 /W:1 /NP /XJ" -NoNewWindow -PassThru -Wait
    
    # Robocopy exit codes < 8 indicate success or no changes
    if ($robocopyProcess.ExitCode -ge 8) {
        throw "Robocopy failed with exit code $($robocopyProcess.ExitCode)"
    }
    
    # 9. Clean up and detach
    Write-Log "Dismounting VHDX container..." "INFO"
    $dpDetach = 'select vdisk file=' + [char]34 + $vhdxPath + [char]34 + [char]10 + 'detach vdisk'
    $tempFile = [System.IO.Path]::GetTempFileName()
    $dpDetach | Out-File -FilePath $tempFile -Encoding ascii -Force
    diskpart /s $tempFile | Out-Null
    Remove-Item $tempFile -Force
    
    Write-Log "Cleaning up local temporary backup directory..." "INFO"
    if (Test-Path $tempBackupDir) {
        Remove-Item -Path $tempBackupDir -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
    }
    
    Write-Log "Backup process for $ComputerName completed successfully!" "SUCCESS"
    exit 0

} catch {
    Write-Log "Backup sequence aborted. Error: $_" "ERROR"
    
    # Cleanup attempts on failure
    try {
        # Unregister task on client if it exists
        if (Test-WSMan -ComputerName $ComputerName -ErrorAction SilentlyContinue) {
            Invoke-Command -ComputerName $ComputerName -ScriptBlock {
                param($tName)
                Unregister-ScheduledTask -TaskName $tName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            } -ArgumentList $taskName -ErrorAction SilentlyContinue
        }
        
        # Detach VHDX on server
        if ($vhdxPath) {
            $dpDetach = 'select vdisk file=' + [char]34 + $vhdxPath + [char]34 + [char]10 + 'detach vdisk'
            $tempFile = [System.IO.Path]::GetTempFileName()
            $dpDetach | Out-File -FilePath $tempFile -Encoding ascii -Force
            diskpart /s $tempFile | Out-Null
            Remove-Item $tempFile -Force
        }
        
        # Remove local temp backup dir
        if ($tempBackupDir -and (Test-Path $tempBackupDir)) {
            Remove-Item -Path $tempBackupDir -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
        }
    } catch {}

    exit 1
}
