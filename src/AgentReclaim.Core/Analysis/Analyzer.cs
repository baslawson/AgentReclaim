using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Analysis;

public sealed record AnalyzerOptions
{
    /// <summary>CPU at or above this counts as "doing something".</summary>
    public double ActiveCpuPercent { get; init; } = 0.5;

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

        // A parent only counts if it started before the child; otherwise the PID was reused.
        ProcessInfo? ParentOf(ProcessInfo p) =>
            p.ParentPid != p.Pid && byPid.TryGetValue(p.ParentPid, out var parent)
            && (parent.StartTimeUtc <= p.StartTimeUtc || p.StartTimeUtc == DateTime.MinValue)
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
        foreach (var p in snapshot.Processes)
        {
            if (SessionRootOf(p) is { } root)
                lineage.Record(p.Key, new LineageEntry(AgentSignatures.AgentRootKind(root)!.Value, root.Pid, root.Name, OwnerStartTicks: root.Key.StartTicks));
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

        ProcessNode Build(ProcessInfo p) => new(
            p,
            IdleFor(p.Key, now),
            children.TryGetValue(p.Pid, out var list) ? list.Select(Build).ToList() : []);

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
                : new OrphanTree(Build(p), info.Kind, Attribution.Observed, $"{info.OwnerName} (pid {info.OwnerPid})", info.OwnerPid, info.OwnerStartTicks));
        }
        orphans.Sort((a, b) => b.Root.TotalPrivateBytes.CompareTo(a.Root.TotalPrivateBytes));

        // Never prune from an empty snapshot: it means collection failed, not that everything exited.
        if (snapshot.Processes.Count > 0)
            lineage.RetainOnly(snapshot.Processes.Select(p => p.Key).ToHashSet());
        lineage.SaveIfDirty();

        var findings = FindLeaks(sessions, now).Concat(FindStalePiles(sessions, now)).ToList();
        return new AnalysisResult(snapshot, sessions, orphans, findings);
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
                growth);
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
                            && n.Process.CpuPercent < _options.ActiveCpuPercent)
                .GroupBy(n => n.Process.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= _options.StaleChildMinCount);

            foreach (var pile in piles)
            {
                var bytes = pile.Sum(n => n.Process.PrivateBytes);
                var root = session.Root.Process;
                findings.Add(new Finding(
                    $"{pile.Count()} idle {pile.Key} under {AgentSignatures.DisplayName(session.Kind)} (pid {root.Pid})",
                    $"Older than {_options.StaleChildAge.TotalMinutes:0} min and not using CPU. They usually go away when that session restarts.",
                    bytes));
            }
        }
        return findings.OrderByDescending(f => f.Bytes).ToList();
    }

    private void UpdateActivity(ProcessSnapshot snapshot)
    {
        var now = snapshot.TakenAtUtc;
        var alive = new HashSet<ProcessKey>();
        foreach (var p in snapshot.Processes)
        {
            alive.Add(p.Key);
            if (!_activity.TryGetValue(p.Key, out var state))
                _activity[p.Key] = (now, now);
            else if (p.CpuPercent >= _options.ActiveCpuPercent)
                _activity[p.Key] = (state.FirstSeen, now);
        }

        foreach (var key in _activity.Keys.Where(k => !alive.Contains(k)).ToList())
            _activity.Remove(key);
    }

    private TimeSpan IdleFor(ProcessKey key, DateTime now) =>
        _activity.TryGetValue(key, out var state) ? now - state.LastActive : TimeSpan.Zero;
}
