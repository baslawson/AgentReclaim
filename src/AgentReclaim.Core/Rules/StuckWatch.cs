using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Rules;

public sealed record StuckNotice(string Title, string Text);

/// <summary>
/// Announces each possibly-stuck process once, when it first looks stuck. It's announced again only if it stops
/// looking stuck and later looks stuck again.
/// </summary>
public sealed class StuckWatch
{
    private HashSet<(ProcessKey, StuckKind)> _known = [];

    /// <summary>Call every poll, even when notifications are off, so turning them on doesn't announce old ones.</summary>
    public StuckNotice? Evaluate(AnalysisResult result, bool enabled)
    {
        var current = result.Stuck.ToDictionary(s => (s.Node.Process.Key, s.Kind));
        var fresh = current.Where(c => !_known.Contains(c.Key)).Select(c => c.Value).ToList();
        _known = current.Keys.ToHashSet();
        if (!enabled || fresh.Count == 0) return null;

        var first = fresh[0];
        var p = first.Node.Process;
        var agent = AgentSignatures.DisplayName(first.Session.Kind);
        var text = first.Kind == StuckKind.Waiting
            ? $"{p.Name} under {agent} has done nothing for {(int)first.For.TotalMinutes} min. It may be waiting for input nobody can see."
            : $"{p.Name} under {agent} has kept a CPU core busy for {(int)first.For.TotalMinutes} min.";
        if (fresh.Count > 1) text += $" And {fresh.Count - 1} more.";
        return new StuckNotice(fresh.Count == 1 ? "Process may be stuck" : $"{fresh.Count} processes may be stuck", text);
    }
}
