using System.Collections.Concurrent;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Cleanup;
using AgentReclaim.Core.Collection;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.App;

/// <param name="Notify">Show a notification as well as adding it to Activity.</param>
public sealed record AutoActionEvent(bool Success, string Message, ActionLogEntry? Entry, bool Notify = true);

/// <summary>One poll: the analysis plus the port and per-project views built from it.</summary>
/// <param name="SafeLeftovers">Leftovers that pass every auto-stop check right now (what the tray's "Free safe leftovers" would stop).</param>
public sealed record MonitorUpdate(AnalysisResult Result, IReadOnlyList<PortEntry> Ports, IReadOnlyList<ProjectSummary> Projects,
    IReadOnlyList<OrphanTree> SafeLeftovers);

/// <summary>
/// Polls on a background thread and applies the opt-in auto rules. The collector, analyzer, rule
/// engine, leftover watch and memory caps are only ever touched from that thread.
/// </summary>
public sealed class MonitorService(
    ProcessCollector collector,
    Analyzer analyzer,
    AutoRuleEngine rules,
    ProcessTerminator terminator,
    Func<AppSettings> settings,
    MemoryHistory? history = null,
    ActionLogTail? actionLogTail = null) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentQueue<SessionEndedMessage> _sessionEnds = new();
    private readonly ConcurrentDictionary<ProcessKey, byte> _freeRequested = new();
    private readonly ProjectResolver _projects = new();
    private readonly MemoryAlertRule _memoryAlert = new();
    private readonly LeftoverWatch _leftoverWatch = new();
    private readonly StuckWatch _stuckWatch = new();
    private readonly SessionMemoryCaps _caps = new();
    private int _batchStops;
    private long _batchFreed;
    private int _batchSkipped;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Pausing also drops any "Free safe leftovers" still queued: they were confirmed for now, not for later.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value) _freeRequested.Clear();
        }
    }

    private volatile bool _paused;

    public MemoryHistory? History => history;

    /// <summary>Raised on the background thread.</summary>
    public event Action<MonitorUpdate>? Updated;

    /// <summary>Memory stayed above the alert threshold. Raised on the background thread, once per episode.</summary>
    public event Action<MemoryAlert>? MemoryLow;

    /// <summary>A leftover appeared (only when that notification is turned on). Raised on the background thread.</summary>
    public event Action<LeftoverNotice>? LeftoverAppeared;

    /// <summary>A process in a session may be stuck (only when that notification is turned on). Raised on the background thread.</summary>
    public event Action<StuckNotice>? StuckFound;

    /// <summary>A new history sample was taken. Raised on the background thread.</summary>
    public event Action? HistoryUpdated;

    public event Action<Exception>? Failed;

    /// <summary>
    /// Something an auto rule, the tray's "Free safe leftovers", the memory caps or an agent (through MCP)
    /// did. Raised on the background thread.
    /// </summary>
    public event Action<AutoActionEvent>? AutoAction;

    public void Start() => Task.Run(() => RunAsync(_stop.Token));

    public void RefreshNow() => _wake.Release();

    /// <summary>
    /// Settings were saved (UI thread). Turning memory caps off takes effect at once, even while monitoring is
    /// paused (polls, which normally apply settings, don't run then).
    /// </summary>
    public void SettingsChanged(AppSettings current)
    {
        if (!current.CapSessionMemory && _caps.LiftAll() is var lifted and > 0)
            AutoAction?.Invoke(new AutoActionEvent(true, $"Memory caps lifted from {lifted} agent session(s).", null));
        RefreshNow();
    }

    /// <summary>From the Claude Code hook (pipe thread).</summary>
    public void NotifySessionEnded(SessionEndedMessage message)
    {
        _sessionEnds.Enqueue(message);
        RefreshNow();
    }

    /// <summary>
    /// The user confirmed stopping these leftovers. Each is stopped on a later poll only if it still passes
    /// every check on that fresh snapshot; anything that no longer does is skipped.
    /// </summary>
    public void RequestFree(IEnumerable<ProcessKey> roots)
    {
        foreach (var key in roots) _freeRequested[key] = 0;
        RefreshNow();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!Paused)
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    Failed?.Invoke(ex);
                }
            }

            try
            {
                await Task.WhenAny(Task.Delay(Interval, token), _wake.WaitAsync(token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Poll()
    {
        var current = settings();
        var selfPid = Environment.ProcessId;
        analyzer.Options = analyzer.Options with
        {
            DetectLeaks = current.LeakWarnings,
            StuckWaitingAfter = TimeSpan.FromMinutes(Math.Max(1, current.StuckWaitingMinutes)),
            StuckSpinningAfter = TimeSpan.FromMinutes(Math.Max(1, current.StuckSpinningMinutes)),
        };

        while (_sessionEnds.TryDequeue(out var ended))
            rules.MarkSessionEnded(new ProcessKey(ended.AgentPid, ended.AgentStartTicks), DateTime.UtcNow);

        var result = analyzer.Analyze(collector.Collect());
        _projects.Retain(result.Snapshot.Processes.Select(p => p.Key));
        var projects = Insights.Projects(result, _projects.ProjectOf);
        var safe = rules.SafeLeftovers(result, current, selfPid);
        Updated?.Invoke(new MonitorUpdate(result, Insights.Ports(result, _projects.ProjectOf), projects, safe));

        // Stops an agent made through MCP (a separate process) show up live.
        foreach (var entry in actionLogTail?.ReadNew() ?? [])
            if (entry.Phase == "done" && entry.Trigger?.StartsWith("mcp:", StringComparison.Ordinal) == true)
                AutoAction?.Invoke(new AutoActionEvent(entry.Success, $"{entry.Trigger[4..].Trim()} through MCP: {entry.Message}", entry));

        if (history?.Add(DateTime.UtcNow, result, projects) == true) HistoryUpdated?.Invoke();

        if (_leftoverWatch.Evaluate(result, current.NotifyNewLeftovers, _projects.ProjectOf) is { } notice)
        {
            Log.Info($"New leftover: {notice.Text}");
            LeftoverAppeared?.Invoke(notice);
        }

        if (_stuckWatch.Evaluate(result, current.NotifyStuck) is { } stuckNotice)
        {
            Log.Info($"Possibly stuck: {stuckNotice.Text}");
            StuckFound?.Invoke(stuckNotice);
        }

        if (_memoryAlert.Evaluate(result, current, DateTime.UtcNow, () => WslManager.VmMemoryBytes(result.Snapshot)) is { } alert)
        {
            Log.Info($"Low memory alert: {alert.Text}");
            MemoryLow?.Invoke(alert);
        }

        foreach (var cap in _caps.Apply(result, () => (settings().CapSessionMemory, settings().SessionMemoryCapGb), selfPid))
        {
            Log.Info($"Memory cap: {cap.Message}");
            AutoAction?.Invoke(new AutoActionEvent(cap.Success, cap.Message, null));
        }

        foreach (var plan in rules.ProcessesToThrottle(result, current))
        {
            rules.RecordThrottled(plan.Process.Key);
            var ok = Throttler.TryThrottle(plan.Process, out var error);
            Log.Info($"Throttle {plan.Process.Name} (pid {plan.Process.Pid}): {(ok ? "ok" : error)}");
            AutoAction?.Invoke(new AutoActionEvent(ok, ok
                ? $"Lowered priority of {plan.Process.Name} (pid {plan.Process.Pid}) under {AgentSignatures.DisplayName(plan.Kind)}: busy for {(int)plan.BusyFor.TotalSeconds}s."
                : $"Couldn't throttle {plan.Process.Name} (pid {plan.Process.Pid}): {error}", null));
        }

        // At most one stop per fresh snapshot: a stop can take seconds, and anything planned from this
        // snapshot after it would be acting on stale information. The next poll re-plans.
        var stuckToStop = rules.StuckToStop(result, current, selfPid);
        if (stuckToStop.Count > 0 && !Paused)
        {
            var stuck = stuckToStop[0];
            rules.RecordStopAttempt(stuck);
            var agent = AgentSignatures.DisplayName(stuck.Session.Kind);
            var why = stuck.Kind == StuckKind.Waiting
                ? $"no activity for {(int)stuck.For.TotalMinutes} min"
                : $"a whole CPU core busy for {(int)stuck.For.TotalMinutes} min";
            Log.Info($"Auto-stop stuck: {stuck.Node.Process.Name} (pid {stuck.Node.Process.Pid}) under {agent}, {why}.");
            // The terminator re-reads the tree (refusing if it gained processes) and the connections first.
            var outcome = terminator.StopSubtree(stuck.Node, agent, "live session", $"auto: stuck process ({why})",
                requireNoConnections: true, requireNoListeners: true,
                stillWanted: () => !Paused && AutoRuleEngine.StillWanted(stuck, settings()));
            Log.Info($"Auto-stop stuck result: {outcome.Message}");
            AutoAction?.Invoke(new AutoActionEvent(outcome.Success,
                outcome.Success ? $"Auto: stopped {stuck.Node.Process.Name} under {agent} ({why}); the agent sees it fail. {outcome.Message}" : $"Auto: {outcome.Message}",
                outcome.Entry));
            if (stuckToStop.Count > 1) RefreshNow();
            return;
        }

        var candidates = rules.LeftoversToStop(result, current, selfPid);
        Log.Debug($"Poll: {result.Snapshot.Processes.Count} processes, {result.Sessions.Count} sessions, {result.Orphans.Count} leftovers, {candidates.Count} auto-stop candidate(s).");
        if (candidates.Count > 0 && !Paused && settings().AutoStopLeftovers)
        {
            var tree = candidates[0];
            rules.RecordStopAttempt(tree);
            Log.Info($"Auto-stop: {tree.Root.Process.Name} (pid {tree.Root.Process.Pid}), {tree.Root.TotalCount} process(es), owner {tree.FormerOwner}.");
            var outcome = terminator.StopTree(tree, "auto: idle leftover", requireNoConnections: true,
                stillWanted: () => !Paused && settings().AutoStopLeftovers);
            Log.Info($"Auto-stop result: {outcome.Message}");
            AutoAction?.Invoke(new AutoActionEvent(outcome.Success, $"Auto: {outcome.Message}", outcome.Entry));
            if (candidates.Count > 1 || !_freeRequested.IsEmpty) RefreshNow();
            return;
        }

        FreeOneRequested(safe);
    }

    /// <summary>The tray's "Free safe leftovers": one stop per poll, each re-checked against this poll's snapshot.</summary>
    private void FreeOneRequested(IReadOnlyList<OrphanTree> safe)
    {
        if (_freeRequested.IsEmpty || Paused) return;

        var safeByKey = safe.ToDictionary(t => t.Root.Process.Key);
        foreach (var key in _freeRequested.Keys.Where(k => !safeByKey.ContainsKey(k)).ToList())
            if (_freeRequested.TryRemove(key, out _)) _batchSkipped++;

        var tree = safe.FirstOrDefault(t => _freeRequested.ContainsKey(t.Root.Process.Key));
        if (tree is not null && _freeRequested.TryRemove(tree.Root.Process.Key, out _))
        {
            rules.RecordStopAttempt(tree);
            var outcome = terminator.StopTree(tree, "manual: free safe leftovers (tray)", requireNoConnections: true);
            Log.Info($"Free safe leftovers: {outcome.Message}");
            if (outcome.Success)
            {
                _batchStops++;
                _batchFreed += outcome.FreedBytes;
            }
            AutoAction?.Invoke(new AutoActionEvent(outcome.Success, outcome.Message, outcome.Entry, Notify: !outcome.Success));
        }

        if (!_freeRequested.IsEmpty)
        {
            RefreshNow();
            return;
        }
        if (_batchStops + _batchSkipped == 0) return;

        var summary = $"Freed {_batchStops} leftover(s), {Format.Bytes(_batchFreed)}." +
                      (_batchSkipped > 0 ? $" Skipped {_batchSkipped} that no longer passed the checks." : "");
        AutoAction?.Invoke(new AutoActionEvent(true, summary, null));
        _batchStops = 0;
        _batchFreed = 0;
        _batchSkipped = 0;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        history?.Save();
        // With caps off, make sure none are left on (the poll that would lift them may not have run, e.g. while
        // paused). With caps on, they stay on the processes: Windows can't take a process out of a job.
        if (!settings().CapSessionMemory) _caps.LiftAll();
    }
}
