using ADShield.Models;
using Newtonsoft.Json;

namespace ADShield.Core;

/// <summary>Reads and writes config + backup history to %AppData%\ADShield\</summary>
public static class AppConfig
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ADShield");

    private static readonly string ConfigFile  = Path.Combine(DataDir, "config.json");
    private static readonly string HistoryFile = Path.Combine(DataDir, "history.json");

    static AppConfig() => Directory.CreateDirectory(DataDir);

    // ── Settings ──────────────────────────────────────────────────────────────

    public static AppSettings ReadSettings()
    {
        try
        {
            if (!File.Exists(ConfigFile))
            {
                var d = new AppSettings();
                SaveSettings(d);
                return d;
            }
            return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(ConfigFile))
                   ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public static void SaveSettings(AppSettings s) =>
        File.WriteAllText(ConfigFile, JsonConvert.SerializeObject(s, Formatting.Indented));

    // ── Backup History ────────────────────────────────────────────────────────

    public static List<ComputerEntry> ReadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return [];
            return JsonConvert.DeserializeObject<List<ComputerEntry>>(File.ReadAllText(HistoryFile)) ?? [];
        }
        catch { return []; }
    }

    public static void SaveHistory(List<ComputerEntry> computers) =>
        File.WriteAllText(HistoryFile, JsonConvert.SerializeObject(computers, Formatting.Indented));

    /// <summary>Merges newly discovered computers into saved history, preserving past backup status.</summary>
    public static List<ComputerEntry> MergeDiscovered(List<ComputerEntry> discovered)
    {
        var existing = ReadHistory();
        var merged = discovered.Select(d =>
        {
            var prev = existing.FirstOrDefault(e => e.ComputerName == d.ComputerName);
            if (prev != null)
            {
                d.LastBackupStatus = prev.LastBackupStatus;
                d.LastBackupTime   = prev.LastBackupTime;
            }
            return d;
        }).ToList();
        SaveHistory(merged);
        return merged;
    }

    /// <summary>Updates just the backup result for one machine.</summary>
    public static void UpdateBackupResult(string computerName, string status)
    {
        var history = ReadHistory();
        var entry   = history.FirstOrDefault(c => c.ComputerName == computerName);
        if (entry is null) return;
        entry.LastBackupStatus = status;
        entry.LastBackupTime   = DateTime.Now;
        SaveHistory(history);
    }
}
