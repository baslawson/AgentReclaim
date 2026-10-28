using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Rules;

public sealed record LeftoverNotice(string Title, string Text, IReadOnlyList<OrphanTree> Trees);

/// <summary>
/// Notices leftovers as they appear, e.g. "Claude Code left node.exe running on port 5173 (shop)". Only
/// leftovers seen under an agent count, and only once they've been there for two polls in a row, so a
/// shell finishing up a moment after its agent doesn't cause a notification. Leftovers already there
/// when the app starts are never announced.
/// </summary>
public sealed class LeftoverWatch
{
    private HashSet<ProcessKey>? _known;
    private HashSet<ProcessKey> _pending = new();

    /// <summary>Call every poll, even when notifications are off, so turning them on doesn't announce old leftovers.</summary>
    public LeftoverNotice? Evaluate(AnalysisResult result, bool enabled, Func<ProcessInfo, string?> projectOf)
    {
        var observed = result.Orphans.Where(o => o.Attribution == Attribution.Observed).ToList();
        var current = observed.Select(o => o.Root.Process.Key).ToHashSet();

        if (_known is null)
        {
            _known = current;
            return null;
        }

        var fresh = new List<OrphanTree>();
        var pending = new HashSet<ProcessKey>();
        foreach (var tree in observed)
        {
            var key = tree.Root.Process.Key;
            if (_known.Contains(key)) continue;
            if (_pending.Contains(key))
            {
                _known.Add(key);
                fresh.Add(tree);
            }
            else
            {
                pending.Add(key);
            }
        }
        _pending = pending;
        _known.IntersectWith(current);

        return enabled && fresh.Count > 0 ? Describe(fresh, projectOf) : null;
    }

    public static LeftoverNotice Describe(IReadOnlyList<OrphanTree> trees, Func<ProcessInfo, string?> projectOf)
    {
        var agents = string.Join(" and ", trees.Select(t => AgentSignatures.DisplayName(t.Kind)).Distinct());
        var parts = trees.Take(3).Select(t =>
        {
            var root = t.Root.Process;
            var ports = t.Root.SelfAndDescendants().SelectMany(x => x.Node.Process.ListeningPorts).Distinct().Order().ToList();
            var project = projectOf(root);
            return $"{root.Name}{(ports.Count == 0 ? "" : $" on port {string.Join(", ", ports.Take(3))}")}" +
                   $"{(project is null ? "" : $" ({ProjectResolver.DisplayName(project)})")}";
        }).ToList();
        if (trees.Count > 3) parts.Add($"{trees.Count - 3} more");

        var text = $"{agents} left {JoinList(parts)} running.";
        return new LeftoverNotice(trees.Count == 1 ? "Leftover process" : $"{trees.Count} leftover processes", text, trees);
    }

    private static string JoinList(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
