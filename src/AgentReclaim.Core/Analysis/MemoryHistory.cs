using System.Text.Json;
using AgentReclaim.Core.Actions;

namespace AgentReclaim.Core.Analysis;

/// <summary>AI-tool memory at one moment: the total, the leftovers' share, and each project's (keyed by folder).</summary>
public sealed record HistorySample(DateTime TimeUtc, long AiBytes, long LeftoverBytes, Dictionary<string, long> Projects);

/// <summary>What was freed in a period: total bytes and number of stops, split by who asked.</summary>
public sealed record FreedSummary(long Bytes, int Stops, long ManualBytes, long AutoBytes, long AgentBytes);

/// <summary>
/// One sample a minute of AI-tool memory, per project, for the last 24 hours. Saved to disk now and then,
/// so a restart doesn't lose the day.
/// </summary>
public sealed class MemoryHistory(string? path = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

    private readonly List<HistorySample> _samples = Load(path);
    private readonly object _gate = new();
    private DateTime _savedAt = DateTime.UtcNow;

    public IReadOnlyList<HistorySample> Samples
    {
        get
        {
            lock (_gate) return _samples.ToList();
        }
    }

    /// <summary>Adds a sample if a minute has passed since the last one. Returns true if it did.</summary>
    public bool Add(DateTime nowUtc, AnalysisResult result, IReadOnlyList<ProjectSummary> projects)
    {
        lock (_gate)
        {
            if (_samples.Count > 0 && nowUtc - _samples[^1].TimeUtc < Every && nowUtc >= _samples[^1].TimeUtc) return false;
            var byProject = projects.Where(p => p.TotalBytes > 0)
                .GroupBy(p => p.Path ?? "")
                .ToDictionary(g => g.Key, g => g.Sum(p => p.TotalBytes));
            _samples.Add(new HistorySample(nowUtc, result.AgentPrivateBytes, result.OrphanPrivateBytes, byProject));
            _samples.RemoveAll(s => nowUtc - s.TimeUtc > Window || s.TimeUtc > nowUtc + Every);
        }
        if (nowUtc - _savedAt >= SaveEvery) Save(nowUtc);
        return true;
    }

    public void Save(DateTime? nowUtc = null)
    {
        _savedAt = nowUtc ?? DateTime.UtcNow;
        if (path is null) return;
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_samples);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the next save tries again.
        }
    }

    /// <summary>The projects that used the most memory at their peak over the window, biggest first.</summary>
    public IReadOnlyList<string> TopProjects(int count)
    {
        lock (_gate)
        {
            return _samples.SelectMany(s => s.Projects)
                .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Path: g.Key, Peak: g.Max(x => x.Value)))
                .OrderByDescending(x => x.Peak)
                .Take(count)
                .Select(x => x.Path)
                .ToList();
        }
    }

    /// <summary>Successful stops since <paramref name="sinceUtc"/>, by trigger: "mcp:" is an agent, "auto:" a rule, anything else a person.</summary>
    public static FreedSummary Freed(IEnumerable<ActionLogEntry> entries, DateTime sinceUtc)
    {
        long manual = 0, auto = 0, agent = 0;
        var stops = 0;
        foreach (var e in entries)
        {
            if (e.Phase != "done" || !e.Success || e.TimeUtc < sinceUtc) continue;
            if (e.Action is not ("stop-tree" or "docker-stop")) continue;
            stops++;
            var bytes = Math.Max(0, e.FreedBytes);
            if (e.Trigger?.StartsWith("mcp:", StringComparison.Ordinal) == true) agent += bytes;
            else if (e.Trigger?.StartsWith("auto:", StringComparison.Ordinal) == true) auto += bytes;
            else manual += bytes;
        }
        return new FreedSummary(manual + auto + agent, stops, manual, auto, agent);
    }

    private static List<HistorySample> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try
        {
            var now = DateTime.UtcNow;
            return (JsonSerializer.Deserialize<List<HistorySample>>(File.ReadAllText(path)) ?? [])
                .Where(s => s is not null && s.Projects is not null && now - s.TimeUtc <= Window && s.TimeUtc <= now + Every)
                .OrderBy(s => s.TimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
