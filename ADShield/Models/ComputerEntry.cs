namespace ADShield.Models;

public class ComputerEntry
{
    public string ComputerName { get; set; } = string.Empty;
    public string DnsHostName  { get; set; } = string.Empty;
    public string OU           { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = "Unknown OS";
    public bool   IsOnline     { get; set; }
    public int    PingMs       { get; set; }
    public string LastBackupStatus { get; set; } = "Never Backed Up";
    public DateTime? LastBackupTime { get; set; }

    // Computed display helpers
    public string OnlineDisplay => IsOnline ? $"Online  ({PingMs} ms)" : "Offline";
    public string LastBackupTimeDisplay =>
        LastBackupTime.HasValue ? LastBackupTime.Value.ToString("yyyy-MM-dd HH:mm") : "—";
}
