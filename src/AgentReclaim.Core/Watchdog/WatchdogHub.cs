using System.Threading.Channels;

namespace AgentReclaim.Core.Watchdog;

/// <summary>What must outlive the app, so an alert nobody has seen yet still reaches someone after a restart.</summary>
public sealed class WatchdogMail
{
    /// <summary>Alerts for everyone that no session has taken yet: the next to connect gets them.</summary>
    public List<WatchdogAlert> Unclaimed { get; set; } = [];

    /// <summary>Sessions between connections, and what's waiting for each.</summary>
    public Dictionary<string, List<WatchdogAlert>> Waiting { get; set; } = [];

    /// <summary>Alerts for everyone, still waiting for some session, that another already had: not to be handed on again.</summary>
    public List<WatchdogAlert> Handed { get; set; } = [];
}

/// <summary>
/// Who gets which alert. An alert for a session that's between connections waits for it, since an agent's listener
/// expires and is re-armed, and whatever happens in that gap must not be lost. An alert for everyone with nobody to
/// give it to waits for whoever connects next. Thread-safe.
/// </summary>
public sealed class WatchdogHub
{
    public const int MaxWaiting = 100;

    private sealed class Entry
    {
        public readonly Queue<WatchdogAlert> Waiting = new();
        public Channel<WatchdogAlert>? Live;
        public long Connection;

        /// <summary>Time away while the PC was awake. Sleep doesn't count: a session can't reconnect while it's asleep.</summary>
        public TimeSpan Away;
    }

    private const int HandedRemembered = 1000;

    private readonly Dictionary<string, Entry> _sessions = [];
    private readonly List<WatchdogAlert> _unclaimed = [];

    /// <summary>Alerts for everyone that some connection has taken, so no one else needs to be given them.</summary>
    /// <summary>How many connections hold or sent each: one that fails to send it doesn't undo another that did.</summary>
    private readonly Dictionary<WatchdogAlert, int> _handed = [];
    private readonly Queue<WatchdogAlert> _handedOrder = new();
    private DateTime? _lastPrune;
    private long _nextConnection;

    /// <summary>Sessions that are connected, or were recently.</summary>
    public IReadOnlyList<(string Session, bool Connected)> Sessions()
    {
        lock (_sessions) return _sessions.Select(kv => (kv.Key, kv.Value.Live is not null)).ToList();
    }

