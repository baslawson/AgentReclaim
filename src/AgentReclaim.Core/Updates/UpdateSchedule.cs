namespace AgentReclaim.Core.Updates;

/// <summary>
/// When the daily update check runs. It used to wake once an hour and find the last check minutes short of a day, so
/// "daily" came out every 25 hours and drifted through the day.
/// </summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1); // settings can change meanwhile, and PCs sleep

    public static bool IsDue(DateTime? lastUtc, DateTime nowUtc) => lastUtc is not { } last || nowUtc - last >= Every;

    /// <summary>
    /// How long to wait before looking again. A check that's already overdue here just failed, or its time hasn't been
    /// saved yet: that waits the full hour instead of retrying at once.
    /// </summary>
    public static TimeSpan NextWait(DateTime? lastUtc, DateTime nowUtc)
    {
        if (lastUtc is not { } last) return MaxWait;
        var remaining = last + Every - nowUtc;
        if (remaining <= TimeSpan.Zero) return MaxWait;
        return remaining < MaxWait ? remaining + TimeSpan.FromSeconds(1) : MaxWait;
    }
}
