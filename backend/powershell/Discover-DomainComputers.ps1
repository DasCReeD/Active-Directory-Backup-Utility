<#
.SYNOPSIS
    Queries Active Directory for computer accounts using lightweight LDAP searchers,
    applies configurable filters (OUs, groups), and runs network availability checks.
.DESCRIPTION
    Outputs discovered computer metadata in JSON format for consumption by the Web UI.
.EXAMPLE
    .\Discover-DomainComputers.ps1 -GroupName "Backup-Targets" -PingCheck $true
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [string]$SearchOU = "", # e.g., "OU=Workstations,DC=domain,DC=local"

    [Parameter(Mandatory = $false)]
    [string]$GroupName = "", # Filter by specific security group (e.g., "Backup-Targets")

    [Parameter(Mandatory = $false)]
    [bool]$PingCheck = $true
)

try {
    # Establish Active Directory root connection
    $domain = [System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain()
    $domainDN = ($domain.GetDirectoryEntry()).distinguishedName
    
    $searchRootPath = "LDAP://"
    if ($SearchOU -ne "") {
        $searchRootPath += $SearchOU
    } else {
        $searchRootPath += $domainDN
    }
    
    $searchRoot = New-Object System.DirectoryServices.DirectoryEntry($searchRootPath)
    $searcher = New-Object System.DirectoryServices.DirectorySearcher($searchRoot)
    
    # Base filter: only computer accounts
    $filter = "(objectCategory=computer)"
    
    # Apply security group filter if specified
    if ($GroupName -ne "") {
        # First find the group's DistinguishedName
        $groupSearcher = New-Object System.DirectoryServices.DirectorySearcher($searchRoot)
        $groupSearcher.Filter = "(&(objectCategory=group)(cn=$GroupName))"
        $groupResult = $groupSearcher.FindOne()
        if ($groupResult -eq $null) {
            Write-Error "AD Security Group '$GroupName' not found in Active Directory."
            exit 1
        }
        $groupDN = $groupResult.Properties.distinguishedname[0]
        # LDAP filter: find members of group (recursively if needed using LDAP_MATCHING_RULE_IN_CHAIN)
        $filter = "(&(objectCategory=computer)(memberOf:1.2.840.113556.1.4.1941:=$groupDN))"
    }
    
    $searcher.Filter = $filter
    $searcher.PageSize = 1000
    
    # Retrieve relevant properties
    $properties = @("cn", "dnshostname", "distinguishedname", "operatingSystem", "whenCreated")
    foreach ($prop in $properties) {
        $searcher.PropertiesToLoad.Add($prop) | Out-Null
    }
    
    $results = $searcher.FindAll()
    $computers = @()
    
    foreach ($result in $results) {
        $name = $result.Properties.cn[0]
        $dnsName = if ($result.Properties.dnshostname.Count -gt 0) { $result.Properties.dnshostname[0] } else { "$name.$($domain.Name)" }
        $ou = $result.Properties.distinguishedname[0] -replace "^CN=$name,", ""
        $os = if ($result.Properties.operatingsystem.Count -gt 0) { $result.Properties.operatingsystem[0] } else { "Unknown OS" }
        $created = if ($result.Properties.whencreated.Count -gt 0) { $result.Properties.whencreated[0] } else { $null }
        
        $isOnline = $false
        $responseTime = $null
        
        if ($PingCheck) {
            # Fast ping check: 1 pack, 500ms timeout
            $ping = Test-Connection -ComputerName $name -Count 1 -Delay 1 -TimeToLive 128 -BufferSize 32 -ErrorAction SilentlyContinue
            if ($ping) {
                $isOnline = $true
                $responseTime = $ping.ResponseTime
            }
        }
        
        $computers += [PSCustomObject]@{
            computerName      = $name
            dnsHostName       = $dnsName
            ou                = $ou
            operatingSystem   = $os
            created           = $created
            isOnline          = $isOnline
            responseTimeMs    = $responseTime
            lastBackupStatus  = "Never Backed Up"
            lastBackupTime    = $null
        }
    }
    
    # Return as clean JSON payload
    $computers | ConvertTo-Json -Depth 4
    
} catch {
    Write-Error "Error querying Active Directory: $_"
    exit 1
}
