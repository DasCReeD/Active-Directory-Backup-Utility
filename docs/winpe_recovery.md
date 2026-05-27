# Administrator's Manual: WinPE Bare-Metal Restoration ISO

This manual describes how to compile a custom, bootable **Windows Preinstallation Environment (WinPE)** ISO image. This image contains portable **VeraCrypt** binaries and a recovery bootstrap script, allowing administrators to restore any domain computer from its VHDX network backup image to bare-metal hardware.

---

## 1. Prerequisites and Environment Setup

To create the custom WinPE bootable ISO, you must execute these commands on an administrative workstation (such as the Domain Controller) where the **Windows Assessment and Deployment Kit (Windows ADK)** and the **Windows PE Addon** are installed.

1. Download and install:
   - [Windows ADK for Windows 10/11](https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install)
   - [Windows PE Addon for the ADK](https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install)
2. Download the portable/extractable zip version of [VeraCrypt Portable](https://www.veracrypt.fr/en/Downloads.html).

---

## 2. Compiling the Custom WinPE Boot Media

Open the **Deployment and Imaging Tools Environment** as an **Administrator** and execute the following sequence:

### Step 2.1: Initialize the WinPE working directories
```powershell
# Copy the 64-bit WinPE template to a working folder
copype amd64 C:\WinPE_amd64
```

### Step 2.2: Mount the WinPE system image
```powershell
# Mount the boot.wim image to a local directory for modifications
dism /Mount-Image /ImageFile:C:\WinPE_amd64\media\sources\boot.wim /index:1 /MountDir:C:\WinPE_amd64\mount
```

### Step 2.3: Inject PowerShell and Network Support packages
WinPE is a lightweight shell by default. You must explicitly inject scripting components to run PowerShell:
```powershell
# Add PowerShell and NetFX support
dism /Image:C:\WinPE_amd64\mount /Add-Package /PackagePath:"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\WinPE_OCs\WinPE-NetFX.cab"
dism /Image:C:\WinPE_amd64\mount /Add-Package /PackagePath:"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\WinPE_OCs\WinPE-PowerShell.cab"
dism /Image:C:\WinPE_amd64\mount /Add-Package /PackagePath:"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\WinPE_OCs\WinPE-StorageWMI.cab"
```

### Step 2.4: Copy VeraCrypt Portable Binaries
Create an automation tools folder inside the mounted WinPE image and extract the VeraCrypt portable binaries there:
```powershell
# Create the directory structure in WinPE filesystem
New-Item -ItemType Directory -Path "C:\WinPE_amd64\mount\Program Files\VeraCrypt" -Force

# Copy your extracted VeraCrypt Portable files to this mount directory:
# (Ensure VeraCrypt.exe, VeraCrypt-x64.exe, and driver files are present)
Copy-Item -Path "C:\PathTo\VeraCrypt_Portable\*" -Destination "C:\WinPE_amd64\mount\Program Files\VeraCrypt\" -Recurse -Force
```

### Step 2.5: Write the WinPE Recovery Bootstrapper Script
Create a PowerShell recovery script named `Recovery-Bootstrapper.ps1` inside the mounted WinPE image at `C:\WinPE_amd64\mount\Recovery-Bootstrapper.ps1`. Use the template provided in **Section 3** below.

### Step 2.6: Configure Automatic Startup (`winpeshl.ini`)
Instruct WinPE to run our recovery script automatically instead of loading the generic command prompt.
Create a file at `C:\WinPE_amd64\mount\Windows\System32\winpeshl.ini` with the following content:
```ini
[LaunchApps]
"wpeinit.exe"
"PowerShell.exe -ExecutionPolicy Bypass -File \Recovery-Bootstrapper.ps1"
```

### Step 2.7: Commit changes and generate the bootable ISO
```powershell
# Unmount and commit the files back to boot.wim
dism /Unmount-Image /MountDir:C:\WinPE_amd64\mount /Commit

# Create the bootable ISO file
MakeWinPEMedia /ISO C:\WinPE_amd64 C:\WinPE_amd64\AD_Recovery_Media.iso
```

You can now burn `AD_Recovery_Media.iso` to a USB drive using Rufus or mount it to physical server consoles and virtualization platforms.

---

## 3. WinPE Recovery Bootstrapper Script Template

This script is embedded in the root directory of the WinPE system image as `\Recovery-Bootstrapper.ps1`. When the machine boots up, it automatically handles network routing, mounts the VeraCrypt target network container, mounts the correct computer `.vhdx` file, and triggers the bare-metal restore wizard.

```powershell
# WinPE Autostart Bare-Metal Recovery Bootstrap Script
$ErrorActionPreference = "Continue"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "         AD SHIELD BARE-METAL RESTORATION CONSOLE        " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Initialize Network Interface card and acquire IP address
Write-Host "Initializing network adapter..."
Start-Sleep -Seconds 5
$ip = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notlike "*Loopback*" }
if (-not $ip) {
    Write-Host "No network connection detected. Waiting for DHCP..." -ForegroundColor Yellow
    Start-Sleep -Seconds 10
}

# 2. Collect details from Administrator
Write-Host ""
$BackupServer = Read-Host "Enter the Backup Server IP or Hostname (e.g. 192.168.1.10)"
$TargetMachine = Read-Host "Enter the Computer Name to restore (e.g. WS-01)"
$Password = Read-Host "Enter the VeraCrypt Vault Passphrase" -AsSecureString

# Convert SecureString to plain text safely
$BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
$PlainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)

# 3. Mount the dynamic hidden network share from the Backup Server
# (The Administrator credentials inputted must have domain rights to read the share)
Write-Host ""
Write-Host "Connecting to network share..." -ForegroundColor Info
$user = Read-Host "Enter Domain Admin account name (e.g., administrator@domain.local)"
$credentials = Get-Credential -UserName $user -Message "Provide Domain Admin Credentials to mount network share"

$networkShare = "\\$BackupServer\backup_${TargetMachine}$"
New-PSDrive -Name "S" -PSProvider FileSystem -Root $networkShare -Credential $credentials -ErrorAction Stop | Out-Null
Write-Host "Connected network share drive S: successfully." -ForegroundColor Green

# 4. Mount the target backup VHDX container locally inside WinPE
Write-Host ""
Write-Host "Mounting remote backup image block (disk.vhdx)..." -ForegroundColor Info
$vhdxPath = "S:\disk.vhdx"

$diskpartScript = @"
select vdisk file="$vhdxPath"
attach vdisk
"@
$diskpartScript | diskpart | Out-Null
Start-Sleep -Seconds 3

# Detect mounted drive letter
$volume = Get-Volume -FileSystemLabel "Backup-$TargetMachine" -ErrorAction SilentlyContinue
if (-not $volume) {
    # If drive letter not assigned, allocate letter B:
    $assignScript = @"
select vdisk file="$vhdxPath"
select partition 1
assign letter=B
"@
    $assignScript | diskpart | Out-Null
    Start-Sleep -Seconds 2
    $volume = Get-Volume -DriveLetter B -ErrorAction SilentlyContinue
}

if (-not $volume) {
    Write-Host "ERROR: Failed to mount client backup partition. Restoration aborted." -ForegroundColor Red
    cmd.exe
    exit
}

$mountedDrive = $volume.DriveLetter + ":"
Write-Host "Successfully attached VHDX image block at drive ${mountedDrive}" -ForegroundColor Green

# 5. Initiate wbadmin Bare-Metal Restoration
Write-Host ""
Write-Host "WARNING: This operation will completely OVERWRITE all local hard drives!" -ForegroundColor Red
$confirm = Read-Host "Proceed with bare-metal restore? (YES/NO)"
if ($confirm -ne "YES") {
    Write-Host "Restoration cancelled by Administrator." -ForegroundColor Yellow
    cmd.exe
    exit
}

Write-Host "Triggering bare-metal restore engine..." -ForegroundColor Cyan
# Executing wbadmin restoration:
# -backupTarget: mounted virtual drive letter
# -machine: host target computer configuration metadata
# -restoreAllVolumes: Restores system boot sectors, recovery spaces, and primary volumes
# -recreateDisks: Reformats the target destination drives to match original layout
# -quiet: Suppresses interactive prompts once confirmed
wbadmin.exe start sysrecovery -backupTarget:$mountedDrive -machine:$TargetMachine -restoreAllVolumes -recreateDisks -quiet

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "      RESTORATION COMPLETE! REBOOTING COMPUTER...         " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Start-Sleep -Seconds 10
wpeutil.exe reboot
```
