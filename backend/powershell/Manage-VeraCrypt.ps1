<#
.SYNOPSIS
    Manages the VeraCrypt backup vault lifecycle, volume mounting, and dynamically scoped SMB sharing.
.DESCRIPTION
    Integrates with the VeraCrypt CLI to secure the backup target directory.
    Exposes and revokes dynamic hidden network shares for agentless target machines.
.EXAMPLE
    .\Manage-VeraCrypt.ps1 -Action Mount -ContainerPath "D:\BackupVault.hc" -MountLetter "V" -Password "SecurePassword"
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [ValidateSet("Mount", "Dismount", "CreateShare", "RemoveShare", "CreateContainer")]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [string]$ContainerPath,

    [Parameter(Mandatory = $false)]
    [string]$MountLetter = "V",

    [Parameter(Mandatory = $false)]
    [string]$Password,

    [Parameter(Mandatory = $false)]
    [string]$ComputerName,

    [Parameter(Mandatory = $false)]
    [string]$ContainerSize = "10G"
)

# Standard VeraCrypt path installation defaults
$VeraCryptPaths = @(
    "${env:ProgramFiles}\VeraCrypt\VeraCrypt.exe",
    "${env:ProgramFiles(x86)}\VeraCrypt\VeraCrypt.exe",
    "C:\VeraCrypt\VeraCrypt.exe"
)

function Get-VeraCryptPath {
    foreach ($path in $VeraCryptPaths) {
        if (Test-Path $path) { return $path }
    }
    throw "VeraCrypt.exe not found. Please install VeraCrypt on the server."
}

function Is-Mounted {
    param ([string]$Letter)
    return (Get-PSDrive -Name $Letter -ErrorAction SilentlyContinue) -ne $null
}

switch ($Action) {
    "Mount" {
        if (-not $ContainerPath -or -not $Password) {
            Write-Error "ContainerPath and Password are required to mount."
            exit 1
        }
        if (Is-Mounted -Letter $MountLetter) {
            Write-Output "Volume is already mounted at ${MountLetter}:"
            exit 0
        }
        
        $vc = Get-VeraCryptPath
        $args = "/volume `"$ContainerPath`" /letter $MountLetter /password `"$Password`" /silent /quit /silent"
        
        Write-Output "Mounting VeraCrypt container: $ContainerPath to ${MountLetter}:..."
        $process = Start-Process -FilePath $vc -ArgumentList $args -PassThru -Wait -NoNewWindow
        
        if (Is-Mounted -Letter $MountLetter) {
            Write-Output "Successfully mounted at ${MountLetter}:"
        } else {
            Write-Error "Failed to mount volume. Check password or file integrity."
            exit 1
        }
    }

    "Dismount" {
        if (-not (Is-Mounted -Letter $MountLetter)) {
            Write-Output "Volume is not mounted at ${MountLetter}:"
            exit 0
        }
        
        $vc = Get-VeraCryptPath
        $args = "/dismount $MountLetter /silent /quit"
        
        Write-Output "Dismounting volume at ${MountLetter}:..."
        $process = Start-Process -FilePath $vc -ArgumentList $args -PassThru -Wait -NoNewWindow
        
        if (-not (Is-Mounted -Letter $MountLetter)) {
            Write-Output "Successfully dismounted ${MountLetter}:"
        } else {
            Write-Error "Failed to dismount ${MountLetter}:. Volume might be in use."
            exit 1
        }
    }

    "CreateShare" {
        if (-not $ComputerName) {
            Write-Error "ComputerName is required to expose target share."
            exit 1
        }
        if (-not (Is-Mounted -Letter $MountLetter)) {
            Write-Error "VeraCrypt volume is not mounted. Mount the volume first."
            exit 1
        }
        
        $sharePath = "${MountLetter}:\backups\$ComputerName"
        $shareName = "backup_${ComputerName}$" # Hidden share
        
        if (-not (Test-Path $sharePath)) {
            New-Item -ItemType Directory -Path $sharePath -Force | Out-Null
            Write-Output "Created NTFS target directory: $sharePath"
        }
        
        # Check if SMB Share already exists
        $existingShare = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
        if ($existingShare) {
            Write-Output "SMB Share $shareName already exists."
            exit 0
        }
        
        Write-Output "Creating SMB Share: $shareName -> $sharePath"
        # Grant access strictly to Domain Admins and the target computer account itself
        $computerPrincipal = "$env:USERDOMAIN\$ComputerName$"
        New-SmbShare -Name $shareName -Path $sharePath -FullAccess "Domain Admins", $computerPrincipal -Description "Agentless backup endpoint for $ComputerName" | Out-Null
        
        Write-Output "SMB Share $shareName created successfully."
    }

    "RemoveShare" {
        if (-not $ComputerName) {
            Write-Error "ComputerName is required to remove dynamic share."
            exit 1
        }
        
        $shareName = "backup_${ComputerName}$"
        $existingShare = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
        
        if (-not $existingShare) {
            Write-Output "SMB Share $shareName does not exist."
            exit 0
        }
        
        Write-Output "Removing SMB Share: $shareName"
        Remove-SmbShare -Name $shareName -Force | Out-Null
        Write-Output "SMB Share $shareName removed successfully."
    }

    "CreateContainer" {
        if (-not $ContainerPath -or -not $Password) {
            Write-Error "ContainerPath and Password are required to create a container."
            exit 1
        }
        
        if (Test-Path $ContainerPath) {
            Write-Error "A file already exists at $ContainerPath"
            exit 1
        }
        
        $vc = Get-VeraCryptPath
        # Command line container creation flags:
        # /create: file target
        # /size: size of the container (e.g. 10G)
        # /password: secure password
        # /hash: SHA-512
        # /encryption: AES
        # /filesystem: NTFS
        $args = "/create `"$ContainerPath`" /size $ContainerSize /password `"$Password`" /hash sha512 /encryption AES /filesystem NTFS /force /silent"
        
        Write-Output "Creating new VeraCrypt container ($ContainerSize) at $ContainerPath..."
        $process = Start-Process -FilePath $vc -ArgumentList $args -PassThru -Wait -NoNewWindow
        
        if (Test-Path $ContainerPath) {
            Write-Output "VeraCrypt container created successfully at $ContainerPath"
        } else {
            Write-Error "Failed to create VeraCrypt container."
            exit 1
        }
    }
}
