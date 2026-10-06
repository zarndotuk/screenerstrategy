namespace KlseBacktester.Data;

/// <summary>Bursa Malaysia session timing (Malaysia time, UTC+8).</summary>
public static class BursaCalendar
{
    /// <summary>
    /// Bursa closes at 17:00 MYT; allow time for Yahoo to publish the final daily bar.
    /// </summary>
    private static readonly TimeSpan FinalBarAvailableAt = new(18, 0, 0);

    private static readonly TimeZoneInfo Malaysia = ResolveMalaysiaTimeZone();

    /// <summary>
    /// Date of the latest session whose daily bar is final. Before 18:00 MYT that is
    /// the previous calendar day, so an in-progress bar is never stored or scored.
    /// </summary>
    public static DateTime LastCompletedSessionDate()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Malaysia);
        return now.TimeOfDay >= FinalBarAvailableAt ? now.Date : now.Date.AddDays(-1);
    }

    private static TimeZoneInfo ResolveMalaysiaTimeZone()
    {
        foreach (var id in new[] { "Asia/Kuala_Lumpur", "Singapore Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
                return tz;
        }

        return TimeZoneInfo.CreateCustomTimeZone("MYT", TimeSpan.FromHours(8), "MYT", "MYT");
    }
}
