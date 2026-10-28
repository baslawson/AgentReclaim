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
}

/// <summary>Something worth the user's attention that isn't a clean orphan, e.g. a pile of idle children.</summary>
public sealed record Finding(string Title, string Detail, long Bytes);

public sealed record AnalysisResult(
    ProcessSnapshot Snapshot,
    IReadOnlyList<AgentSession> Sessions,
    IReadOnlyList<OrphanTree> Orphans,
    IReadOnlyList<Finding> Findings)
{
    public long AgentPrivateBytes =>
        Sessions.Sum(s => s.Root.TotalPrivateBytes) + Orphans.Sum(o => o.Root.TotalPrivateBytes);

    public long OrphanPrivateBytes => Orphans.Sum(o => o.Root.TotalPrivateBytes);
}
