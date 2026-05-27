using ADShield.Models;

namespace ADShield.Core;

/// <summary>
/// Simple daily/weekly scheduler using System.Threading.Timer.
/// Parses the cron-style times stored in AppSettings (hour:minute only — no full cron engine needed).
/// </summary>
public class SchedulerService : IDisposable
{
    private System.Threading.Timer?   _nightlyTimer;
    private System.Threading.Timer?   _weeklyTimer;
    private readonly AppSettings _settings;

    public event Action<string>? BackupTriggered; // raises with backupType "Incremental"|"Full"

    public SchedulerService(AppSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        Stop();
        if (!_settings.ScheduleActive) return;

        ScheduleNightly();
        ScheduleWeekly();
    }

    public void Stop()
    {
        _nightlyTimer?.Dispose();
        _weeklyTimer?.Dispose();
        _nightlyTimer = null;
        _weeklyTimer  = null;
    }

    // ── Nightly Incremental ───────────────────────────────────────────────────

    private void ScheduleNightly()
    {
        var (hour, minute) = ParseCronHM(_settings.NightlyCron, defaultHour: 1);
        var delay = DelayUntil(hour, minute);
        _nightlyTimer = new System.Threading.Timer(_ =>
        {
            BackupTriggered?.Invoke("Incremental");
            // Reschedule for next day
            ScheduleNightly();
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    // ── Weekly Full ───────────────────────────────────────────────────────────

    private void ScheduleWeekly()
    {
        var (hour, minute) = ParseCronHM(_settings.WeeklyCron, defaultHour: 0);
        var delay = DelayUntilWeekly(DayOfWeek.Sunday, hour, minute);
        _weeklyTimer = new System.Threading.Timer(_ =>
        {
            BackupTriggered?.Invoke("Full");
            ScheduleWeekly();
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Parses the minute and hour fields from a 5-field cron string.</summary>
    private static (int hour, int minute) ParseCronHM(string cron, int defaultHour)
    {
        try
        {
            var parts = cron.Trim().Split(' ');
            int minute = int.Parse(parts[0]);
            int hour   = int.Parse(parts[1]);
            return (hour, minute);
        }
        catch
        {
            return (defaultHour, 0);
        }
    }

    private static TimeSpan DelayUntil(int hour, int minute)
    {
        var now    = DateTime.Now;
        var target = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
        if (target <= now) target = target.AddDays(1);
        return target - now;
    }

    private static TimeSpan DelayUntilWeekly(DayOfWeek day, int hour, int minute)
    {
        var now    = DateTime.Now;
        int daysUntil = ((int)day - (int)now.DayOfWeek + 7) % 7;
        if (daysUntil == 0 && now.TimeOfDay >= new TimeSpan(hour, minute, 0))
            daysUntil = 7;
        var target = now.Date.AddDays(daysUntil)
                        .AddHours(hour).AddMinutes(minute);
        return target - now;
    }

    public void Dispose() => Stop();
}
