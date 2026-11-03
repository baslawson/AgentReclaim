using System.Text.Json;
using AgentReclaim.Core.Actions;

namespace AgentReclaim.Core.Analysis;

/// <summary>
/// The lifetime count behind "42 GB reclaimed". The action log is the source: entries newer than the last one counted
/// are added, so stops by rules or by agents through MCP count too, and trimming the log loses nothing.
/// </summary>
public sealed class ReclaimTally
{
    public long TotalBytes { get; set; }
    public int Stops { get; set; }
    public DateTime CountedThroughUtc { get; set; }

    /// <summary>The first day this tally existed, so a new install doesn't claim a streak from before it.</summary>
    public DateOnly? Since { get; set; }

    /// <summary>The last day any leftover was seen.</summary>
    public DateOnly? LastLeftoverDay { get; set; }

    /// <summary>Counts what's new in the log (any order). True if the tally changed.</summary>
    public bool Count(IEnumerable<ActionLogEntry> entries)
    {
        var through = CountedThroughUtc;
        var changed = false;
        foreach (var e in entries)
        {
            if (!MemoryHistory.IsFreed(e) || e.TimeUtc <= CountedThroughUtc) continue;
            TotalBytes += Math.Max(0, e.FreedBytes);
            Stops++;
            if (e.TimeUtc > through) through = e.TimeUtc;
            changed = true;
        }
        CountedThroughUtc = through;
        return changed;
    }

    /// <summary>Notes today's state after a poll. True if the tally changed, which is at most about once a day.</summary>
    public bool NoteDay(DateOnly today, bool hasLeftovers)
    {
        var changed = false;
        if (Since is null)
        {
            Since = today;
            changed = true;
        }
        if (hasLeftovers && LastLeftoverDay != today)
        {
            LastLeftoverDay = today;
            changed = true;
        }
        return changed;
    }

    /// <summary>Full days in a row with no leftover seen, not counting today, which isn't over yet.</summary>
    public int CleanDays(DateOnly today)
    {
        if (Since is not { } since) return 0;
        var start = LastLeftoverDay is { } last ? last.AddDays(1) : since;
        return Math.Max(0, today.DayNumber - start.DayNumber);
    }

    public static ReclaimTally Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<ReclaimTally>(File.ReadAllText(path)) is { } tally) return tally;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Diagnostics.Log.Warning("Couldn't read the reclaimed tally; starting again from the action log", ex);
        }
        return new ReclaimTally();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Warning("Couldn't save the reclaimed tally", ex);
        }
    }
}
