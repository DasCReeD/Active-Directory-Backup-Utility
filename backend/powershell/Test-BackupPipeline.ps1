[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$ComputerName
)

$ErrorActionPreference = "Stop"

Write-Output "=== Start Scheduled-Task Backup Feasibility Test ==="
Write-Output "Target Computer: $ComputerName"

# Step 1: Ping target
Write-Output "1. Pinging target..."
if (-not (Test-Connection -ComputerName $ComputerName -Count 1 -Delay 1 -Quiet)) {
    Write-Error "Target $ComputerName is offline."
    exit 1
}
Write-Output "   Ping OK."

# Step 2: Test WinRM connectivity
Write-Output "2. Testing WinRM connectivity..."
try {
    $wsman = Test-WSMan -ComputerName $ComputerName
    Write-Output "   WinRM OK: $($wsman.ProductVersion)"
} catch {
    Write-Error "WinRM not accessible: $_"
    exit 1
}

# Step 3: Configure adshield_temp$ share on FILESVR
Write-Output "3. Configuring temp share on FILESVR..."
$configureShareScript = {
    $path = "E:\adshield_temp"
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    
    # Set NTFS permission for Domain Computers
    $acl = Get-Acl $path
    $ar = New-Object System.Security.AccessControl.FileSystemAccessRule("SERVICEARCIT\Domain Computers", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($ar)
    Set-Acl $path $acl
    
    # Create SMB Share
    if (-not (Get-SmbShare -Name "adshield_temp$" -ErrorAction SilentlyContinue)) {
        New-SmbShare -Name "adshield_temp$" -Path $path -FullAccess "Domain Admins", "SERVICEARCIT\Domain Computers" -Description "ADShield Temp share" | Out-Null
    }
    Write-Output "   [FILESVR] Share adshield_temp$ is ready."
}
Invoke-Command -ComputerName FILESVR -ScriptBlock $configureShareScript

# Step 4: Ensure Windows Backup feature is installed on client
Write-Output "4. Ensuring Windows Backup feature is installed on $ComputerName..."
$installFeatureScript = {
    if (Get-Command wbadmin.exe -ErrorAction SilentlyContinue) {
        Write-Output "   [Remote] wbadmin.exe is already available. Skipping install."
        return
    }
    Write-Output "   [Remote] wbadmin.exe is missing. Attempting to install Windows Backup capability/feature..."
    if (Get-Command Get-WindowsCapability -ErrorAction SilentlyContinue) {
        $cap = Get-WindowsCapability -Online -Name Backup.Client* | Where-Object {$_.State -ne 'Installed'}
        if ($cap) {
            Write-Output "   [Remote] Installing Backup Client capability..."
            Add-WindowsCapability -Online -Name $cap.Name | Out-Null
        }
    } elseif (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
        $feature = Get-WindowsFeature -Name Windows-Server-Backup
        if ($feature.InstallState -ne 'Installed') {
            Write-Output "   [Remote] Installing Windows-Server-Backup feature..."
            Install-WindowsFeature -Name Windows-Server-Backup -IncludeManagementTools | Out-Null
        }
    }
    
    if (-not (Get-Command wbadmin.exe -ErrorAction SilentlyContinue)) {
        throw "Failed to install Windows Backup capability/feature. wbadmin.exe is still missing."
    }
}
Invoke-Command -ComputerName $ComputerName -ScriptBlock $installFeatureScript

# Step 5: Pre-create backup folder on fileserver
Write-Output "5. Pre-creating target backup directory on fileserver..."
$backupDir = "E:\adshield_temp\$ComputerName"
$preCreateScript = {
    param($dir)
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Output "   [FILESVR] Created target folder: $dir"
    } else {
        Write-Output "   [FILESVR] Target folder already exists: $dir"
    }
}
Invoke-Command -ComputerName FILESVR -ScriptBlock $preCreateScript -ArgumentList $backupDir

