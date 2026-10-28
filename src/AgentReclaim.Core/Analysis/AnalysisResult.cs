using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Analysis;

public sealed class ProcessNode
{
    public ProcessNode(ProcessInfo process, TimeSpan idleFor, IReadOnlyList<ProcessNode> children)
    {
        Process = process;
        IdleFor = idleFor;
        Children = children;
        TotalPrivateBytes = process.PrivateBytes + children.Sum(c => c.TotalPrivateBytes);
        TotalCpuPercent = process.CpuPercent + children.Sum(c => c.TotalCpuPercent);
        TotalCount = 1 + children.Sum(c => c.TotalCount);
    }

    public ProcessInfo Process { get; }

    /// <summary>How long this process has been under the activity threshold, as far as we've observed.</summary>
    public TimeSpan IdleFor { get; }

    public IReadOnlyList<ProcessNode> Children { get; }
    public long TotalPrivateBytes { get; }
    public double TotalCpuPercent { get; }
    public int TotalCount { get; }

    public IEnumerable<(ProcessNode Node, int Depth)> SelfAndDescendants(int depth = 0)
    {
        yield return (this, depth);
        foreach (var child in Children)
        foreach (var item in child.SelfAndDescendants(depth + 1))
            yield return item;
    }
}

/// <summary>A live agent and every process running under it.</summary>
public sealed record AgentSession(AgentKind Kind, ProcessNode Root);

public enum Attribution
{
    /// <summary>We saw this process running under the agent before the agent exited.</summary>
    Observed,

    /// <summary>Its parent was already gone when we first saw it; owner guessed from its path.</summary>
    Heuristic,
}

/// <summary>Processes an agent left behind after the agent (or the shell it used) exited.</summary>
public sealed record OrphanTree(ProcessNode Root, AgentKind Kind, Attribution Attribution, string FormerOwner, int? OwnerPid = null, long OwnerStartTicks = 0)
{
    /// <summary>The exact agent instance that owned this tree, when known (pid plus start time, so a reused pid never matches).</summary>
    public ProcessKey? Owner => OwnerPid is { } pid && OwnerStartTicks != 0 ? new ProcessKey(pid, OwnerStartTicks) : null;

    /// <summary>Every agent it was seen running under (just <see cref="Owner"/> unless agents were nested).</summary>
    public IReadOnlySet<ProcessKey> Agents { get; init; } = new HashSet<ProcessKey>();
}

/// <summary>Something worth the user's attention that isn't a clean orphan, e.g. a pile of idle children.</summary>
public sealed record Finding(string Title, string Detail, long Bytes, FindingKind Kind = FindingKind.Other);

public enum FindingKind
{
    Other,
    GrowingAgent,
    IdlePile,
    Stuck,
}

public sealed record AnalysisResult(
    ProcessSnapshot Snapshot,
    IReadOnlyList<AgentSession> Sessions,
    IReadOnlyList<OrphanTree> Orphans,
    IReadOnlyList<Finding> Findings)
{
    public long AgentPrivateBytes =>
        Sessions.Sum(s => s.Root.TotalPrivateBytes) + Orphans.Sum(o => o.Root.TotalPrivateBytes);

    public long OrphanPrivateBytes => Orphans.Sum(o => o.Root.TotalPrivateBytes);

    /// <summary>Every process's subtree (same parent-must-be-older rule), for stopping something outside a session or leftover.</summary>
    public IReadOnlyDictionary<ProcessKey, ProcessNode> Nodes { get; init; } = new Dictionary<ProcessKey, ProcessNode>();

    /// <summary>Processes in live sessions that may be stuck (also listed in <see cref="Findings"/>).</summary>
    public IReadOnlyList<StuckProcess> Stuck { get; init; } = [];
}

public enum StuckKind
{
    /// <summary>A tool the agent started has shown no activity for a long time: hung, or waiting for input nobody sees.</summary>
    Waiting,

    /// <summary>Kept a whole CPU core busy for a long time without a break: possibly stuck in a loop.</summary>
    Spinning,
}

/// <summary>A process in a live session that may be stuck. A guess from CPU over time; nothing is done about it automatically.</summary>
public sealed record StuckProcess(AgentSession Session, ProcessNode Node, StuckKind Kind, TimeSpan For)
{
    /// <summary>Short, for a status column: "Stuck? No activity for 12 min".</summary>
    public string Status => Kind == StuckKind.Waiting
        ? $"Stuck? No activity for {(int)For.TotalMinutes} min"
        : $"Stuck? Busy for {(int)For.TotalMinutes} min";
}
