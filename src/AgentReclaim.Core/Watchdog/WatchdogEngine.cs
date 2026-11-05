using System.Text.RegularExpressions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Integration;

namespace AgentReclaim.Core.Watchdog;

/// <summary>What the watchdog looks at. Everything here only reads.</summary>
public sealed class WatchdogSettings
{
    /// <summary>Off until the user turns it on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Loopback port for the alert socket.</summary>
    public int Port { get; set; } = 47615;

    public int CheckSeconds { get; set; } = 120;

    /// <summary>Work under way with no watched file changing for this long counts as stuck.</summary>
    public int QuietMinutes { get; set; } = 20;

    /// <summary>Files moving while the session's progress hasn't changed for this long counts as stale.</summary>
    public int ProgressStaleMinutes { get; set; } = 60;

    /// <summary>A session isn't told it's stuck, or stale, again sooner than this unless its progress has changed.</summary>
    public int RepeatMinutes { get; set; } = 120;

    /// <summary>Servers are read every this many checks.</summary>
    public int ServerEveryChecks { get; set; } = 2;

    /// <summary>Holds &lt;session id&gt;.txt, each "percent|step". Null: ~/.claude/progress.</summary>
    public string? ProgressDirectory { get; set; }

    /// <summary>Folders whose files count as a session's activity. "{session}" stands for its id.</summary>
    public List<string> ActivityPaths { get; set; } = [];

    public List<WatchedRepo> Repos { get; set; } = [];
    public List<WatchedServer> Servers { get; set; } = [];

    /// <summary>Keys in settings.json that aren't settings: kept, so a save doesn't drop them, and reported.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }
}

/// <summary>A new commit, or a change to one of the files, raises an alert.</summary>
public sealed class WatchedRepo
{
    public string Path { get; set; } = "";

    /// <summary>Relative to <see cref="Path"/>.</summary>
    public List<string> Files { get; set; } = [];

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }
}

/// <summary>Read over ssh with a key (never a prompt). A change in what the command prints raises an alert.</summary>
public sealed class WatchedServer
{
    public string Name { get; set; } = "";

    /// <summary>user@host or host.</summary>
    public string SshTarget { get; set; } = "";

    /// <summary>Should only read. Its output must stay the same while nothing has happened, so no clocks.</summary>
    public string Command { get; set; } = "uptime -s";

    /// <summary>Also show a Windows notification, so it's seen with no agent session open.</summary>
    public bool Notify { get; set; } = true;

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }
}

public enum WatchdogAlertKind
{
    /// <summary>A watched repo changed.</summary>
    Change,

    /// <summary>A session's work is under way and nothing has changed for a long time.</summary>
    Stuck,

    /// <summary>Files are moving, but a session's progress isn't.</summary>
    Stale,

    /// <summary>AgentReclaim's own process check thinks a command is stuck.</summary>
    Hung,

    /// <summary>A watched server changed, or can't be reached.</summary>
    Server,

    /// <summary>Something the user asked to have watched can't be, so nothing would ever be heard from it.</summary>
    Problem,
}

/// <param name="Session">Who it's for. Null: everyone.</param>
/// <param name="Notify">Also show a Windows notification.</param>
public sealed record WatchdogAlert(WatchdogAlertKind Kind, string Text, string? Session, DateTime AtUtc, bool Notify);

/// <param name="Complete">False: there were too many files to look through them all, so this may not be the newest.</param>
public sealed record ActivityMark(DateTime AtUtc, string Path, bool Complete = true);

public sealed record ProgressMark(string Text, DateTime AtUtc)
{
    public bool Done => Text.Split('|')[0].Trim() == "100";
}

public interface IWatchdogProbes
{
    /// <summary>One line per thing watched, stable while nothing changes. Null: couldn't read it this time.</summary>
    IReadOnlyList<string>? RepoState(WatchedRepo repo);

    /// <summary>Null: couldn't reach it.</summary>
    string? ServerState(WatchedServer server);

    ActivityMark? NewestActivity(IReadOnlyList<string> paths);

    /// <summary>Null: this session doesn't report progress.</summary>
    ProgressMark? Progress(string session);

