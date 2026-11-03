using AgentReclaim.Core.Analysis;

namespace AgentReclaim.Core.Rules;

/// <summary>
/// How long to wait before the next poll. While any agent is running the pace stays the same with the window closed:
/// attribution depends on seeing a process under its agent, and one whose parent exits between polls can't be traced.
/// With no agents and no leftovers there's nothing to miss, and a new agent is picked up within one slow poll.
/// </summary>
public static class PollSchedule
{
    public static readonly TimeSpan Normal = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(15);

    /// <param name="windowVisible">Someone may be looking at live numbers.</param>
    /// <param name="result">The poll just taken.</param>
    /// <param name="workQueued">Something is waiting for a later poll, such as "Free safe leftovers".</param>
    public static TimeSpan Next(bool windowVisible, AnalysisResult result, bool workQueued) =>
        !windowVisible && !workQueued && result.Sessions.Count == 0 && result.Orphans.Count == 0 ? Quiet : Normal;
}
