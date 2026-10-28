using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.Core.Rules;

public sealed record PlannedThrottle(ProcessInfo Process, AgentKind Kind, TimeSpan BusyFor);

/// <summary>
/// Decides what the opt-in automatic rules would do with a snapshot. Pure decisions, no side effects,
/// so the policy can be tested without touching real processes. Stateful only to remember how long
/// things have been busy and what it already acted on.
/// </summary>
public sealed class AutoRuleEngine
{
    /// <summary>Build and test tools worth throttling when they hog the CPU. Never the agent or the IDE.</summary>
    private static readonly HashSet<string> ThrottleableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "tsc.exe", "esbuild.exe", "bun.exe", "deno.exe", "python.exe", "pytest.exe",
        "cargo.exe", "rustc.exe", "go.exe", "dotnet.exe", "msbuild.exe", "vbcscompiler.exe",
        "cl.exe", "link.exe", "clang.exe", "clang++.exe", "gcc.exe", "g++.exe", "ld.exe",
        "java.exe", "gradle.exe", "mvn.exe", "jest.exe", "vitest.exe", "webpack.exe",
    };

    private readonly Dictionary<ProcessKey, DateTime> _busySince = new();
    private readonly HashSet<ProcessKey> _throttled = new();
    private readonly HashSet<ProcessKey> _attemptedStops = new();
    private readonly Dictionary<ProcessKey, DateTime> _hookEndedAgents = new();

    /// <summary>
    /// Called when the Claude Code SessionEnd hook reports that this agent instance is ending. Matched on
    /// pid and start time, so a later process that reuses the pid doesn't inherit the short grace period.
    /// </summary>
    public void MarkSessionEnded(ProcessKey agent, DateTime whenUtc)
    {
        if (agent.StartTicks != 0) _hookEndedAgents[agent] = whenUtc;
    }

    /// <summary>
    /// Leftovers eligible right now, best first. The caller should stop at most one per fresh snapshot:
    /// each stop takes time, and the rest of the plan goes stale meanwhile.
    /// </summary>
    public IReadOnlyList<OrphanTree> LeftoversToStop(AnalysisResult result, AppSettings settings, int selfPid)
    {
        if (!settings.AutoStopLeftovers) return [];

        // Without connection data we can't tell a leftover from a server someone is using.
        if (!result.Snapshot.NetworkComplete) return [];

        var now = result.Snapshot.TakenAtUtc;
        var idleNeeded = TimeSpan.FromMinutes(settings.AutoStopIdleMinutes);
        var plan = new List<OrphanTree>();

        foreach (var tree in result.Orphans)
        {
            // Only leftovers we actually saw under an agent. Guessed owners always need a human.
            if (tree.Attribution != Attribution.Observed) continue;
            if (_attemptedStops.Contains(tree.Root.Process.Key)) continue;

            var nodes = tree.Root.SelfAndDescendants().Select(x => x.Node).ToList();
            if (nodes.Sum(n => n.Process.EstablishedConnections) > 0) continue; // something is talking to it

            var check = SafetyPolicy.Check(tree, selfPid);
            if (!check.Allowed) continue;

            // After a hook-reported session end, the grace period must have passed since that end
            // (and the tree been idle that long); otherwise the normal idle threshold applies.
            var grace = TimeSpan.FromSeconds(settings.HookGraceSeconds);
            var hookGraceOver = tree.Owner is { } owner
                                && _hookEndedAgents.TryGetValue(owner, out var endedAt)
                                && now - endedAt >= grace;
            var required = hookGraceOver ? grace : idleNeeded;
            if (nodes.Min(n => n.IdleFor) < required) continue;

            plan.Add(tree);
        }

        // Forget hook notices for agents that are long gone.
        foreach (var pid in _hookEndedAgents.Where(kv => now - kv.Value > TimeSpan.FromHours(6)).Select(kv => kv.Key).ToList())
            _hookEndedAgents.Remove(pid);

        return plan;
    }

    /// <summary>Records that a stop was attempted, so a tree that refuses to die isn't retried every poll.</summary>
    public void RecordStopAttempt(OrphanTree tree) => _attemptedStops.Add(tree.Root.Process.Key);

    public IReadOnlyList<PlannedThrottle> ProcessesToThrottle(AnalysisResult result, AppSettings settings)
    {
        var now = result.Snapshot.TakenAtUtc;
        var alive = new HashSet<ProcessKey>();
        var plan = new List<PlannedThrottle>();

        foreach (var session in result.Sessions)
        {
            foreach (var (node, depth) in session.Root.SelfAndDescendants())
            {
                var p = node.Process;
                alive.Add(p.Key);
                if (depth == 0 || !ThrottleableNames.Contains(p.Name)) continue;
                if (AgentSignatures.AgentRootKind(p) is not null) continue;

                if (p.CpuPercent >= settings.ThrottleCpuPercent)
                    _busySince.TryAdd(p.Key, now);
                else
                    _busySince.Remove(p.Key);

                if (!settings.AutoThrottleBuilds || _throttled.Contains(p.Key)) continue;
                if (_busySince.TryGetValue(p.Key, out var since) && now - since >= TimeSpan.FromSeconds(settings.ThrottleAfterSeconds))
                    plan.Add(new PlannedThrottle(p, session.Kind, now - since));
            }
        }

        _busySince.Keys.Where(k => !alive.Contains(k)).ToList().ForEach(k => _busySince.Remove(k));
        _throttled.RemoveWhere(k => !alive.Contains(k));
        var running = result.Snapshot.Processes.Select(p => p.Key).ToHashSet();
        _attemptedStops.RemoveWhere(k => !running.Contains(k));
        return plan;
    }

    public void RecordThrottled(ProcessKey key) => _throttled.Add(key);
}
