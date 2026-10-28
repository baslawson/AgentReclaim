using System.Collections.Concurrent;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Collection;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.App;

public sealed record AutoActionEvent(bool Success, string Message, ActionLogEntry? Entry);

/// <summary>
/// Polls on a background thread and applies the opt-in auto rules. The collector, analyzer and rule
/// engine are only ever touched from that thread.
/// </summary>
public sealed class MonitorService(
    ProcessCollector collector,
    Analyzer analyzer,
    AutoRuleEngine rules,
    ProcessTerminator terminator,
    Func<AppSettings> settings) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentQueue<SessionEndedMessage> _sessionEnds = new();

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(3);
    public bool Paused { get; set; }

    /// <summary>Raised on the background thread.</summary>
    public event Action<AnalysisResult>? Updated;

    public event Action<Exception>? Failed;

    /// <summary>Something an auto rule did. Raised on the background thread.</summary>
    public event Action<AutoActionEvent>? AutoAction;

    public void Start() => Task.Run(() => RunAsync(_stop.Token));

    public void RefreshNow() => _wake.Release();

    /// <summary>From the Claude Code hook (pipe thread).</summary>
    public void NotifySessionEnded(SessionEndedMessage message)
    {
        _sessionEnds.Enqueue(message);
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
        analyzer.Options = analyzer.Options with { DetectLeaks = current.LeakWarnings };

        while (_sessionEnds.TryDequeue(out var ended))
            rules.MarkSessionEnded(new ProcessKey(ended.AgentPid, ended.AgentStartTicks), DateTime.UtcNow);

        var result = analyzer.Analyze(collector.Collect());
        Updated?.Invoke(result);

        foreach (var plan in rules.ProcessesToThrottle(result, current))
        {
            rules.RecordThrottled(plan.Process.Key);
            var ok = Throttler.TryThrottle(plan.Process, out var error);
            Log.Info($"Throttle {plan.Process.Name} (pid {plan.Process.Pid}): {(ok ? "ok" : error)}");
            AutoAction?.Invoke(new AutoActionEvent(ok, ok
                ? $"Lowered priority of {plan.Process.Name} (pid {plan.Process.Pid}) under {AgentSignatures.DisplayName(plan.Kind)}: busy for {(int)plan.BusyFor.TotalSeconds}s."
                : $"Couldn't throttle {plan.Process.Name} (pid {plan.Process.Pid}): {error}", null));
        }

        // At most one automatic stop per fresh snapshot: a stop can take seconds, and anything planned
        // from this snapshot after it would be acting on stale information. The next poll re-plans.
        var candidates = rules.LeftoversToStop(result, current, Environment.ProcessId);
        Log.Debug($"Poll: {result.Snapshot.Processes.Count} processes, {result.Sessions.Count} sessions, {result.Orphans.Count} leftovers, {candidates.Count} auto-stop candidate(s).");
        if (candidates.Count > 0 && !Paused && settings().AutoStopLeftovers)
        {
            var tree = candidates[0];
            rules.RecordStopAttempt(tree);
            Log.Info($"Auto-stop: {tree.Root.Process.Name} (pid {tree.Root.Process.Pid}), {tree.Root.TotalCount} process(es), owner {tree.FormerOwner}.");
            var outcome = terminator.StopTree(tree, "auto: idle leftover", requireNoConnections: true);
            Log.Info($"Auto-stop result: {outcome.Message}");
            AutoAction?.Invoke(new AutoActionEvent(outcome.Success, $"Auto: {outcome.Message}", outcome.Entry));
            if (candidates.Count > 1) RefreshNow();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