    /// <summary>Settings that point at nothing, so something would never be heard from. Each line names one.</summary>
    IReadOnlyList<string> SetupProblems(WatchdogSettings settings) => [];
}

/// <summary>What must survive a restart, so what happened while the app was closed is still noticed.</summary>
public sealed class WatchdogMemory
{
    public Dictionary<string, List<string>> Repos { get; set; } = [];
    public Dictionary<string, string> Servers { get; set; } = [];

    /// <summary>The target and command each server's state was read with: after editing them, the state is new, not changed.</summary>
    public Dictionary<string, string> ServerSetups { get; set; } = [];

    /// <summary>Problems already told and still there, so a restart doesn't tell them again.</summary>
    public List<string> ProblemsTold { get; set; } = [];

    /// <summary>The last stuck or stale alert per session and kind, so a restart doesn't repeat one sooner than it should.</summary>
    public Dictionary<string, AlertMark> LastAlerts { get; set; } = [];

    /// <summary>Stuck commands already told, and when last seen: a restart doesn't tell them again.</summary>
    public Dictionary<string, DateTime> HungSeen { get; set; } = [];
}

public sealed class AlertMark
{
    public string Key { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

/// <summary>Decides what's worth an alert. Does no I/O of its own: see <see cref="IWatchdogProbes"/>.</summary>
public sealed class WatchdogEngine(IWatchdogProbes probes, WatchdogMemory? memory = null)
{
    public const string Unreachable = "unreachable";

    /// <summary>A repo that can't be read this many checks in a row is reported, rather than staying silent for good.</summary>
    public const int RepoFailuresBeforeAlert = 3;

    private readonly Dictionary<string, string> _pendingServer = [];
    /// <summary>A stuck command that drops off the list (its CPU dipped for one poll) isn't new when it's back this soon.</summary>
    public static readonly TimeSpan HungMemory = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The longest the process check can take to call a command stuck (its setting goes up to 240 minutes). Until the
    /// watchdog has been watching this long, plus <see cref="HungMemory"/>, a command missing from the list may just not
    /// have been judged yet: after a restart everything starts unjudged.
    /// </summary>
    public static readonly TimeSpan LongestStuckWait = TimeSpan.FromMinutes(240);

    private const int HungRemembered = 500;
    private DateTime? _watchingStuckSince;

    /// <summary>A told problem missing from this many rounds in a row has gone: one failed read doesn't mean fixed.</summary>
    public const int RoundsBeforeProblemGone = 3;

    private readonly Dictionary<string, int> _problemAbsent = [];
    private readonly Dictionary<string, DateTime> _hungSeen = new(memory?.HungSeen ?? []);
    private readonly object _hungLock = new();
    private readonly Dictionary<string, int> _repoFailures = [];
    private readonly Dictionary<string, DateTime> _sessionSince = [];

    private DateTime? _lastCheck;
    private DateTime _quietFrom = DateTime.MinValue;
    private long _checks;

    public WatchdogMemory Memory { get; } = memory ?? new WatchdogMemory();

    public static bool ValidSshTarget(string? target) => target is not null && SshTarget.IsMatch(target);

    public static bool ValidSession(string? session) => session is not null && SessionId.IsMatch(session);

    // No leading "-": it would be read as an ssh option.
    private static readonly Regex SshTarget = new(@"^[A-Za-z0-9_][A-Za-z0-9._-]*(@[A-Za-z0-9_][A-Za-z0-9._-]*)?\z", RegexOptions.CultureInvariant);

    // Becomes a file name, so no separators and no dots.
    private static readonly Regex SessionId = new(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);

    /// <summary>One round of checks. One thing failing never costs the alerts already decided, or the checks after it.</summary>
    /// <param name="scheduled">False: run early because settings changed. Servers then wait for their turn, so two reads
    /// meant to be a check apart, to confirm a change, aren't taken seconds apart.</param>
    public List<WatchdogAlert> Check(WatchdogSettings settings, IReadOnlyCollection<string> sessions, DateTime now, bool scheduled = true)
    {
        // After a gap (the PC slept, or the app was closed) file times say nothing about the time that passed unseen.
        var longestNormalGap = TimeSpan.FromSeconds(Math.Max(Math.Max(1, settings.CheckSeconds) * 3, 300));
        // From where the last round ended: many slow servers make a long round, not a gap.
        if (_lastCheck is not { } last || now - last - _lastRoundTook > longestNormalGap)
        {
            _quietFrom = now;
            _pendingServer.Clear(); // a read from before the gap can't be confirmed by one after it
        }
        _lastCheck = now;
        var took = System.Diagnostics.Stopwatch.StartNew();
        _serversNotRead.Clear();

        var repos = (settings.Repos ?? []).Where(r => r is not null).ToList();
        var servers = (settings.Servers ?? []).Where(s => s is not null).ToList();
        var alerts = new List<WatchdogAlert>();
        var problems = new List<(string Key, string Text)>();
        _judgedAny = false;
        _unreadRepos.Clear();

        Guarded("the settings", () =>
        {
            foreach (var line in probes.SetupProblems(settings)) problems.Add(($"setup {line}", line));
            // A misspelt setting would otherwise just be left out, and what it was meant to watch never heard from.
            var unknown = Unknown("Watchdog", settings.Unknown)
                .Concat(repos.SelectMany(r => Unknown($"repo {r.Path}", r.Unknown)))
                .Concat(servers.SelectMany(s => Unknown($"server {s.Name}", s.Unknown)));
            foreach (var key in unknown)
                problems.Add(($"key {key}", $"the setting {key} isn't one the watchdog knows, so it's ignored. Check its spelling in settings.json."));
            foreach (var name in servers.GroupBy(s => s.Name ?? "").Where(g => g.Key.Trim().Length == 0 || g.Count() > 1).Select(g => g.Key))
                problems.Add(($"name {name}", name.Trim().Length == 0
                    ? "a server has no name, so it can't be told apart from the others"
                    : $"two servers are both called \"{name}\", so only one of them is watched"));
        });
        foreach (var repo in repos) Guarded($"repo {repo.Path}", () => CheckRepo(repo, alerts, problems, now));
        var serversDue = _checks % Math.Max(1, settings.ServerEveryChecks) == 0;
        if (scheduled) _checks++;
        foreach (var server in servers.DistinctBy(s => s.Name ?? ""))
            // An early round reads only a server never read at all: confirming a reading takes a scheduled one.
            if (server.Name is { } name && name.Trim().Length > 0)
            {
                if (WatchdogProbes.SshArguments(server) is null)
                {
                    // Every round, not only on its turn: a problem missing from rounds would count as fixed.
                    problems.Add(($"server {name}", $"the server {name} can't be watched: its ssh target \"{server.SshTarget}\" or command \"{server.Command}\" isn't usable (empty, or starting with \"-\")"));
                    Memory.Servers.Remove(name); // its old reading no longer stands, and once fixed it starts afresh
                    Memory.ServerSetups.Remove(name);
                    _pendingServer.Remove(name);
                }
                else if (scheduled && serversDue || !Memory.Servers.ContainsKey(name) && !_pendingServer.ContainsKey(name))
                    Guarded($"server {name}", () => CheckServer(server, alerts, problems, now));
                else _serversNotRead.Add(name);
            }
        foreach (var session in sessions)
        {
            if (!_sessionSince.ContainsKey(session)) _sessionSince[session] = now;
            Guarded($"session {session}", () => CheckSession(settings, session, alerts, problems, now));
        }

        var repeatFor = TimeSpan.FromMinutes(Math.Max(1, settings.RepeatMinutes));
        Memory.LastAlerts ??= [];
        foreach (var old in Memory.LastAlerts.Where(kv => now - kv.Value.AtUtc >= repeatFor).Select(kv => kv.Key).ToList())
            Memory.LastAlerts.Remove(old);
        foreach (var gone in _sessionSince.Keys.Where(k => !sessions.Contains(k)).ToList()) _sessionSince.Remove(gone);
        var watchedRepos = repos.Select(RepoKey).ToHashSet();
        foreach (var old in Memory.Repos.Keys.Where(k => !watchedRepos.Contains(k)).ToList()) Memory.Repos.Remove(old);
        var watchedServers = servers.Select(s => s.Name ?? "").ToHashSet();
        foreach (var old in Memory.Servers.Keys.Where(k => !watchedServers.Contains(k)).ToList()) Memory.Servers.Remove(old);
        foreach (var old in Memory.ServerSetups.Keys.Where(k => !watchedServers.Contains(k)).ToList()) Memory.ServerSetups.Remove(old);

        // A problem can only be seen gone where something was looked at: with no session judged this round, or a repo
        // unread, its problems are neither there nor fixed.
        var held = new List<string>();
        if (!_judgedAny) held.Add("toomany ");
        held.AddRange(_unreadRepos.Select(key => $"file {key} "));
        held.AddRange(_serversNotRead.Select(name => $"fails {name} ")); // read only on its turn: neither there nor fixed
        TellProblems(problems, alerts, now, held);
        _lastRoundTook = took.Elapsed;
        return alerts;
    }

    private TimeSpan _lastRoundTook;
    private readonly HashSet<string> _serversNotRead = [];

    private bool _judgedAny;
    private readonly HashSet<string> _unreadRepos = [];

    /// <summary>
    /// Each problem once, for as long as it lasts, and to the user as well as agents: it's in their settings, and while it
    /// lasts nothing will be heard from what it's about. Told again if it comes back after being fixed.
    /// </summary>
    private void TellProblems(List<(string Key, string Text)> problems, List<WatchdogAlert> alerts, DateTime now, List<string> held)
    {
        var told = (Memory.ProblemsTold ??= []).ToHashSet();
        foreach (var (key, text) in problems.DistinctBy(p => p.Key))
            if (told.Add(key))
                alerts.Add(new WatchdogAlert(WatchdogAlertKind.Problem, $"WATCHDOG: PROBLEM, {text}", null, now, true));
        var current = problems.Select(p => p.Key).ToHashSet();
        foreach (var key in told.ToList())
        {
            if (current.Contains(key) || held.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                _problemAbsent.Remove(key);
                continue;
            }
            // Not seen this round. A read that failed, or a restart, leaves it unseen for a round or two without it being fixed.
            if ((_problemAbsent[key] = _problemAbsent.GetValueOrDefault(key) + 1) < RoundsBeforeProblemGone) continue;
            _problemAbsent.Remove(key);
            told.Remove(key);
        }
        Memory.ProblemsTold = told.ToList();
    }

    private static IEnumerable<string> Unknown(string where, Dictionary<string, System.Text.Json.JsonElement>? extra) =>
        (extra ?? []).Keys.Select(k => $"\"{k}\" (in {where})");

    private static void Guarded(string what, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex)
        {
            Log.Error($"Watchdog: checking {what} failed", ex);
        }
    }

    // Includes the file list: editing it in settings changes what's read, which isn't a change in the repo.
    private static string RepoKey(WatchedRepo repo) =>
        repo.Files is not { Count: > 0 } files ? repo.Path ?? "" : $"{repo.Path} [{string.Join(", ", files)}]";

    private void CheckRepo(WatchedRepo repo, List<WatchdogAlert> alerts, List<(string, string)> problems, DateTime now)
    {
        var key = RepoKey(repo);
        if (probes.RepoState(repo) is not { } state)
        {
            _unreadRepos.Add(key);
            var failures = _repoFailures[key] = _repoFailures.GetValueOrDefault(key) + 1;
            if (failures >= RepoFailuresBeforeAlert)
                problems.Add(($"repo {key}", $"can't read the repo {repo.Path}, so it isn't being watched. Is the path right, does git work there, and can every file listed for it be read?"));
            return;
        }
        _repoFailures.Remove(key);
        foreach (var line in state.Where(l => l.EndsWith(" missing", StringComparison.Ordinal) || l.EndsWith(" outside the repo", StringComparison.Ordinal) || l.EndsWith(" not a file", StringComparison.Ordinal)))
            problems.Add(($"file {key} {line}", $"in {repo.Path}, {line}, so it isn't being watched"));

        if (!Memory.Repos.TryGetValue(key, out var known))
        {
            Memory.Repos[key] = state.ToList();
            return;
        }
        if (known.SequenceEqual(state)) return;

        Memory.Repos[key] = state.ToList();
        var lines = known.Except(state).Select(l => $"< {l}").Concat(state.Except(known).Select(l => $"> {l}"));
        alerts.Add(new WatchdogAlert(WatchdogAlertKind.Change,
            $"WATCHDOG: CHANGE in {repo.Path}, update the notes\n{string.Join('\n', lines)}", null, now, false));
    }

    /// <summary>A change must be seen on two reads in a row: one odd reading isn't news.</summary>
    private void CheckServer(WatchedServer server, List<WatchdogAlert> alerts, List<(string, string)> problems, DateTime now)
    {
        var name = server.Name;
        var setup = $"{server.SshTarget} {server.Command}";
        if (Memory.ServerSetups.GetValueOrDefault(name) != setup)
        {
            // Edited in settings: what it reads now is a new start, not a change on the server.
            Memory.Servers.Remove(name);
            _pendingServer.Remove(name);
            Memory.ServerSetups[name] = setup;
        }

        var state = probes.ServerState(server) ?? Unreachable;
        if (state.StartsWith("(exit ", StringComparison.Ordinal))
            // Some commands say what they say with an exit code ("systemctl is-active"); a command the server doesn't
            // have in that form ("uptime -s" on macOS) also exits 1 and would never change. Only the user knows which.
            problems.Add(($"fails {name} ", $"the server {name}'s command \"{server.Command}\" fails: {(state.Length <= 160 ? state : state[..160])}. If that's what it's meant to say, nothing's wrong; if not, it can't see the server change."));
        if (!Memory.Servers.TryGetValue(name, out var known))
        {
            if (state != Unreachable)
            {
                _pendingServer.Remove(name);
                Memory.Servers[name] = state;
                return;
            }
            // Never read yet. Taking this as normal would mean no word from it, ever.
            if (_pendingServer.GetValueOrDefault(name) != state)
            {
                _pendingServer[name] = state;
                return;
            }
            _pendingServer.Remove(name);
            Memory.Servers[name] = state;
            alerts.Add(new WatchdogAlert(WatchdogAlertKind.Problem,
                $"WATCHDOG: PROBLEM, can't read the server {name}, so it isn't being watched: it has never answered from this PC", null, now, true));
            return;
        }
        if (state == known)
        {
            _pendingServer.Remove(name);
            return;
        }
        if (_pendingServer.GetValueOrDefault(name) != state)
        {
            _pendingServer[name] = state;
            return;
        }

        _pendingServer.Remove(name);
        Memory.Servers[name] = state;
        var note = state == Unreachable ? " (seen from this PC, whose own network may be the cause)" : "";
        alerts.Add(new WatchdogAlert(WatchdogAlertKind.Server,
            $"WATCHDOG: SERVER {name} changed, read twice{note}\nwas: {known}\nnow: {state}", null, now, server.Notify));
    }

    private void CheckSession(WatchdogSettings settings, string session, List<WatchdogAlert> alerts, List<(string, string)> problems, DateTime now)
    {
        // A session with no progress or at 100 is idle. Idle is never an alert.
        if (probes.Progress(session) is not { Done: false } progress) return;

        var paths = (settings.ActivityPaths ?? []).Where(p => p is not null).Select(p => p.Replace("{session}", session, StringComparison.Ordinal)).ToList();
        var newest = probes.NewestActivity(paths);
        _judgedAny = true;
        if (newest is { Complete: false })
        {
            // One problem for the folders, however many sessions look at them.
            var folders = string.Join(", ", settings.ActivityPaths ?? []);
            problems.Add(($"toomany {folders}",
                $"there are too many files in the activity folders ({folders}) to tell whether sessions are still working, so they aren't being judged. List narrower folders."));
            return;
        }

        // Quiet is only counted from when the watchdog could see: a session that has just connected, say after being
        // resumed, may have been waiting on a person.
        var seenFrom = _sessionSince.TryGetValue(session, out var since) && since > _quietFrom ? since : _quietFrom;
        var at = newest is null || newest.AtUtc < progress.AtUtc ? progress.AtUtc : newest.AtUtc;
        var quiet = now - (at < seenFrom ? seenFrom : at);
        var repeat = TimeSpan.FromMinutes(Math.Max(1, settings.RepeatMinutes));
        var progressKey = $"{progress.AtUtc:O} {progress.Text}";

        if (quiet >= TimeSpan.FromMinutes(Math.Max(1, settings.QuietMinutes)))
        {
            if (FreshAlert(session, WatchdogAlertKind.Stuck, progressKey, now, repeat))
                alerts.Add(new WatchdogAlert(WatchdogAlertKind.Stuck,
                    $"WATCHDOG: STUCK, nothing has changed for {(int)quiet.TotalMinutes} minutes\nprogress: {progress.Text}\nlast file: {newest?.Path ?? "none"}",
                    session, now, false));
            return; // not stale as well: stale means files are moving
        }

        var unchanged = now - (progress.AtUtc < seenFrom ? seenFrom : progress.AtUtc);
        if (unchanged >= TimeSpan.FromMinutes(Math.Max(1, settings.ProgressStaleMinutes))
            && FreshAlert(session, WatchdogAlertKind.Stale, progressKey, now, repeat))
            alerts.Add(new WatchdogAlert(WatchdogAlertKind.Stale,
                $"WATCHDOG: STALE, progress unchanged for {(int)unchanged.TotalMinutes} minutes while files move\nprogress: {progress.Text}\nlast file: {newest?.Path ?? "none"}",
                session, now, false));
    }

    /// <summary>Each kind keeps its own record, so stuck and stale taking turns don't reset each other's wait.</summary>
    private bool FreshAlert(string session, WatchdogAlertKind kind, string key, DateTime now, TimeSpan repeat)
    {
        var slot = $"{session}|{kind}";
        if (Memory.LastAlerts.TryGetValue(slot, out var last) && last.Key == key && now - last.AtUtc < repeat) return false;
        Memory.LastAlerts[slot] = new AlertMark { Key = key, AtUtc = now };
        return true;
    }

    /// <summary>
    /// What the process check thinks is stuck right now. Each is reported once: one that drops off the list and comes
    /// back within <see cref="HungMemory"/> is the same one. Called from the process poll's thread.
    /// </summary>
    public List<WatchdogAlert> NoteStuck(IEnumerable<StuckProcess> stuck, DateTime now)
    {
        var alerts = new List<WatchdogAlert>();
        lock (_hungLock)
        {
            foreach (var item in stuck)
            {
                var p = item.Node.Process;
                var key = $"{p.Key.Pid}:{p.Key.StartTicks}:{item.Kind}";
                if (!_hungSeen.ContainsKey(key))
                    alerts.Add(new WatchdogAlert(WatchdogAlertKind.Hung,
                        $"WATCHDOG: HUNG? {p.Name} (pid {p.Pid}) under agent pid {item.Session.Root.Process.Pid}: {item.Status}\n{Shorten(p.CommandLine)}",
                        null, now, false));
                _hungSeen[key] = now;
            }
            _watchingStuckSince ??= now;
            if (now - _watchingStuckSince.Value > LongestStuckWait + HungMemory)
                foreach (var old in _hungSeen.Where(kv => now - kv.Value > HungMemory).Select(kv => kv.Key).ToList()) _hungSeen.Remove(old);
            foreach (var old in _hungSeen.OrderByDescending(kv => kv.Value).Skip(HungRemembered).Select(kv => kv.Key).ToList()) _hungSeen.Remove(old);
        }
        return alerts;
    }

    /// <summary>For saving: the process poll changes this on its own thread.</summary>
    public Dictionary<string, DateTime> HungSnapshot()
    {
        lock (_hungLock) return new Dictionary<string, DateTime>(_hungSeen);
    }

    // Both ends: an agent's shell starts with the same long wrapper every time, and what it's running comes last.
    // Secrets are hidden: this goes to every connected agent, the log and the status file.
    private static string Shorten(string? text)
    {
        if (text is null) return "(command line unknown)";
        text = AgentDoctor.Redact(text);
        return text.Length <= 300 ? text : $"{text[..60]}…{text[^239..]}";
    }
}
