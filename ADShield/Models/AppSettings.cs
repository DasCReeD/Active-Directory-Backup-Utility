namespace ADShield.Models;

public class AppSettings
{
    // ── VeraCrypt (Process.Start exception — core concept)
    public string VeraCryptExePath    { get; set; } = @"C:\Program Files\VeraCrypt\VeraCrypt.exe";
    public string VeraCryptContainer  { get; set; } = @"C:\BackupVault.hc";
    public string MountLetter         { get; set; } = "V";

    // ── Backup storage
    // Root folder inside the mounted VeraCrypt volume where per-machine VHDX files are stored.
    public string BackupStorageRoot   { get; set; } = "backups";   // relative to mount letter root
    public long   VhdxSizeGb         { get; set; } = 1024;          // 1 TB default

    // ── Active Directory targeting
    // Leave SearchOU blank to search the entire domain.
    // Leave AdGroup blank to target ALL domain computers (no group filter).
    public string SearchOU    { get; set; } = "";
    public string AdGroup     { get; set; } = "";   // blank = all computers

    // ── Scheduling
    public bool   ScheduleActive { get; set; } = false;  // off by default until configured
    public string NightlyCron    { get; set; } = "0 1 * * *";   // 01:00 every night
    public string WeeklyCron     { get; set; } = "0 0 * * 0";   // Sunday midnight

    public bool DomainAdminContext { get; set; } = true;

    // ── Client Agent Connection Settings
    public int    AgentPort   { get; set; } = 9099;
    public string AgentApiKey { get; set; } = "ADShieldDefaultApiKeySecret_ChangeMe";

    // Computed helper: full path to backup root on the mounted drive
    public string BackupRootPath =>
        System.IO.Path.Combine($"{MountLetter}:\\", BackupStorageRoot);
}
