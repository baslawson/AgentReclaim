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
    private HashSet<ProcessKey> _knownLeftovers = [];
    private readonly HashSet<ProcessKey> _burst = [];
    private int _unreportedBurst;
    private readonly HashSet<(ProcessKey Key, string Reason)> _skipsNoted = [];
    private readonly List<string> _newSkips = [];
    private bool _noNetworkNoted;

    /// <summary>
    /// This many new leftovers in one poll is left to a person. Normal use has shown at most 6 at any time, while the
    /// one time attribution went wrong 174 appeared at once and automatic stopping worked through Windows' own.
    /// </summary>
    public const int BurstSize = 10;

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
        NoteBurst(result);
        if (!settings.AutoStopLeftovers) return [];
        var plan = new List<OrphanTree>();
        foreach (var tree in SafeLeftovers(result, settings, selfPid, skipAttempted: true))
        {
            if (tree.Root.SelfAndDescendants().Any(x => _burst.Contains(x.Node.Process.Key)))
                NoteSkip(tree.Root.Process, "it appeared together with many others");
            else
                plan.Add(tree);
        }
        return plan;
    }

    /// <summary>
    /// Why automatic stops were held back, once per process and reason, since the last time this was asked. Waiting for
    /// a time to pass isn't listed: that's the rule working, not something holding it back.
    /// </summary>
    public IReadOnlyList<string> TakeSkipNotes()
    {
        if (_newSkips.Count == 0) return [];
        var notes = _newSkips.ToList();
        _newSkips.Clear();
        return notes;
    }

    private void NoteSkip(ProcessInfo process, string reason)
    {
        if (_skipsNoted.Add((process.Key, reason)))
            _newSkips.Add($"Auto-stop skipped {process.Name} (pid {process.Pid}): {reason}.");
    }

    private void NoteNetwork(bool complete)
    {
        if (!complete && !_noNetworkNoted) _newSkips.Add("Auto-stop paused: connection data can't be read.");
        _noNetworkNoted = !complete;
    }

    /// <summary>
    /// How many leftovers just appeared at once and won't be stopped automatically (null: none since last asked). They
    /// stay listed for a person to judge, and "Free safe leftovers" still offers them.
    /// </summary>
    public int? TakeBurstNotice()
    {
        if (_unreportedBurst == 0) return null;
        var count = _unreportedBurst;
        _unreportedBurst = 0;
        return count;
    }

    private void NoteBurst(AnalysisResult result)
    {
        var current = result.Orphans
            .SelectMany(tree => tree.Root.SelfAndDescendants().Select(x => x.Node.Process.Key))
            .ToHashSet();
        var newTrees = result.Orphans.Where(tree => !_knownLeftovers.Contains(tree.Root.Process.Key)).ToList();
        if (newTrees.Count >= BurstSize)
        {
            foreach (var tree in newTrees)
            foreach (var (node, _) in tree.Root.SelfAndDescendants())
                _burst.Add(node.Process.Key);
            _unreportedBurst += newTrees.Count;
        }
        _burst.IntersectWith(current);
        _knownLeftovers = current;
    }

    /// <summary>
    /// Leftovers that pass every auto-stop check (owner seen, idle long enough, no connections, allowed by
    /// the safety policy), whether or not automatic stopping is on. The tray's "Free safe leftovers" uses this.
    /// </summary>
    /// <param name="skipAttempted">Leave out trees an automatic stop already tried.</param>
    public IReadOnlyList<OrphanTree> SafeLeftovers(AnalysisResult result, AppSettings settings, int selfPid, bool skipAttempted = false)
    {
        // Without connection data we can't tell a leftover from a server someone is using.
        if (skipAttempted) NoteNetwork(result.Snapshot.NetworkComplete);
        if (!result.Snapshot.NetworkComplete) return [];

        var now = result.Snapshot.TakenAtUtc;
        var idleNeeded = TimeSpan.FromMinutes(settings.AutoStopIdleMinutes);
        var plan = new List<OrphanTree>();

        foreach (var tree in result.Orphans)
        {
            // Only leftovers we actually saw under an agent. Guessed owners always need a human.
            if (tree.Attribution != Attribution.Observed)
            {
                if (skipAttempted) NoteSkip(tree.Root.Process, "its owner is only a guess");
                continue;
            }
            if (skipAttempted && _attemptedStops.Contains(tree.Root.Process.Key)) continue;

            var nodes = tree.Root.SelfAndDescendants().Select(x => x.Node).ToList();
            if (nodes.Sum(n => n.Process.EstablishedConnections) > 0) // something is talking to it
            {
                if (skipAttempted) NoteSkip(tree.Root.Process, "it has open connections");
                continue;
            }

            var check = SafetyPolicy.Check(tree, selfPid);
            if (!check.Allowed)
            {
                if (skipAttempted) NoteSkip(tree.Root.Process, check.BlockedReason ?? "the safety checks refused it");
                continue;
            }

            // After a hook-reported session end, the grace period must have passed since that end
            // (and the tree been idle that long); otherwise the normal idle threshold applies.
            var grace = TimeSpan.FromSeconds(settings.HookGraceSeconds);
            var hookGraceOver = tree.Owner is { } owner
                                && _hookEndedAgents.TryGetValue(owner, out var endedAt)
                                && now - endedAt >= grace;
            var required = hookGraceOver ? grace : idleNeeded;
            if (nodes.Min(n => n.IdleFor) < required) continue;

            // Idle isn't enough: a child that sat idle under its agent is idle the moment the agent exits, or restarts.
            // After a hook-reported end, that end is already the start of the wait.
            if (!hookGraceOver && tree.LeftoverFor < idleNeeded) continue;

            plan.Add(tree);
        }

        // Forget hook notices for agents that are long gone.
        foreach (var pid in _hookEndedAgents.Where(kv => now - kv.Value > TimeSpan.FromHours(6)).Select(kv => kv.Key).ToList())
            _hookEndedAgents.Remove(pid);

        return plan;
    }

    /// <summary>Records that a stop was attempted, so a tree that refuses to die isn't retried every poll.</summary>
    public void RecordStopAttempt(OrphanTree tree) => _attemptedStops.Add(tree.Root.Process.Key);

    /// <summary>
    /// Possibly-stuck commands the opt-in rule may stop now, each with everything it started. Never an agent (nor a
    /// tree with one in it), nothing on the protected list, nothing that might be an agent (an unreadable node.exe),
    /// nothing listening on a TCP port or holding a UDP one, nothing with an open connection, and never twice. The
    /// agent sees the command fail and can run it again; AgentReclaim never restarts it itself (that would run it a
    /// second time, outside the agent).
    /// </summary>
    public IReadOnlyList<StuckProcess> StuckToStop(AnalysisResult result, AppSettings settings, int selfPid)
    {
        if (!settings.AutoStopStuckWaiting && !settings.AutoStopStuckSpinning) return [];
        NoteNetwork(result.Snapshot.NetworkComplete);
        if (!result.Snapshot.NetworkComplete) return []; // can't tell whether something is still talking to it

        var plan = new List<StuckProcess>();
        foreach (var stuck in result.Stuck)
        {
            if (!(stuck.Kind == StuckKind.Waiting ? settings.AutoStopStuckWaiting : settings.AutoStopStuckSpinning)) continue;
            if (_attemptedStops.Contains(stuck.Node.Process.Key)) continue;

            var nodes = stuck.Node.SelfAndDescendants().Select(x => x.Node.Process).ToList();
            if (nodes.Any(p => p.EstablishedConnections > 0))
            {
                NoteSkip(stuck.Node.Process, "it has open connections");
                continue;
            }
            if (nodes.Any(p => p.ListeningPorts.Count > 0 || p.UdpPorts.Count > 0))
            {
                NoteSkip(stuck.Node.Process, "it's using a port");
                continue;
            }
            var check = SafetyPolicy.CheckSubtree(stuck.Node, selfPid, requireKnownNodes: true);
            if (!check.Allowed)
            {
                NoteSkip(stuck.Node.Process, check.BlockedReason ?? "the safety checks refused it");
                continue;
            }
            plan.Add(stuck);
        }
        return plan;
    }

    public void RecordStopAttempt(StuckProcess stuck) => _attemptedStops.Add(stuck.Node.Process.Key);

    /// <summary>
    /// Do the settings now (possibly changed since the poll began) still ask for this stop? Its kind's switch must be
    /// on and its time reached under the current setting.
    /// </summary>
    public static bool StillWanted(StuckProcess stuck, AppSettings now) => stuck.Kind == StuckKind.Waiting
        ? now.AutoStopStuckWaiting && stuck.For >= TimeSpan.FromMinutes(Math.Max(1, now.StuckWaitingMinutes))
        : now.AutoStopStuckSpinning && stuck.For >= TimeSpan.FromMinutes(Math.Max(1, now.StuckSpinningMinutes));

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
                if (AgentSignatures.MightBeAgent(p)) continue;

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
        _skipsNoted.RemoveWhere(s => !running.Contains(s.Key));
        return plan;
    }

    public void RecordThrottled(ProcessKey key) => _throttled.Add(key);
}