    /// <summary>A new connection for a session replaces its old one, which is told to end through its reader.</summary>
    public (long Connection, ChannelReader<WatchdogAlert> Reader) Connect(string session, DateTime now)
    {
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(session, out var entry)) _sessions[session] = entry = new Entry();
            entry.Live?.Writer.TryComplete();
            entry.Live = Channel.CreateUnbounded<WatchdogAlert>(new UnboundedChannelOptions { SingleReader = false });
            entry.Connection = ++_nextConnection;
            entry.Away = TimeSpan.Zero;
            var live = entry.Live;
            var given = new HashSet<WatchdogAlert>(); // one alert can be both unclaimed and in its own queue
            foreach (var alert in _unclaimed)
                if (given.Add(alert)) Hand(live, alert);
            _unclaimed.Clear();
            while (entry.Waiting.TryDequeue(out var alert))
                if (given.Add(alert)) Hand(live, alert);
            // Alerts for everyone that only sessions between connections hold: nobody has had them yet.
            foreach (var other in _sessions.Values.Where(e => e != entry && e.Live is null))
                foreach (var alert in other.Waiting.Where(a => a.Session is null && !_handed.ContainsKey(a)).ToList())
                    if (given.Add(alert)) Hand(live, alert);
            return (entry.Connection, live.Reader);
        }
    }

    /// <param name="unsent">What the connection hadn't sent yet: kept for the next one.</param>
    public void Disconnect(string session, long connection, IEnumerable<WatchdogAlert> unsent, DateTime now)
    {
        lock (_sessions)
        {
            var unsentList = unsent.ToList();
            foreach (var alert in unsentList) Unhand(alert); // taken, but never got there
            if (!_sessions.TryGetValue(session, out var entry))
            {
                foreach (var alert in unsentList.Where(a => a.Session is null)) Claimless(alert);
                return;
            }
            foreach (var alert in unsentList)
                if (entry.Connection == connection || entry.Live is not { } live || !Hand(live, alert))
                    Enqueue(entry, alert);
            if (entry.Connection != connection) return; // already replaced
            if (entry.Live is { } own)
            {
                own.Writer.TryComplete();
                // Published after the connection's last read, before this: still in the channel, never sent.
                while (own.Reader.TryRead(out var left))
                {
                    if (unsentList.Contains(left)) continue; // already handed back above
                    Unhand(left);
                    Enqueue(entry, left);
                }
            }
            entry.Live = null;
            entry.Away = TimeSpan.Zero;
        }
    }

    public void Publish(WatchdogAlert alert)
    {
        lock (_sessions)
        {
            if (alert.Session is null && _sessions.Count == 0)
            {
                Claimless(alert);
                return;
            }
            foreach (var (session, entry) in _sessions)
            {
                if (alert.Session is not null && alert.Session != session) continue;
                if (entry.Live is not { } live || !Hand(live, alert)) Enqueue(entry, alert);
            }
        }
    }

    /// <summary>
    /// Forgets sessions that have been away this long: they've ended. Only time awake counts, so a session isn't taken as
    /// ended because the PC slept. Alerts for everyone that an ended one held and nobody else has had go to whoever
    /// connects next.
    /// </summary>
    /// <param name="longestStep">The most that one call can add: a longer gap since the last call was sleep.</param>
    public void Prune(TimeSpan olderThan, DateTime now, TimeSpan longestStep)
    {
        lock (_sessions)
        {
            var step = _lastPrune is { } last && now > last ? now - last : TimeSpan.Zero;
            if (step > longestStep) step = longestStep;
            _lastPrune = now;
            foreach (var (session, entry) in _sessions.ToList())
            {
                if (entry.Live is not null) continue;
                entry.Away += step;
                if (entry.Away < olderThan) continue;
                foreach (var alert in entry.Waiting.Where(a => a.Session is null && !_handed.ContainsKey(a))) Claimless(alert);
                _sessions.Remove(session);
            }
        }
    }

    /// <summary>
    /// Ends every connection. What they hadn't sent yet goes back in their queues now, so a save straight after has it:
    /// their own clean-up runs later, maybe after the app has gone.
    /// </summary>
    public void CloseAll()
    {
        lock (_sessions)
            foreach (var entry in _sessions.Values)
            {
                if (entry.Live is not { } live) continue;
                live.Writer.TryComplete();
                while (live.Reader.TryRead(out var alert))
                {
                    Unhand(alert);
                    Enqueue(entry, alert);
                }
            }
    }

    /// <summary>What to save. Alerts already handed to a connection count as gone.</summary>
    public WatchdogMail Mail()
    {
        lock (_sessions)
            return new WatchdogMail
            {
                Unclaimed = _unclaimed.ToList(),
                Waiting = _sessions.Where(kv => kv.Value.Waiting.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Waiting.ToList()),
                Handed = _sessions.Values.SelectMany(e => e.Waiting).Where(_handed.ContainsKey).Distinct().ToList(),
            };
    }

    /// <summary>From the last run. A session in it has the prune age, counted from now, to come back.</summary>
    public void Restore(WatchdogMail mail)
    {
        lock (_sessions)
        {
            foreach (var alert in mail.Handed ?? [])
                if (_handed.TryAdd(alert, 1)) _handedOrder.Enqueue(alert);
            foreach (var alert in mail.Unclaimed) Claimless(alert);
            foreach (var (session, alerts) in mail.Waiting)
            {
                if (!WatchdogEngine.ValidSession(session)) continue;
                if (!_sessions.TryGetValue(session, out var entry)) _sessions[session] = entry = new Entry();
                foreach (var alert in alerts) Enqueue(entry, alert);
            }
        }
    }

    private bool Hand(Channel<WatchdogAlert> live, WatchdogAlert alert)
    {
        if (!live.Writer.TryWrite(alert)) return false;
        if (alert.Session is null)
        {
            if (_handed.TryGetValue(alert, out var takers)) _handed[alert] = takers + 1;
            else
            {
                _handed[alert] = 1;
                _handedOrder.Enqueue(alert);
                while (_handedOrder.Count > HandedRemembered)
                {
                    var old = _handedOrder.Dequeue();
                    if (!_handedOrder.Contains(old)) _handed.Remove(old); // not if it was handed again since
                }
            }
        }
        return true;
    }

    private void Unhand(WatchdogAlert alert)
    {
        if (!_handed.TryGetValue(alert, out var takers)) return;
        if (takers > 1) _handed[alert] = takers - 1;
        else _handed.Remove(alert); // left in the order queue; harmless, it's only for the cap
    }

    private void Claimless(WatchdogAlert alert)
    {
        if (_unclaimed.Contains(alert)) return; // several ended sessions can each hold the same one
        _unclaimed.Add(alert);
        if (_unclaimed.Count > MaxWaiting) _unclaimed.RemoveRange(0, _unclaimed.Count - MaxWaiting);
    }

    private static void Enqueue(Entry entry, WatchdogAlert alert)
    {
        entry.Waiting.Enqueue(alert);
        while (entry.Waiting.Count > MaxWaiting) entry.Waiting.Dequeue();
    }
}