# Step 6: Create and execute the Scheduled Task on target client
Write-Output "6. Creating and launching Scheduled Task on $ComputerName..."
$runBackupScript = {
    param($targetComputer)
    $taskName = "ADShield_Backup_Test"
    
    # We execute wbadmin.exe directly as SYSTEM
    $action = New-ScheduledTaskAction -Execute "wbadmin.exe" -Argument "start backup -backuptarget:\\filesvr\adshield_temp$\$targetComputer -include:c: -allcritical -quiet"
    $principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount
    $task = New-ScheduledTask -Action $action -Principal $principal
    
    # Register task
    Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
    Write-Host "   [Remote] Task registered. Starting task..."
    
    Start-ScheduledTask -TaskName $taskName
    
    # Poll task until complete
    $timeoutSeconds = 1800 # 30 minutes limit for test
    $elapsed = 0
    while ($true) {
        Start-Sleep -Seconds 10
        $elapsed += 10
        $state = (Get-ScheduledTask -TaskName $taskName).State
        Write-Host "   [Remote] Task State: $state ($elapsed seconds elapsed)..."
        
        if ($state -ne "Running") {
            break
        }
        
        if ($elapsed -ge $timeoutSeconds) {
            Write-Host "   [Remote] Timeout reached. Stopping task..."
            Stop-ScheduledTask -TaskName $taskName | Out-Null
            break
        }
    }
    
    # Get exit status
    $info = Get-ScheduledTask -TaskName $taskName | Get-ScheduledTaskInfo
    $result = $info.LastTaskResult
    Write-Host "   [Remote] Task finished with Exit Code (LastTaskResult): $result"
    
    # Unregister task
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false | Out-Null
    return $result
}

try {
    $exitCode = Invoke-Command -ComputerName $ComputerName -ScriptBlock $runBackupScript -ArgumentList $ComputerName
    if ($exitCode -ne 0) {
        Write-Error "Backup task failed on remote client with exit code $exitCode."
        exit 1
    }
    Write-Output "Scheduled Task backup: PASSED"
} catch {
    Write-Error "Scheduled Task backup failed: $_"
    exit 1
}

# Step 7: Local VHDX copy check
Write-Output "7. Verifying local copy to VHDX..."
$mountLetter = "V"
if (-not (Get-PSDrive -Name $mountLetter -ErrorAction SilentlyContinue)) {
    Write-Output "   VeraCrypt drive ${mountLetter}: is not mounted."
    Write-Output "   Skipping VHDX copy phase. To fully complete, mount VeraCrypt container to ${mountLetter}:."
    Write-Output "=== Feasibility Test COMPLETED (Backup Successful, VHDX Skip) ==="
    exit 0
}

# Mount target VHDX locally on the backup server
$testVhdxDir = "${mountLetter}:\backups\${ComputerName}"
if (-not (Test-Path $testVhdxDir)) {
    New-Item -ItemType Directory -Path $testVhdxDir -Force | Out-Null
}
$testVhdxPath = Join-Path $testVhdxDir "disk.vhdx"

Write-Output "   Mounting target VHDX..."
$dpMountScript = @"
select vdisk file="$testVhdxPath"
attach vdisk
select partition 2
assign letter=T NOERR
"@
$tempDpFile = [System.IO.Path]::GetTempFileName()
$dpMountScript | Out-File -FilePath $tempDpFile -Encoding ascii -Force
diskpart /s $tempDpFile | Out-Null
Remove-Item $tempDpFile -Force

if (-not (Get-PSDrive -Name "T" -ErrorAction SilentlyContinue)) {
    Write-Error "Failed to mount VHDX locally as drive T:"
    exit 1
}

# Copy the backup from fileserver into VHDX
Write-Output "   Copying WindowsImageBackup from fileserver into VHDX..."
$srcPath = "\\filesvr\adshield_temp$\$ComputerName\WindowsImageBackup"
$destPath = "T:\WindowsImageBackup"

# Run Robocopy
robocopy $srcPath $destPath /E /COPY:DAT /R:1 /W:1 /NP /XJ

# Verify copy
if (Test-Path $destPath) {
    Write-Output "   Copy verified: WindowsImageBackup copied into VHDX successfully!"
} else {
    Write-Error "Failed to copy WindowsImageBackup into VHDX."
}

# Unmount VHDX
Write-Output "   Unmounting VHDX..."
$dpDetach = @"
select vdisk file="$testVhdxPath"
detach vdisk
"@
$tempDpFile = [System.IO.Path]::GetTempFileName()
$dpDetach | Out-File -FilePath $tempDpFile -Encoding ascii -Force
diskpart /s $tempDpFile | Out-Null
Remove-Item $tempDpFile -Force

# Clean up temporary test files on fileserver
Write-Output "   Cleaning up fileserver temp folder..."
$cleanupScript = {
    param($dir)
    if (Test-Path $dir) {
        Remove-Item -Path $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Invoke-Command -ComputerName FILESVR -ScriptBlock $cleanupScript -ArgumentList $backupDir

Write-Output "=== Feasibility Test Completed Successfully! ==="
