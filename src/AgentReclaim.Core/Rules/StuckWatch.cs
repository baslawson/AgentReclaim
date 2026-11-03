using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Rules;

public sealed record StuckNotice(string Title, string Text)
{
    /// <summary>For the log: which processes, by name and pid.</summary>
    public string Processes { get; init; } = "";
}

/// <summary>
/// Announces each possibly-stuck process once, when it first looks stuck. It's announced again only after it has gone
/// a long time without looking stuck: a process that drops off the list for a moment and comes straight back is the
/// same episode (one once did this for 3.5 hours, one notification every 22 seconds).
/// </summary>
public sealed class StuckWatch
{
    /// <summary>How long a process must not look stuck before it can be announced again.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(1);

    private readonly Dictionary<(ProcessKey Key, StuckKind Kind), DateTime> _lastSeen = new();

    /// <summary>Call every poll, even when notifications are off, so turning them on doesn't announce old ones.</summary>
    public StuckNotice? Evaluate(AnalysisResult result, bool enabled)
    {
        var now = result.Snapshot.TakenAtUtc;
        var current = result.Stuck.ToDictionary(s => (s.Node.Process.Key, s.Kind));
        var fresh = current
            .Where(c => !_lastSeen.TryGetValue(c.Key, out var seen) || now - seen >= RepeatAfter)
            .Select(c => c.Value).ToList();

        foreach (var key in current.Keys) _lastSeen[key] = now;
        if (_lastSeen.Count > 0 && result.Snapshot.Processes.Count > 0)
        {
            var running = result.Snapshot.Processes.Select(p => p.Key).ToHashSet();
            foreach (var key in _lastSeen.Keys.Where(k => !running.Contains(k.Key)).ToList()) _lastSeen.Remove(key);
        }

        if (!enabled || fresh.Count == 0) return null;

        var first = fresh[0];
        var p = first.Node.Process;
        var agent = AgentSignatures.DisplayName(first.Session.Kind);
        var text = first.Kind == StuckKind.Waiting
            ? $"{p.Name} under {agent} has done nothing for {(int)first.For.TotalMinutes} min. It may be waiting for input nobody can see."
            : $"{p.Name} under {agent} has kept a CPU core busy for {(int)first.For.TotalMinutes} min.";
        if (fresh.Count > 1) text += $" And {fresh.Count - 1} more.";
        return new StuckNotice(fresh.Count == 1 ? "Process may be stuck" : $"{fresh.Count} processes may be stuck", text)
        {
            Processes = string.Join(", ", fresh.Select(s => $"{s.Node.Process.Name} pid {s.Node.Process.Pid}")),
        };
    }
}
