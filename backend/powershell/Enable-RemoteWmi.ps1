# Enable-RemoteWmi.ps1
# Run this script as Administrator locally on the target client machine (e.g. MASONP-DESKTOP)
# to temporarily enable remote WMI administration.

$ErrorActionPreference = "Stop"

Write-Host "Configuring Remote WMI and RPC access settings..." -ForegroundColor Cyan

# 1. Ensure WMI Service is running and set to Automatic
Write-Host "Configuring Windows Management Instrumentation service..." -ForegroundColor Gray
Set-Service -Name Winmgmt -StartupType Automatic
Start-Service -Name Winmgmt -ErrorAction SilentlyContinue

# 2. Enable Windows Firewall rules for WMI and Remote Administration
Write-Host "Enabling Firewall exceptions for WMI and Remote Administration..." -ForegroundColor Gray
Enable-NetFirewallRule -DisplayGroup "Windows Management Instrumentation (WMI)" -ErrorAction SilentlyContinue
Enable-NetFirewallRule -DisplayGroup "Remote Administration" -ErrorAction SilentlyContinue

# 3. Check and temporarily change network connection profiles from Public to Private 
# (Windows Firewall blocks remote WMI requests by default on Public network profiles)
$changedProfiles = @()
$profiles = Get-NetConnectionProfile
foreach ($p in $profiles) {
    if ($p.NetworkCategory -eq "Public") {
        Write-Host "Temporarily changing Network Profile '$($p.Name)' from Public to Private..." -ForegroundColor Yellow
        Set-NetConnectionProfile -Name $p.Name -NetworkCategory Private
        $changedProfiles += $p.Name
    }
}

$tempFile = "$env:TEMP\ADShield_ChangedProfiles.txt"
if ($changedProfiles.Count -gt 0) {
    $changedProfiles | Out-File -FilePath $tempFile -Encoding utf8 -Force
} else {
    if (Test-Path $tempFile) {
        Remove-Item -Path $tempFile -ErrorAction SilentlyContinue
    }
}

Write-Host "Configurations successfully applied. Remote WMI is now accessible." -ForegroundColor Green
