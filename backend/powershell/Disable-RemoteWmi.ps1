# Disable-RemoteWmi.ps1
# Run this script as Administrator locally on the target client machine (e.g. MASONP-DESKTOP)
# once the backup is completed to restore the settings to their original secure states.

$ErrorActionPreference = "Stop"

Write-Host "Reverting and locking down Remote WMI/RPC access settings..." -ForegroundColor Cyan

# 1. Disable Windows Firewall rules for WMI and Remote Administration
Write-Host "Disabling Firewall exceptions for WMI and Remote Administration..." -ForegroundColor Gray
Disable-NetFirewallRule -DisplayGroup "Windows Management Instrumentation (WMI)" -ErrorAction SilentlyContinue
Disable-NetFirewallRule -DisplayGroup "Remote Administration" -ErrorAction SilentlyContinue

# 2. Revert any Network Profiles that were temporarily changed to Private back to Public
$tempFile = "$env:TEMP\ADShield_ChangedProfiles.txt"
if (Test-Path $tempFile) {
    $changedProfiles = Get-Content -Path $tempFile
    foreach ($name in $changedProfiles) {
        if ($name) {
            Write-Host "Reverting Network Profile '$name' from Private back to Public..." -ForegroundColor Yellow
            Set-NetConnectionProfile -Name $name -NetworkCategory Public -ErrorAction SilentlyContinue
        }
    }
    Remove-Item -Path $tempFile -ErrorAction SilentlyContinue
} else {
    Write-Host "No record of modified network profiles found. Skipping profile revert." -ForegroundColor Gray
}

Write-Host "Security configuration restored successfully. Remote WMI is now blocked." -ForegroundColor Green
