using System.Globalization;

namespace AgentReclaim.Core.Diagnostics;

/// <summary>
/// Decides which polls are worth a log line. A line for every poll was nearly all of the debug log and said little, so
/// a poll is only logged when the sessions, leftovers or stop candidates change, when it's slow, or as a heartbeat.
/// Once an hour there's a summary at Info level, so poll times and the app's own memory can be followed over days
/// without debug logging.
/// </summary>
public sealed class PollLog
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Summary = TimeSpan.FromHours(1);
    public const double SlowMilliseconds = 1000;

    private (int Sessions, int Leftovers, int Candidates)? _state;
    private DateTime _lastLine;
    private DateTime _lastSummary;
    private Stats _sinceLine;
    private Stats _sinceSummary;

    private struct Stats
    {
        public int Polls;
        public double Total;
        public double Slowest;

        public void Add(double milliseconds)
        {
            Polls++;
            Total += milliseconds;
            Slowest = Math.Max(Slowest, milliseconds);
        }

        public readonly string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"{Polls} poll(s): average {Total / Math.Max(1, Polls):0} ms, slowest {Slowest:0} ms");
    }

    /// <summary>The line to write for this poll, if any.</summary>
    /// <param name="now">The poll's time.</param>
    /// <param name="processes">Processes in the snapshot. Too changeable to count as a change by itself.</param>
    /// <param name="sessions">Live agent sessions.</param>
    /// <param name="leftovers">Leftovers.</param>
    /// <param name="candidates">Leftovers that could be stopped automatically now.</param>
    /// <param name="elapsedMilliseconds">How long the poll took.</param>
    /// <param name="appPrivateBytes">Read only when a line is written. Null if unknown.</param>
    public (LogLevel Level, string Text)? Note(DateTime now, int processes, int sessions, int leftovers, int candidates,
        double elapsedMilliseconds, Func<long?> appPrivateBytes)
    {
        _sinceLine.Add(elapsedMilliseconds);
        _sinceSummary.Add(elapsedMilliseconds);

        var state = (sessions, leftovers, candidates);
        if (_state is null) _lastSummary = now; // the first hour starts here
        var changed = _state != state;
        _state = state;

        var summaryDue = now - _lastSummary >= Summary;
        if (!summaryDue && !changed && elapsedMilliseconds < SlowMilliseconds && now - _lastLine < Heartbeat) return null;

        var since = summaryDue ? _sinceSummary : _sinceLine;
        var memory = appPrivateBytes() is { } bytes ? $" App: {bytes >> 20} MB." : "";
        var text = $"Poll: {processes} processes, {sessions} sessions, {leftovers} leftovers, {candidates} auto-stop candidate(s). " +
                   $"Last {since.Describe()}.{memory}";

        _lastLine = now;
        _sinceLine = default;
        if (!summaryDue) return (LogLevel.Debug, text);

        _lastSummary = now;
        _sinceSummary = default;
        return (LogLevel.Info, text);
    }
}
