using System.Runtime.InteropServices;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;
using Microsoft.Win32.SafeHandles;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Actions;

public sealed record CapEvent(bool Success, string Message);

/// <summary>
/// Opt-in: caps the memory an agent session's processes may commit together, using one Windows job
/// object per agent. Hitting the cap makes the next allocation fail, so a runaway build or dev server
/// errors out instead of pushing the PC into swap.
/// <para>
/// The agent itself is never put in the job (nor a nested agent, nor a console host), so reaching the cap
/// fails a build, never the session. Nothing joins the job by inheritance (it allows silent breakaway): each
/// eligible process under the session is added explicitly as it is seen, every poll. So a nested agent or an
/// IDE launched from a capped shell is never capped, at the cost of a few seconds before a new process joins.
/// </para>
/// <para>
/// A job is kept while its agent is alive or Windows reports any process still in it, so turning the setting off lifts the cap
/// from leftovers too. Windows can't take a process out of a job, and an unnamed job can't be reopened, so
/// caps applied before AgentReclaim restarted stay until those processes exit.
/// </para>
/// </summary>
public sealed class SessionMemoryCaps : IDisposable
{
    public const int MinimumGb = 2;
    public const int MaximumGb = 512;

    /// <summary>
    /// Never moved into a job: agents, the console a session draws in, and everything the safety policy protects
    /// (IDEs, terminals, system processes). A node.exe whose command line can't be read yet might be an agent, so
    /// it waits for a later poll instead of being capped and staying capped (a process can't leave a job).
    /// </summary>
    internal static bool Eligible(ProcessInfo p) =>
        AgentSignatures.AgentRootKind(p) is null
        && !SafetyPolicy.IsProtected(p.Name)
        && !p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
        && !(p.CommandLine is null && p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase));

    private readonly Dictionary<ProcessKey, Cap> _caps = new();
    private readonly HashSet<ProcessKey> _failed = new();
    private readonly object _gate = new();

    /// <summary>Agents with a job right now, and how many of their processes are in it.</summary>
    public IReadOnlyDictionary<ProcessKey, int> Capped => _caps.ToDictionary(c => c.Key, c => c.Value.Members.Count);

    public static long ClampBytes(int gb) => (long)Math.Clamp(gb, MinimumGb, MaximumGb) * 1024 * 1024 * 1024;

    /// <summary>Call every poll. Returns what's worth telling the user (cap reached, a session couldn't be capped, caps lifted).</summary>
    public IReadOnlyList<CapEvent> Apply(AnalysisResult result, bool enabled, int capGb, int selfPid)
    {
        lock (_gate) return ApplyCore(result, enabled, capGb, selfPid);
    }

    /// <summary>
    /// Reads the setting inside the lock, so a <see cref="LiftAll"/> from another thread (the setting was just turned
    /// off) can't be undone by a poll that read the setting a moment earlier.
    /// </summary>
    public IReadOnlyList<CapEvent> Apply(AnalysisResult result, Func<(bool Enabled, int CapGb)> setting, int selfPid)
    {
        lock (_gate)
        {
            var (enabled, capGb) = setting();
            return ApplyCore(result, enabled, capGb, selfPid);
        }
    }

    /// <summary>
    /// Lifts every cap this instance holds, right away, without a snapshot: for turning the setting off while
    /// monitoring is paused, and on exit. Returns how many sessions had a cap. Safe from any thread.
    /// </summary>
    public int LiftAll()
    {
        lock (_gate) return LiftAllCore();
    }

    private int LiftAllCore()
    {
        var lifted = _caps.Values.Count(cap => cap.Limit != 0 && SetLimit(cap.Job, 0));
        Clear();
        _failed.Clear();
        return lifted;
    }

    private IReadOnlyList<CapEvent> ApplyCore(AnalysisResult result, bool enabled, int capGb, int selfPid)
    {
        var events = new List<CapEvent>();
        var alive = result.Snapshot.Processes.Select(p => p.Key).ToHashSet();

        if (!enabled)
        {
            var lifted = LiftAllCore();
            if (lifted > 0) events.Add(new CapEvent(true, $"Memory caps lifted from {lifted} agent session(s)."));
            return events;
        }

        var limit = ClampBytes(capGb);
        var now = result.Snapshot.TakenAtUtc;
        foreach (var session in result.Sessions)
        {
            var agent = session.Root.Process.Key;
            if (agent.StartTicks == 0 || _failed.Contains(agent)) continue;

            if (!_caps.TryGetValue(agent, out var cap))
            {
                var job = CreateJobObjectW(IntPtr.Zero, null);
                // Breakaway goes on before anything is assigned, so no child can slip in by inheritance.
                if (job.IsInvalid || !SetLimit(job, 0))
                {
                    // Reported once, not every poll.
                    _failed.Add(agent);
                    events.Add(new CapEvent(false, $"Couldn't cap the memory of {AgentSignatures.DisplayName(session.Kind)} (pid {agent.Pid})'s session: couldn't set up a job object (error {Marshal.GetLastWin32Error()})."));
                    job.Dispose();
                    continue;
                }
                _caps[agent] = cap = new Cap(job, session.Kind);
            }

            Enroll(agent, cap, session.Root, skipRoot: true);
        }

        // A capped session's leftovers (its agent gone, or just the launcher between them) keep their job, and so must
        // anything they start: nothing joins by inheritance.
        foreach (var leftover in result.Orphans)
        {
            var owner = leftover.Owner is { } o && _caps.ContainsKey(o) ? o : leftover.Agents.FirstOrDefault(_caps.ContainsKey);
            if (owner.StartTicks != 0)
                Enroll(owner, _caps[owner], leftover.Root, skipRoot: false);
        }

        void Enroll(ProcessKey agent, Cap cap, ProcessNode root, bool skipRoot)
        {
            var refused = new List<string>();
            foreach (var (node, depth) in root.SelfAndDescendants())
            {
                var p = node.Process;
                if ((skipRoot && depth == 0) || p.Pid == selfPid || p.Key.StartTicks == 0) continue;
                if (!Eligible(p)) continue; // not marked failed: a node.exe may become readable next poll
                if (cap.Members.Contains(p.Key)) continue;
                if (cap.Failed.TryGetValue(p.Key, out var retryAt) && now < retryAt) continue;

                var error = Assign(cap.Job, p);
                if (error is null)
                {
                    cap.Members.Add(p.Key);
                    cap.Failed.Remove(p.Key);
                    continue;
                }
                if (error == Gone) continue;
                // Retried every minute (its other job may end, or it was briefly unavailable); reported once per process.
                cap.Failed[p.Key] = now + RetryEvery;
                if (cap.Reported.Add(p.Key)) refused.Add($"{p.Name} (pid {p.Pid}): {error}");
            }

            if (refused.Count > 0)
                events.Add(new CapEvent(false,
                    $"The memory cap doesn't cover {refused.Count} process(es) in {AgentSignatures.DisplayName(cap.Kind)} (pid {agent.Pid})'s session, " +
                    $"so they run without it: {string.Join("; ", refused.Take(3))}{(refused.Count > 3 ? "; …" : "")}. AgentReclaim tries again every minute."));
        }

        foreach (var (agent, cap) in _caps)
        {
            if (cap.Limit != limit)
            {
                if (!SetLimit(cap.Job, limit))
                {
                    events.Add(new CapEvent(false, $"Couldn't set the memory cap for {AgentSignatures.DisplayName(cap.Kind)} (pid {agent.Pid}): error {Marshal.GetLastWin32Error()}."));
                    continue;
                }
                cap.Limit = limit;
                cap.Warned = false;
            }

            if (!cap.Warned && Peak(cap.Job) is { } peak && peak >= limit * 0.95)
            {
                cap.Warned = true;
                events.Add(new CapEvent(false,
                    $"{AgentSignatures.DisplayName(cap.Kind)} (pid {agent.Pid})'s session reached its {limit / (1024L * 1024 * 1024)} GB memory cap. Something it ran may have failed with an out-of-memory error."));
            }
        }

        // Keep a job while its agent, anything in it, or any leftover of that agent is still running: that's what
        // lets "off" lift the cap from leftovers, and lets leftovers not yet in the job (waiting for a retry, or a
        // node.exe whose command line couldn't be read) still join later. Windows' own count decides, not what we tracked.
        var referenced = result.Orphans
            .SelectMany(o => o.Owner is { } owner ? o.Agents.Append(owner) : o.Agents)
            .ToHashSet();
        foreach (var (agent, cap) in _caps.ToList())
        {
            cap.Members.IntersectWith(alive);
            foreach (var key in cap.Failed.Keys.Where(k => !alive.Contains(k)).ToList()) cap.Failed.Remove(key);
            cap.Reported.IntersectWith(alive);
            if (!alive.Contains(agent) && ActiveProcesses(cap.Job) == 0 && !referenced.Contains(agent))
            {
                cap.Job.Dispose();
                _caps.Remove(agent);
            }
        }
        _failed.IntersectWith(alive);
        return events;
    }

    /// <summary>0 removes the cap. Silent breakaway is always on, so children never join by inheritance.</summary>
    private static bool SetLimit(SafeFileHandle job, long bytes)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
        if (bytes > 0)
        {
            info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_JOB_MEMORY;
            info.JobMemoryLimit = (UIntPtr)(ulong)bytes;
        }
        return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
    }

    /// <summary>Processes Windows says are in the job; if it can't say, assume some, so the handle is kept.</summary>
    private static uint ActiveProcesses(SafeFileHandle job) =>
        QueryJobAccounting(job, JobObjectBasicAccountingInformation, out var info,
            Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
            ? info.ActiveProcesses
            : 1;

    private static long? Peak(SafeFileHandle job) =>
        QueryInformationJobObject(job, JobObjectExtendedLimitInformation, out var info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero)
            ? (long)info.PeakJobMemoryUsed
            : null;

    private const string Gone = "exited";
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(1);

    /// <summary>Null if the process is in the job, <see cref="Gone"/> if it exited, otherwise why it couldn't be added.</summary>
    private static string? Assign(SafeFileHandle job, ProcessInfo process)
    {
        using var handle = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)process.Pid);
        if (handle.IsInvalid)
        {
            var openError = Marshal.GetLastWin32Error();
            return openError == 87 ? Gone : $"Windows won't let AgentReclaim change it (error {openError}; elevated or protected?)";
        }
        // Make sure the pid still belongs to the process we decided about.
        if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created != process.Key.StartTicks) return Gone;
        if (IsProcessInJob(handle, job, out var already) && already) return null;
        if (AssignProcessToJobObject(job, handle)) return null;
        var error = Marshal.GetLastWin32Error();
        // It may have exited since the snapshot (its process object kept alive by another handle): not a cap failure.
        if (WaitForSingleObject(handle, 0) == WAIT_OBJECT_0) return Gone;
        return error == 5
            ? "it is already in another job that can't be combined with the cap (access denied)"
            : $"couldn't add it to the job (error {error})";
    }

    /// <summary>For tests: the job holding this agent's processes (null if none).</summary>
    internal SafeFileHandle? JobOf(ProcessKey agent) => _caps.GetValueOrDefault(agent)?.Job;

    private void Clear()
    {
        foreach (var cap in _caps.Values) cap.Job.Dispose();
        _caps.Clear();
    }

    public void Dispose()
    {
        lock (_gate) Clear();
    }

    private sealed class Cap(SafeFileHandle job, AgentKind kind)
    {
        public SafeFileHandle Job { get; } = job;
        public AgentKind Kind { get; } = kind;
        public long Limit { get; set; }
        public bool Warned { get; set; }
        public HashSet<ProcessKey> Members { get; } = new();
        public Dictionary<ProcessKey, DateTime> Failed { get; } = new();
        public HashSet<ProcessKey> Reported { get; } = new();
    }
}
