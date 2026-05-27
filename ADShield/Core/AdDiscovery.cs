using System.DirectoryServices;
using System.Net.NetworkInformation;
using ADShield.Models;

namespace ADShield.Core;

/// <summary>
/// Queries Active Directory for computer accounts using lightweight LDAP —
/// no external AD PowerShell module required.
/// </summary>
public static class AdDiscovery
{
    /// <summary>
    /// Queries Active Directory via LDAP and returns a list of domain computer accounts,
    /// optionally filtered to a specific OU and/or AD security group.
    /// Merges backup history from <see cref="AppConfig"/> so previous backup status is preserved.
    /// </summary>
    /// <param name="searchOU">The distinguished name of the OU to scope the search to
    /// (e.g. <c>OU=Workstations,DC=corp,DC=local</c>). Pass an empty string to search the entire domain.</param>
    /// <param name="groupName">An AD security group name to restrict results to members of that group
    /// (uses recursive LDAP OID <c>1.2.840.113556.1.4.1941</c>). Pass an empty string to return all computers.</param>
    /// <param name="pingCheck">If <see langword="true"/>, sends an 800 ms ICMP ping to each discovered computer.</param>
    /// <param name="progress">Optional progress reporting sink.</param>
    /// <returns>A list of <see cref="ComputerEntry"/> objects with AD attributes and ping state populated.</returns>
    /// <exception cref="Exception">Thrown if the domain DN cannot be resolved or the AD group is not found.</exception>
    public static List<ComputerEntry> Discover(
        string searchOU,
        string groupName,
        bool   pingCheck,
        IProgress<string>? progress = null)
    {
        var computers = new List<ComputerEntry>();

        // Connect to domain root or specified OU
        var domain   = System.DirectoryServices.ActiveDirectory.Domain.GetCurrentDomain();
        var domainDN = domain.GetDirectoryEntry().Properties["distinguishedName"][0]?.ToString()
                       ?? throw new Exception("Could not resolve domain DN.");

        var searchRootPath = "LDAP://" + (string.IsNullOrWhiteSpace(searchOU) ? domainDN : searchOU);
        using var searchRoot = new DirectoryEntry(searchRootPath);
        using var searcher   = new DirectorySearcher(searchRoot);

        // Build LDAP filter — optionally scope to security group
        if (!string.IsNullOrWhiteSpace(groupName))
        {
            progress?.Report($"[INFO] Resolving AD group '{groupName}'...");
            using var groupSearcher = new DirectorySearcher(searchRoot)
            {
                Filter = $"(&(objectCategory=group)(cn={groupName}))"
            };
            var groupResult = groupSearcher.FindOne()
                ?? throw new Exception($"AD Security Group '{groupName}' not found in directory.");
            var groupDN = groupResult.Properties["distinguishedname"][0]?.ToString();
            // Recursive member-of via LDAP_MATCHING_RULE_IN_CHAIN
            searcher.Filter = $"(&(objectCategory=computer)(memberOf:1.2.840.113556.1.4.1941:={groupDN}))";
        }
        else
        {
            searcher.Filter = "(objectCategory=computer)";
        }

        searcher.PageSize = 1000;
        foreach (string p in new[] { "cn", "dnshostname", "distinguishedname", "operatingSystem" })
            searcher.PropertiesToLoad.Add(p);

        progress?.Report("[INFO] Executing LDAP query...");
        var results  = searcher.FindAll();
        var existing = AppConfig.ReadHistory();

        foreach (SearchResult result in results)
        {
            var name = result.Properties["cn"][0]?.ToString() ?? "Unknown";
            var dns  = result.Properties["dnshostname"].Count > 0
                ? result.Properties["dnshostname"][0]?.ToString() ?? $"{name}.{domain.Name}"
                : $"{name}.{domain.Name}";
            var dn = result.Properties["distinguishedname"][0]?.ToString() ?? string.Empty;
            var ou = dn.Replace($"CN={name},", "");
            var os = result.Properties["operatingsystem"].Count > 0
                ? result.Properties["operatingsystem"][0]?.ToString() ?? "Unknown OS"
                : "Unknown OS";

            bool online = false;
            int  pingMs = 0;

            if (pingCheck)
            {
                try
                {
                    using var ping   = new Ping();
                    var reply = ping.Send(name, 800);
                    if (reply.Status == IPStatus.Success)
                    {
                        online = true;
                        pingMs = (int)reply.RoundtripTime;
                    }
                }
                catch { /* host unreachable — leave offline */ }
            }

            var prev = existing.FirstOrDefault(e => e.ComputerName == name);

            computers.Add(new ComputerEntry
            {
                ComputerName      = name,
                DnsHostName       = dns,
                OU                = ou,
                OperatingSystem   = os,
                IsOnline          = online,
                PingMs            = pingMs,
                LastBackupStatus  = prev?.LastBackupStatus ?? "Never Backed Up",
                LastBackupTime    = prev?.LastBackupTime
            });

            progress?.Report($"[INFO] Found: {name} ({os}) — {(online ? $"Online {pingMs}ms" : "Offline")}");
        }

        progress?.Report($"[SUCCESS] Discovery complete. {computers.Count} computer(s) found.");
        return computers;
    }
}
