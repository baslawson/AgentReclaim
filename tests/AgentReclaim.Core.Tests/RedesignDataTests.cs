using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;

namespace AgentReclaim.Core.Tests;

public class RedesignDataTests
{
    private static readonly DateTime Noon = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 3);

    private static ActionLogEntry Entry(DateTime utc, long bytes, string action = "stop-tree", string phase = "done",
        bool success = true, string trigger = "manual") =>
        new(utc, action, phase, trigger, success, "", 1, "x", null, null, null, 1, bytes, "", "");

    [Fact]
    public void Tally_counts_each_stop_once_across_refreshes_and_in_any_order()
    {
        var tally = new ReclaimTally();
        var log = new[] { Entry(Noon.AddHours(1), 200), Entry(Noon, 100) }; // the log reads newest first

        Assert.True(tally.Count(log));
        Assert.False(tally.Count(log));
        Assert.True(tally.Count(log.Prepend(Entry(Noon.AddHours(2), 50))));

        Assert.Equal(350, tally.TotalBytes);
        Assert.Equal(3, tally.Stops);
    }

    [Fact]
    public void Tally_ignores_failures_other_actions_and_start_phases()
    {
        var tally = new ReclaimTally();
        Assert.False(tally.Count([
            Entry(Noon, 1, success: false),
            Entry(Noon, 2, phase: "start"),
            Entry(Noon, 4, action: "throttle"),
        ]));
        Assert.Equal(0, tally.TotalBytes);
    }

    [Fact]
    public void Tally_counts_containers_and_treats_a_negative_measurement_as_nothing()
    {
        var tally = new ReclaimTally();
        tally.Count([Entry(Noon, 100, action: "docker-stop"), Entry(Noon.AddMinutes(1), -5)]);
        Assert.Equal(100, tally.TotalBytes);
        Assert.Equal(2, tally.Stops);
    }

    [Fact]
    public void Tally_survives_a_trimmed_log_and_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tally-{Guid.NewGuid():N}.json");
        var tally = new ReclaimTally();
        tally.Count([Entry(Noon, 100)]);
        tally.NoteDay(Today, hasLeftovers: true);
        tally.Save(path);

        var loaded = ReclaimTally.Load(path);
        Assert.False(loaded.Count([])); // log trimmed: nothing lost
        Assert.True(loaded.Count([Entry(Noon, 100), Entry(Noon.AddMinutes(1), 1)]));
        Assert.Equal(101, loaded.TotalBytes);
        Assert.Equal(Today, loaded.LastLeftoverDay);
    }

    [Fact]
    public void A_new_install_has_no_streak_until_a_full_day_passes()
    {
        var tally = new ReclaimTally();
        Assert.Equal(0, tally.CleanDays(Today));
        tally.NoteDay(Today, hasLeftovers: false);
        Assert.Equal(0, tally.CleanDays(Today));
        Assert.Equal(3, tally.CleanDays(Today.AddDays(3)));
    }

    [Fact]
    public void A_leftover_resets_the_streak_counting_from_the_next_day()
    {
        var tally = new ReclaimTally();
        tally.NoteDay(Today.AddDays(-10), hasLeftovers: false);
        Assert.True(tally.NoteDay(Today, hasLeftovers: true));
        Assert.False(tally.NoteDay(Today, hasLeftovers: true)); // saved at most once a day
        Assert.False(tally.NoteDay(Today, hasLeftovers: false));

        Assert.Equal(0, tally.CleanDays(Today));
        Assert.Equal(0, tally.CleanDays(Today.AddDays(1)));
        Assert.Equal(1, tally.CleanDays(Today.AddDays(2)));
    }

    [Fact]
    public void Freed_by_day_fills_empty_days_and_ends_today()
    {
        var localNoon = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var days = MemoryHistory.FreedByDay([
            Entry(localNoon, 100), Entry(localNoon.AddHours(-1), 50), Entry(localNoon.AddDays(-2), 7),
            Entry(localNoon.AddDays(-30), 999), Entry(localNoon, 1000, success: false),
        ], Today, 3);

        Assert.Equal([(Today.AddDays(-2), 7L), (Today.AddDays(-1), 0L), (Today, 150L)], days);
    }

    [Theory]
    [InlineData(0.0, MeterState.Good)]
    [InlineData(0.699, MeterState.Good)]
    [InlineData(0.7, MeterState.Warn)]
    [InlineData(0.899, MeterState.Warn)]
    [InlineData(0.9, MeterState.Danger)]
    [InlineData(1.0, MeterState.Danger)]
    public void Meter_thresholds(double fraction, MeterState expected) => Assert.Equal(expected, Personality.Meter(fraction));

    [Theory]
    [InlineData(0.5, 0, MascotMood.Content)]
    [InlineData(0.5, 2, MascotMood.Spotted)]
    [InlineData(0.95, 0, MascotMood.Concerned)]
    [InlineData(0.95, 2, MascotMood.Concerned)] // low memory outranks the leftovers
    public void Mood_follows_memory_then_leftovers(double load, int leftovers, MascotMood expected) =>
        Assert.Equal(expected, Personality.Mood(load, leftovers));

    [Fact]
    public void Status_lines_are_plain_when_something_needs_attention()
    {
        Assert.Equal("Everything's tidy.", Personality.StatusLine(0.5, 0));
        Assert.Equal("Spotted 1 leftover.", Personality.StatusLine(0.5, 1));
        Assert.Equal("Spotted 2 leftovers.", Personality.StatusLine(0.5, 2));
        Assert.Equal("Memory is running low.", Personality.StatusLine(0.95, 2));
    }

    [Fact]
    public void Greetings_cover_the_whole_day()
    {
        foreach (var hour in Enumerable.Range(0, 24)) Assert.NotEmpty(Personality.Greeting(hour));
    }

    [Fact]
    public void Only_real_gains_are_celebrated_and_never_a_stuck_stop()
    {
        Assert.True(Personality.ShouldCelebrate(Entry(Noon, 100)));
        Assert.True(Personality.ShouldCelebrate(Entry(Noon, 100, trigger: "auto: leftover idle")));
        Assert.False(Personality.ShouldCelebrate(Entry(Noon, 0)));
        Assert.False(Personality.ShouldCelebrate(Entry(Noon, 100, success: false)));
        Assert.False(Personality.ShouldCelebrate(Entry(Noon, 100, trigger: "auto: stuck command")));
    }

    [Fact]
    public void Rolling_series_keeps_the_newest_in_order()
    {
        var series = new RollingSeries(3);
        Assert.Empty(series.ToArray());
        series.Add(1);
        series.Add(2);
        Assert.Equal([1.0, 2.0], series.ToArray());
        series.Add(3);
        series.Add(4);
        Assert.Equal([2.0, 3.0, 4.0], series.ToArray());
    }
}

/// <summary>Writes to the shared log, so it can't run alongside the tests that repoint it.</summary>
[Collection("Global path overrides")]
public class DamagedTallyTests
{
    [Fact]
    public void A_damaged_tally_file_starts_again()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tally-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(0, ReclaimTally.Load(path).TotalBytes);
    }
}
