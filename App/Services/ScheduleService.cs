using ReactionStudio.Models;

namespace ReactionStudio.Services;

/// <summary>
/// Works out when an upload should auto-publish. Two peak slots per day in the
/// configured US time zone; successive uploads queue into future slots so you
/// effectively post publicly twice a day.
/// </summary>
public static class ScheduleService
{
    private static TimeZoneInfo Tz(AppConfig cfg)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(cfg.ScheduleTimeZone); }
        catch { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Computes the next peak slot (UTC) after the saved cursor — WITHOUT advancing it.
    /// Call Commit() only once the upload actually succeeds, so cancelled/failed
    /// uploads don't burn a slot and leave a gap.
    /// </summary>
    public static DateTimeOffset PeekNextSlot()
    {
        var cfg = AppConfig.Load();
        var tz  = Tz(cfg);

        var minUtc = DateTimeOffset.UtcNow.AddMinutes(20);   // YouTube needs a future time
        DateTimeOffset lastUtc = DateTimeOffset.MinValue;
        if (DateTimeOffset.TryParse(cfg.LastScheduledUtc, out var l)) lastUtc = l;
        var floor = lastUtc > minUtc ? lastUtc : minUtc;

        int[] hours = { cfg.PeakHourDay, cfg.PeakHourNight };
        Array.Sort(hours);

        var floorLocal = TimeZoneInfo.ConvertTime(floor, tz);
        for (int d = 0; d < 31; d++)
        {
            var day = floorLocal.Date.AddDays(d);
            foreach (var h in hours)
            {
                var wall = new DateTime(day.Year, day.Month, day.Day, h, 0, 0, DateTimeKind.Unspecified);
                DateTimeOffset utc;
                try { utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, tz), TimeSpan.Zero); }
                catch { continue; }   // skip DST-gap times
                if (utc > floor) return utc;
            }
        }
        return floor.AddHours(1);
    }

    /// <summary>Advance the cursor to this slot — call only after a successful upload.</summary>
    public static void Commit(DateTimeOffset slot)
    {
        var cfg = AppConfig.Load();
        cfg.LastScheduledUtc = slot.ToString("o");
        cfg.Save();
    }

    /// <summary>Human-readable slot description in ET + the user's local time.</summary>
    public static string Describe(DateTimeOffset utc)
    {
        var cfg = AppConfig.Load();
        var et  = TimeZoneInfo.ConvertTime(utc, Tz(cfg));
        var loc = utc.ToLocalTime();
        return $"{et:ddd MMM d, h:mm tt} ET  (your time: {loc:ddd MMM d, h:mm tt})";
    }
}
