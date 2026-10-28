using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Analysis;

public sealed record AnalyzerOptions
{
    /// <summary>
    /// CPU (a share of the whole machine) at or above this counts as "doing something", or at or above
    /// <see cref="ActiveCorePercent"/> of one core, whichever is lower: on a machine with many cores a process
    /// keeping a whole core busy is under 0.5% of it.
    /// </summary>
    public double ActiveCpuPercent { get; init; } = 0.5;

    /// <summary>Share of one core that always counts as activity (no change up to 16 cores).</summary>
    public double ActiveCorePercent { get; init; } = 8;

    /// <summary>Children of a live agent older than this and currently idle count toward a "stale pile" finding.</summary>
    public TimeSpan StaleChildAge { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How many stale children with the same name it takes to raise a finding.</summary>
    public int StaleChildMinCount { get; init; } = 5;

    /// <summary>Never reported as a leftover: AgentReclaim itself (which may well have been launched by an agent).</summary>
    public int SelfPid { get; init; } = Environment.ProcessId;

    /// <summary>Warn when an agent's own memory keeps climbing.</summary>
    public bool DetectLeaks { get; init; } = true;

    public TimeSpan LeakSampleInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan LeakMinWindow { get; init; } = TimeSpan.FromMinutes(20);
    public long LeakMinGrowthBytes { get; init; } = 500L << 20;
    public double LeakMinGrowthRatio { get; init; } = 0.5;

    /// <summary>A tool an agent started that's shown no activity this long may be stuck (null: don't look).</summary>
    public TimeSpan? StuckWaitingAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A process in a session that's kept a whole CPU core busy this long may be stuck in a loop (null: don't look).</summary>
    public TimeSpan? StuckSpinningAfter { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Share of one core that counts as "a whole core".</summary>
    public double SpinningCorePercent { get; init; } = 90;

    /// <summary>A dip under that share shorter than this doesn't restart the clock (builds fluctuate).</summary>
    public TimeSpan SpinningGap { get; init; } = TimeSpan.FromSeconds(30);

    public int CoreCount { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Children started this soon after their agent are its standing helpers (MCP servers, plugins), which sit idle
    /// by design, not tool calls.
    /// </summary>
    public TimeSpan AgentStartupGrace { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Groups processes into live agent sessions and finds what dead sessions left behind.
/// Stateful across snapshots: it tracks idle time and remembers lineage.
/// </summary>
public sealed class Analyzer(LineageStore lineage, AnalyzerOptions? options = null)
{
    private AnalyzerOptions _options = options ?? new AnalyzerOptions();
    private readonly Dictionary<ProcessKey, (DateTime FirstSeen, DateTime LastActive)> _activity = new();
    private readonly Dictionary<ProcessKey, List<(DateTime Time, long Bytes)>> _agentMemory = new();
    private readonly Dictionary<ProcessKey, (DateTime Since, DateTime LastHigh)> _busy = new(); // a whole core busy since
    // A tool's whole tree: last seen active (a member busy, or one starting or exiting) and who was in it.
    private readonly Dictionary<ProcessKey, (DateTime LastActive, string Members)> _toolActivity = new();

    /// <summary>Swapped in when the user changes settings; only read on the polling thread.</summary>
    public AnalyzerOptions Options
    {
        get => _options;
        set => _options = value;
    }

    public AnalysisResult Analyze(ProcessSnapshot snapshot)
    {
        var now = snapshot.TakenAtUtc;
        var byPid = new Dictionary<int, ProcessInfo>();
        foreach (var p in snapshot.Processes) byPid[p.Pid] = p;

        UpdateActivity(snapshot);

        // A parent only counts if it started before the child; otherwise the PID was reused. When either start
        // time can't be read (system processes, for a non-admin app) that can't be checked, so there's no parent:
        // Windows reuses PIDs, and wininit.exe's long-gone parent PID once belonged to an agent's child, which
        // made every system service look like part of that session.
        ProcessInfo? ParentOf(ProcessInfo p) =>
            p.ParentPid != p.Pid && byPid.TryGetValue(p.ParentPid, out var parent)
            && p.StartTimeUtc != DateTime.MinValue && parent.StartTimeUtc != DateTime.MinValue
            && parent.StartTimeUtc <= p.StartTimeUtc
                ? parent
                : null;

        var children = new Dictionary<int, List<ProcessInfo>>();
        foreach (var p in snapshot.Processes)
        {
            if (ParentOf(p) is not { } parent) continue;
            if (!children.TryGetValue(parent.Pid, out var list)) children[parent.Pid] = list = [];
            list.Add(p);
        }

        // Session root = the topmost agent in the ancestor chain, so Codex launched by Claude stays in Claude's session.
        var rootMemo = new Dictionary<int, ProcessInfo?>();
        ProcessInfo? SessionRootOf(ProcessInfo p)
        {
            if (rootMemo.TryGetValue(p.Pid, out var memo)) return memo;
            rootMemo[p.Pid] = null; // cycle guard
            var parent = ParentOf(p);
            var root = (parent is null ? null : SessionRootOf(parent)) ?? (AgentSignatures.AgentRootKind(p) is not null ? p : null);
            rootMemo[p.Pid] = root;
            return root;
        }

        // Record lineage for everything under a live agent before deciding what's orphaned.
        // The owner is the outermost agent (the session); every agent above it is kept too, so an agent nested
        // in another (Codex started by Claude) can still find its own leftovers over MCP.
        IEnumerable<ProcessKey> AgentsAbove(ProcessInfo p)
        {
            var seen = new HashSet<int>();
            for (var at = ParentOf(p); at is not null && seen.Add(at.Pid); at = ParentOf(at))
                if (AgentSignatures.AgentRootKind(at) is not null) yield return at.Key;
        }

        foreach (var p in snapshot.Processes)
        {
            if (SessionRootOf(p) is { } root)
            {
                lineage.Record(p.Key, new LineageEntry(AgentSignatures.AgentRootKind(root)!.Value, root.Pid, root.Name,
                    OwnerStartTicks: root.Key.StartTicks,
                    // Union with what was seen before: if a launcher in between exits, the agents above it still count.
                    Agents: LineageEntry.FormatAgents(AgentsAbove(p).Concat(lineage.Get(p.Key)?.AgentKeys ?? new HashSet<ProcessKey>()))));
            }
        }

        // Anything above a live agent is hosting it (e.g. a plugin broker that launched Codex), not a leftover.
        var hostsLiveAgent = new HashSet<int>();
        foreach (var p in snapshot.Processes.Where(p => SessionRootOf(p) == p))
        {
            for (var host = ParentOf(p); host is not null && hostsLiveAgent.Add(host.Pid); host = ParentOf(host)) { }
        }

        var orphanMemo = new Dictionary<int, LineageEntry?>();
        LineageEntry? OrphanInfo(ProcessInfo p)
        {
            if (orphanMemo.TryGetValue(p.Pid, out var memo)) return memo;
            orphanMemo[p.Pid] = null;

            LineageEntry? result = null;
            if (SessionRootOf(p) is null && !hostsLiveAgent.Contains(p.Pid) && p.Pid != _options.SelfPid)
            {
                var parent = ParentOf(p);
                var own = lineage.Get(p.Key);
                var inherited = parent is null ? null : OrphanInfo(parent);

                // Prefer what we actually saw over what we inherited or guessed.
                result = own is { Guessed: false } ? own
                    : inherited ?? own
                    ?? (parent is null && AgentSignatures.GuessOwnerFromPaths(p) is { } guess
                        ? new LineageEntry(guess, 0, "unknown (parent already exited)", Guessed: true)
                        : null);

                // Persist it, so ownership survives this process's parent exiting and app restarts.
                if (result is not null) lineage.Record(p.Key, result);
            }

            orphanMemo[p.Pid] = result;
            return result;
        }

        // Memoised, so sessions, leftovers and the all-processes index share the same nodes.
        // Every process is built now, including unreadable ones (no start time), whose stale parent PIDs
        // could form a loop: a process already being built is never its own descendant.
        var built = new Dictionary<ProcessKey, ProcessNode>();
        var building = new HashSet<int>();
        ProcessNode Build(ProcessInfo p)
        {
            if (built.TryGetValue(p.Key, out var node)) return node;
            building.Add(p.Pid);
            node = new ProcessNode(p, IdleFor(p.Key, now),
                children.TryGetValue(p.Pid, out var list) ? list.Where(c => !building.Contains(c.Pid)).Select(Build).ToList() : []);
            building.Remove(p.Pid);
            built[p.Key] = node;
            return node;
        }

        var sessions = snapshot.Processes
            .Where(p => SessionRootOf(p) == p)
            .Select(p => new AgentSession(AgentSignatures.AgentRootKind(p)!.Value, Build(p)))
            .OrderByDescending(s => s.Root.TotalPrivateBytes)
            .ToList();

        var orphans = new List<OrphanTree>();
        foreach (var p in snapshot.Processes)
        {
            if (OrphanInfo(p) is not { } info) continue;
            if (ParentOf(p) is { } parent && OrphanInfo(parent) is not null) continue; // not the top of its tree
            orphans.Add(info.Guessed
                ? new OrphanTree(Build(p), info.Kind, Attribution.Heuristic, info.OwnerName)
                : new OrphanTree(Build(p), info.Kind, Attribution.Observed, $"{info.OwnerName} (pid {info.OwnerPid})", info.OwnerPid, info.OwnerStartTicks)
                {
                    Agents = info.AgentKeys,
                });
        }
        orphans.Sort((a, b) => b.Root.TotalPrivateBytes.CompareTo(a.Root.TotalPrivateBytes));

        // Never prune from an empty snapshot: it means collection failed, not that everything exited.
        if (snapshot.Processes.Count > 0)
            lineage.RetainOnly(snapshot.Processes.Select(p => p.Key).ToHashSet());
        lineage.SaveIfDirty();

        var stuck = FindStuck(sessions, now);
        var findings = FindLeaks(sessions, now).Concat(FindStalePiles(sessions, now)).Concat(stuck.Select(Describe)).ToList();
        foreach (var p in snapshot.Processes) Build(p);
        return new AnalysisResult(snapshot, sessions, orphans, findings) { Nodes = built, Stuck = stuck };
    }

    /// <summary>
    /// An agent whose own private memory has grown steadily (not just spiked) over a long window.
    /// Long sessions with big contexts do grow, so the thresholds are deliberately high.
    /// </summary>
    private IEnumerable<Finding> FindLeaks(IReadOnlyList<AgentSession> sessions, DateTime now)
    {
        var live = sessions.Select(s => s.Root.Process).ToDictionary(p => p.Key);
        foreach (var key in _agentMemory.Keys.Where(k => !live.ContainsKey(k)).ToList())
            _agentMemory.Remove(key);

        foreach (var (key, process) in live)
        {
            if (!_agentMemory.TryGetValue(key, out var samples)) _agentMemory[key] = samples = [];
            if (samples.Count == 0 || now - samples[^1].Time >= _options.LeakSampleInterval)
            {
                samples.Add((now, process.PrivateBytes));
                if (samples.Count > 120) samples.RemoveAt(0);
            }

            if (!_options.DetectLeaks || samples.Count < 5) continue;
            var first = samples[0];
            var last = samples[^1];
            var growth = last.Bytes - first.Bytes;
            if (last.Time - first.Time < _options.LeakMinWindow
                || growth < _options.LeakMinGrowthBytes
                || first.Bytes <= 0
                || (double)growth / first.Bytes < _options.LeakMinGrowthRatio)
                continue;

            // Mostly-rising, not one jump: at least 75% of the steps go up or stay flat.
            var rising = samples.Zip(samples.Skip(1)).Count(pair => pair.Second.Bytes >= pair.First.Bytes);
            if (rising < (samples.Count - 1) * 0.75) continue;

            var kind = AgentSignatures.AgentRootKind(process);
            var name = kind is { } k ? AgentSignatures.DisplayName(k) : process.Name;
            yield return new Finding(
                $"{name} (pid {process.Pid}) keeps growing: {growth >> 20} MB in {(int)(last.Time - first.Time).TotalMinutes} min",
                $"From {first.Bytes >> 20} MB to {last.Bytes >> 20} MB and still climbing. Restarting that session (resume it afterwards) gives the memory back.",
                growth, FindingKind.GrowingAgent);
        }
    }

    private IReadOnlyList<Finding> FindStalePiles(IEnumerable<AgentSession> sessions, DateTime now)
    {
        var findings = new List<Finding>();
        foreach (var session in sessions)
        {
            var piles = session.Root.SelfAndDescendants()
                .Select(x => x.Node)
                .Where(n => n != session.Root
                            && n.Process.StartTimeUtc != DateTime.MinValue
                            && now - n.Process.StartTimeUtc >= _options.StaleChildAge
                            && !IsActive(n.Process))
                .GroupBy(n => n.Process.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= _options.StaleChildMinCount);

            foreach (var pile in piles)
            {
                var bytes = pile.Sum(n => n.Process.PrivateBytes);
                var root = session.Root.Process;
                findings.Add(new Finding(
                    $"{pile.Count()} idle {pile.Key} under {AgentSignatures.DisplayName(session.Kind)} (pid {root.Pid})",
                    $"Older than {_options.StaleChildAge.TotalMinutes:0} min and not using CPU. They usually go away when that session restarts.",
                    bytes, FindingKind.IdlePile));
            }
        }
        return findings.OrderByDescending(f => f.Bytes).ToList();
    }


    /// <summary>
    /// Processes in live sessions that may be stuck. Waiting: a tool the agent started (not one of its standing
    /// helpers, not a server) whose whole tree has shown no CPU activity for a long time, e.g. a command waiting for
    /// a password or yes/no prompt nobody can see. Spinning: anything in the session, the agent included, that's
    /// kept a whole core busy without a break. An agent idle at its own prompt is normal, so agents themselves are
    /// only ever reported as spinning.
    /// </summary>
    private List<StuckProcess> FindStuck(IReadOnlyList<AgentSession> sessions, DateTime now)
    {
        var stuck = new List<StuckProcess>();
        var tracked = new HashSet<ProcessKey>();
        foreach (var session in sessions)
        {
            var nodes = session.Root.SelfAndDescendants().Select(x => x.Node).ToList();

            if (_options.StuckSpinningAfter is { } spinAfter)
            {
                foreach (var node in nodes)
                {
                    if (_busy.TryGetValue(node.Process.Key, out var busy) && busy.LastHigh == now && now - busy.Since >= spinAfter)
                        stuck.Add(new StuckProcess(session, node, StuckKind.Spinning, now - busy.Since));
                }
            }

            // Already reported as a pile of idle children: not repeated one by one.
            var piled = nodes.Where(n => n != session.Root && n.Process.StartTimeUtc != DateTime.MinValue
                                         && now - n.Process.StartTimeUtc >= _options.StaleChildAge && !IsActive(n.Process))
                .GroupBy(n => n.Process.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= _options.StaleChildMinCount).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var agent in nodes.Where(n => AgentSignatures.AgentRootKind(n.Process) is not null))
            foreach (var tool in agent.Children)
            {
                var p = tool.Process;
                if (AgentSignatures.AgentRootKind(p) is not null) continue;

                // Tracked for every tool, whether or not it can be reported now (a server, the setting off): a busy
                // child that has since exited was activity too. Without this, a shell whose build just finished would
                // count as idle for as long as the shell itself had been.
                var tree = tool.SelfAndDescendants().Select(x => x.Node).ToList();
                var members = string.Join(';', tree.Select(n => $"{n.Process.Key.Pid}:{n.Process.Key.StartTicks}").Order());
                var active = now - tree.Min(n => n.IdleFor);
                if (_toolActivity.TryGetValue(p.Key, out var seen))
                    active = seen.Members == members ? (seen.LastActive > active ? seen.LastActive : active) : now;
                _toolActivity[p.Key] = (active, members);
                tracked.Add(p.Key);

                if (_options.StuckWaitingAfter is not { } waitAfter || piled.Contains(p.Name)) continue;
                if (string.Equals(p.Name, "conhost.exe", StringComparison.OrdinalIgnoreCase)) continue; // the console host, always idle
                if (p.StartTimeUtc == DateTime.MinValue || agent.Process.StartTimeUtc == DateTime.MinValue
                    || p.StartTimeUtc - agent.Process.StartTimeUtc < _options.AgentStartupGrace) continue;
                if (tree.Any(n => n.Process.ListeningPorts.Count > 0 || n.Process.UdpPorts.Count > 0)) continue; // a server: idle between requests by design
                var idle = now - active;
                if (idle >= waitAfter && stuck.All(s => s.Node != tool))
                    stuck.Add(new StuckProcess(session, tool, StuckKind.Waiting, idle));
            }
        }
        foreach (var key in _toolActivity.Keys.Where(k => !tracked.Contains(k)).ToList())
            _toolActivity.Remove(key);
        return stuck;
    }

    private static Finding Describe(StuckProcess s)
    {
        var p = s.Node.Process;
        var agent = $"{AgentSignatures.DisplayName(s.Session.Kind)} (pid {s.Session.Root.Process.Pid})";
        return s.Kind == StuckKind.Waiting
            ? new Finding($"{p.Name} (pid {p.Pid}) under {agent} may be stuck: no activity for {(int)s.For.TotalMinutes} min",
                "A command the agent started that has done nothing since. It may be waiting for input nobody can see (a password or a yes/no prompt), or hung. " +
                "Check the agent's terminal; Stop… on the Sessions tab ends it, and the agent sees the command fail.",
                s.Node.TotalPrivateBytes, FindingKind.Stuck)
            : new Finding($"{p.Name} (pid {p.Pid}) under {agent} may be stuck: a whole CPU core busy for {(int)s.For.TotalMinutes} min",
                "It hasn't let up since. Long builds and tests do this too; if it shouldn't take this long, it may be stuck in a loop. " +
                "Stop… on the Sessions tab ends it (an agent itself is never stopped by AgentReclaim).",
                s.Node.TotalPrivateBytes, FindingKind.Stuck);
    }
    private bool IsActive(ProcessInfo p) =>
        p.CpuPercent >= Math.Min(_options.ActiveCpuPercent, _options.ActiveCorePercent / Math.Max(1, _options.CoreCount));

    private void UpdateActivity(ProcessSnapshot snapshot)
    {
        var now = snapshot.TakenAtUtc;
        var alive = new HashSet<ProcessKey>();
        foreach (var p in snapshot.Processes)
        {
            alive.Add(p.Key);
            if (!_activity.TryGetValue(p.Key, out var state))
                _activity[p.Key] = (now, now);
            else if (IsActive(p))
                _activity[p.Key] = (state.FirstSeen, now);

            // CPU is a share of the whole machine; a whole core is 100 / cores of it.
            if (p.CpuPercent >= _options.SpinningCorePercent / Math.Max(1, _options.CoreCount))
            {
                _busy[p.Key] = _busy.TryGetValue(p.Key, out var busy) && now - busy.LastHigh <= _options.SpinningGap
                    ? (busy.Since, now)
                    : (now, now);
            }
        }

        foreach (var key in _activity.Keys.Where(k => !alive.Contains(k)).ToList())
            _activity.Remove(key);
        foreach (var key in _busy.Keys.Where(k => !alive.Contains(k) || now - _busy[k].LastHigh > _options.SpinningGap).ToList())
            _busy.Remove(key);
    }

    private TimeSpan IdleFor(ProcessKey key, DateTime now) =>
        _activity.TryGetValue(key, out var state) ? now - state.LastActive : TimeSpan.Zero;
}
