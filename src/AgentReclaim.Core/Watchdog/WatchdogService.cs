using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Cleanup;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.Core.Watchdog;

/// <summary>What the app saves and the MCP server's watchdog_status reads (that server is a separate process).</summary>
public sealed class WatchdogStatus
{
    public bool Enabled { get; set; }

    /// <summary>False once the app has stopped watching, by being closed or turned off: the address then leads nowhere of ours.</summary>
    public bool Running { get; set; }

    public string? Error { get; set; }
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public DateTime? LastCheckUtc { get; set; }
    public int CheckSeconds { get; set; }

    /// <summary>How long the last round took: slow ssh reads stretch the time between saves.</summary>
    public int LastRoundSeconds { get; set; }
    public WatchdogMemory Memory { get; set; } = new();
    public List<string> Sessions { get; set; } = [];
    public List<WatchdogAlert> RecentAlerts { get; set; } = [];

    /// <summary>Alerts not handed over yet.</summary>
    public WatchdogMail Mail { get; set; } = new();

    /// <summary>Problems the service itself has told the user (a port in use, a settings section misspelt).</summary>
    public List<string> ProblemsTold { get; set; } = [];

    /// <summary>ws:// address for an agent's listener, with {session} still to fill in.</summary>
    public string Url => $"ws://127.0.0.1:{Port}/watchdog?session={{session}}&token={Token}";

    /// <summary>The app is alive and checking, by when it last wrote this. Null: it never has.</summary>
    public bool IsCurrent(DateTime now) =>
        Enabled && Running && Error is null && LastCheckUtc is { } at && now - at < TimeSpan.FromSeconds(Math.Max(CheckSeconds, 1) * 3 + Math.Max(LastRoundSeconds, 0) * 2 + 30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>For the service's start: tells a damaged file from one another program is holding for a moment.</summary>
    internal static (WatchdogStatus? Status, bool Damaged, bool Held) LoadAtStart(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return (null, false, false);
                var status = JsonSerializer.Deserialize<WatchdogStatus>(File.ReadAllText(path), JsonOptions);
                return (status, status is null, false);
            }
            catch (JsonException)
            {
                return (null, true, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 20) return (null, false, true);
                Thread.Sleep(250);
            }
        }
    }

    public static WatchdogStatus? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WatchdogStatus>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A reader holding the file open makes the swap fail for a moment, so it's tried again a few times.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>Runs the watchdog on its own thread, so a slow server or disk never holds up the process polls.</summary>
public sealed class WatchdogService(
    Func<AppSettings> settings,
    string statusFile,
    Action<WatchdogAlert>? notify = null,
    IWatchdogProbes? probes = null) : IDisposable
{
    public const int RecentAlertCount = 50;

    /// <summary>A session not seen for this long has ended (a listener lasts 30 minutes, and is re-armed).</summary>
    public static readonly TimeSpan SessionGone = TimeSpan.FromMinutes(65);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _saveLock = new();
    private Task? _run;
    private bool _offSaved;

    /// <summary>Memory as of the last finished round: a round cut short by closing may have noted a change it never told.</summary>
    private WatchdogMemory _lastRoundMemory = new();
    private readonly WatchdogHub _hub = new();
    private readonly object _alertsLock = new();
    private WatchdogEngine? _engine;
    private WatchdogSocket? _socket;
    private readonly object _deliverLock = new();
    private static readonly string[] SettingNames = typeof(AppSettings).GetProperties().Select(p => p.Name).ToArray();
    private static readonly string[] WatchdogSettingNames =
        typeof(WatchdogSettings).GetProperties().Select(p => p.Name).Where(n => n != nameof(WatchdogSettings.Unknown)).ToArray();
    /// <summary>Problems told and still there. Each is told once while it lasts, however many there are.</summary>
    private readonly HashSet<string> _reported = [];
    private readonly HashSet<string> _reportedThisRound = [];
    private volatile bool _loaded;
    private readonly object _noteLock = new();
    private bool _closing;
    private bool _wasOn;
    private readonly WatchdogStatus _status = new();

    public void Start() => _run = Task.Run(RunAsync);

    public void SettingsChanged() => _wake.Release();

    /// <summary>From the process poll, which mustn't wait: see <see cref="Analysis.AnalysisResult.Stuck"/>.</summary>
    public void NoteStuck(IReadOnlyList<StuckProcess> stuck)
    {
        // The engine's checks and this touch separate state, so a slow server never holds up the poll. The lock is only
        // against closing: once that starts, nothing is noted, so nothing is saved as told without being told.
        lock (_noteLock)
        {
            if (_closing) return;
            if (Volatile.Read(ref _engine) is { } engine) Deliver(engine.NoteStuck(stuck, DateTime.UtcNow));
        }
    }

    private async Task RunAsync()
    {
        Task? wake = null; // kept across rounds: a new wait each time would leave the old ones to take the wake-ups
        var scheduled = true;
        while (!_stop.IsCancellationRequested)
        {
            var checkSeconds = 120;
            try
            {
                if (!_loaded) LoadPrevious();
                // Starting afresh would overwrite it: its token, its memory and the alerts waiting in it.
                if (!_loaded) throw new IOException($"{Path.GetFileName(statusFile)} is held open by another program, so the watchdog waits for it rather than start afresh");
                var all = settings();
                var current = all.Watchdog ?? new WatchdogSettings();
                // "watchdog" for "Watchdog" would be dropped, leaving it off (or on without what's in it) with nobody told.
                if (all.Unknown?.Keys.FirstOrDefault(k => k.Equals("Watchdog", StringComparison.OrdinalIgnoreCase)) is { } misspelt)
                    Report(current.Enabled
                        ? $"settings.json has a section \"{misspelt}\" as well as \"Watchdog\". Only \"Watchdog\" is used, so whatever is in \"{misspelt}\" isn't watched. Move it across and delete \"{misspelt}\"."
                        : $"settings.json has \"{misspelt}\" where it needs \"Watchdog\", so that section is ignored and the watchdog is off.");
                // A brace closed too early leaves the watch list outside the section, where nothing reads it.
                foreach (var key in (all.Unknown ?? []).Keys)
                    if (WatchdogSettingNames.FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } setting)
                        Report($"settings.json has \"{key}\" outside the \"Watchdog\" section, so it's ignored. Move it inside \"Watchdog\": {{ ... }} as \"{setting}\".");
                // Keys are matched exactly, so "stuckExemptCommands" leaves the real setting empty without a word.
                if (current.Enabled)
                    foreach (var key in (all.Unknown ?? []).Keys.Where(k => !k.Equals("Watchdog", StringComparison.OrdinalIgnoreCase)))
                        if (SettingNames.FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } meant)
                            Report($"settings.json has \"{key}\" where it needs \"{meant}\", so that setting is ignored.");
                // A file the app couldn't read means defaults, so off. Said only to someone who had it on, or who was
                // turning it on by hand.
                if (all.LoadError is { } loadError && (_wasOn || all.LoadErrorMentionsWatchdog) && !current.Enabled)
                    Report($"settings.json couldn't be read ({loadError}), so the app is using its defaults and the watchdog is off. The app saves over it, so it's kept as settings.json.unreadable: close the app, fix that and copy it back over settings.json.");
                // When on, the engine reports these with everything else; when off, nothing else would.
                if (!current.Enabled && current.Unknown is { Count: > 0 } unknownKeys)
                    Report($"settings.json's Watchdog section has {string.Join(", ", unknownKeys.Keys.Select(k => $"\"{k}\""))}, which isn't a setting it knows, so it's ignored (and \"Enabled\" spelt exactly so is what turns it on).");
                checkSeconds = current.CheckSeconds;
                if (current.Enabled) Round(current, scheduled);
                else TurnOff();
            }
            catch (Exception ex)
            {
                Log.Error("Watchdog: check failed", ex);
                Report($"The watchdog's check failed: {ex.Message}");
            }
            // What wasn't reported this round has gone; if it comes back it's told again.
            lock (_reported) _reported.IntersectWith(_reportedThisRound);
            _reportedThisRound.Clear();

            try
            {
                wake ??= _wake.WaitAsync(_stop.Token);
                var done = await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(Math.Clamp(checkSeconds, 1, 3600)), _stop.Token), wake);
                scheduled = done != wake;
                if (done == wake)
                {
                    wake = null;
                    // Several changes in a row make one round, not one each.
                    await Task.Delay(500, _stop.Token);
                    while (_wake.Wait(0)) { }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void LoadPrevious()
    {
        var (previous, damaged, held) = WatchdogStatus.LoadAtStart(statusFile);
        if (held) return; // not loaded: tried again next round
        if (damaged)
        {
            // Damaged, such as zeroed by a power cut. What it knew is gone: changes made meanwhile won't be seen as changes.
            var kept = statusFile + ".unreadable";
            try
            {
                File.Copy(statusFile, kept, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                kept = "(couldn't keep a copy)";
            }
            Report($"{Path.GetFileName(statusFile)} couldn't be read, so the watchdog started afresh: a change to a repo or server while the app was closed won't be told, and alerts waiting for a session were lost. The old file is kept as {kept}.");
        }
        _status.Token = previous?.Token is { Length: >= 32 } token ? token : Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _status.Memory = previous?.Memory ?? new WatchdogMemory();
        _lastRoundMemory = CopyOf(_status.Memory); // closing before the first round ends must not save an empty memory
        _status.RecentAlerts = previous?.RecentAlerts ?? [];
        if (previous?.Mail is { } mail) _hub.Restore(mail);
        lock (_reported) _reported.UnionWith(previous?.ProblemsTold ?? []); // forgotten after the first round if they've gone
        _wasOn = previous?.Enabled == true;
        _loaded = true;
    }

    private void Round(WatchdogSettings current, bool scheduled)
    {
        var started = DateTime.UtcNow;
        _status.Enabled = true; // on in settings, even if closed before this round ends
        _offSaved = false;
        var engine = _engine;
        if (engine is null) Volatile.Write(ref _engine, engine = new WatchdogEngine(probes ?? new WatchdogProbes(current), _status.Memory));
        Listen(current);

        var now = DateTime.UtcNow;
        _hub.Prune(SessionGone, now, TimeSpan.FromSeconds(Math.Max(Math.Max(1, current.CheckSeconds) * 3, 300)));
        var found = engine.Check(current, _hub.Sessions().Select(s => s.Session).ToList(), now, scheduled);
        // Told before it counts as known: whichever of the two closing saves, below or in Dispose, sees the new memory
        // also sees the alert in the mail.
        lock (_deliverLock)
        {
            Deliver(found);
            _lastRoundMemory = Remembered();
        }
        if (_stop.IsCancellationRequested) return; // closing: Dispose saves, as not running

        _status.Enabled = true;
        _status.Running = _socket is not null;
        _status.Port = _socket?.Port ?? current.Port;
        _status.CheckSeconds = current.CheckSeconds;
        _status.LastCheckUtc = DateTime.UtcNow;
        _status.LastRoundSeconds = (int)Math.Ceiling((_status.LastCheckUtc.Value - started).TotalSeconds);
        _status.Sessions = _hub.Sessions().Select(s => s.Connected ? s.Session : $"{s.Session} (between connections)").ToList();
        Save(_lastRoundMemory);
    }

    /// <summary>
    /// The last finished round's memory, but with the stuck commands told up to now: those come from the process poll,
    /// not the round, and their alerts are already in the mail.
    /// </summary>
    private WatchdogMemory LastRoundWithStuckTold()
    {
        var memory = CopyOf(_lastRoundMemory);
        if (Volatile.Read(ref _engine) is { } engine) memory.HungSeen = engine.HungSnapshot();
        return memory;
    }

    /// <summary>Memory as it stands, with what the process poll has told.</summary>
    private WatchdogMemory Remembered()
    {
        if (Volatile.Read(ref _engine) is { } engine) _status.Memory.HungSeen = engine.HungSnapshot();
        return CopyOf(_status.Memory);
    }

    internal static WatchdogMemory CopyOf(WatchdogMemory memory) => new()
    {
        Repos = memory.Repos.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        Servers = new Dictionary<string, string>(memory.Servers),
        ServerSetups = new Dictionary<string, string>(memory.ServerSetups),
        ProblemsTold = (memory.ProblemsTold ?? []).ToList(),
        LastAlerts = (memory.LastAlerts ?? []).ToDictionary(kv => kv.Key, kv => new AlertMark { Key = kv.Value.Key, AtUtc = kv.Value.AtUtc }),
        HungSeen = new Dictionary<string, DateTime>(memory.HungSeen ?? []),
    };

    private void Listen(WatchdogSettings current)
    {
        if (_socket is not null && (_socket.Port == current.Port || current.Port == 0)) return;
        _socket?.Dispose();
        _socket = null;
        var socket = new WatchdogSocket(_hub, _status.Token);
        try
        {
            socket.Start(current.Port);
            _socket = socket;
            _status.Error = null;
            Log.Info($"Watchdog: listening on 127.0.0.1:{socket.Port}.");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentOutOfRangeException)
        {
            socket.Dispose();
            _status.Error = $"Couldn't listen on port {current.Port}: {ex.Message}";
            Log.Warning(_status.Error);
            Report(_status.Error);
        }
    }

    /// <summary>The user is told once per problem: with the watchdog not working, nobody else will say.</summary>
    private void Report(string problem)
    {
        _reportedThisRound.Add(problem);
        bool fresh;
        lock (_reported) fresh = _reported.Add(problem); // a closing save can be reading it
        if (!fresh) return;
        notify?.Invoke(new WatchdogAlert(WatchdogAlertKind.Problem, $"WATCHDOG: PROBLEM, {problem}", null, DateTime.UtcNow, true));
    }

    private void TurnOff()
    {
        if (_offSaved) return;
        if (!File.Exists(statusFile) && _socket is null)
        {
            _offSaved = true; // never on: nothing to say, and no token made for a feature nobody uses
            return;
        }
        _socket?.Dispose();
        _socket = null;
        var memory = Remembered(); // before the engine goes: it holds the stuck commands already told
        Volatile.Write(ref _engine, null);
        _status.Enabled = false;
        _status.Running = false;
        _status.Error = null;
        _status.Sessions = [];
        _offSaved = Save(memory); // until it's saved, watchdog_status would still give out the address
    }

    private void Deliver(List<WatchdogAlert> alerts)
    {
        foreach (var alert in alerts)
        {
            Log.Info($"Watchdog alert: {alert.Text.Replace('\n', ' ')}");
            _hub.Publish(alert);
            if (alert.Notify) notify?.Invoke(alert);
            lock (_alertsLock)
            {
                _status.RecentAlerts.Add(alert);
                if (_status.RecentAlerts.Count > RecentAlertCount)
                    _status.RecentAlerts.RemoveRange(0, _status.RecentAlerts.Count - RecentAlertCount);
            }
        }
    }

    private bool Save(WatchdogMemory memory)
    {
        try
        {
            // Only copied under the lock: the process poll can be waiting on it, and mustn't wait on the disk.
            List<WatchdogAlert> recent;
            List<string> told;
            lock (_reported) told = _reported.Take(50).ToList();
            lock (_alertsLock) recent = _status.RecentAlerts.ToList();
            var status = new WatchdogStatus
            {
                Enabled = _status.Enabled,
                Running = _status.Running,
                Error = _status.Error,
                Port = _status.Port,
                Token = _status.Token,
                LastCheckUtc = _status.LastCheckUtc,
                LastRoundSeconds = _status.LastRoundSeconds,
                CheckSeconds = _status.CheckSeconds,
                Memory = memory,
                Sessions = _status.Sessions.ToList(),
                RecentAlerts = recent,
                Mail = _hub.Mail(),
                ProblemsTold = told,
            };
            lock (_saveLock) status.Save(statusFile);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("Watchdog: couldn't save its status", ex);
            return false;
        }
    }

    /// <summary>Connections close, so an agent's listener ends and it knows nothing is watching.</summary>
    public void Dispose()
    {
        lock (_noteLock) _closing = true; // a process poll still running can't note anything from here on
        _stop.Cancel();
        // A round under way is let finish (briefly), so what it found is told before it's saved as known.
        var finished = _run?.Wait(TimeSpan.FromSeconds(3)) ?? true;
        _socket?.Dispose();
        _socket?.WaitForConnections(TimeSpan.FromSeconds(2)); // one cut off mid-send puts the alert back first
        _hub.CloseAll();
        _status.Running = false;
        _status.Sessions = [];
        // Not loaded yet (closed straight after starting): saving would overwrite the file with nothing.
        // Never on: leave no file and no token behind.
        if (_loaded && (_status.Enabled || File.Exists(statusFile)))
            lock (_deliverLock) // a late round delivers either before (and its memory is saved too) or after (found again)
                Save(finished ? Remembered() : LastRoundWithStuckTold()); // and anything still waiting for a session
    }
}

/// <summary>The real reads: git, ssh and the file system.</summary>
public sealed class WatchdogProbes(WatchdogSettings initial) : IWatchdogProbes
{
    private const int MaxFilesScanned = 50_000;
    private static readonly TimeSpan ToolLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan InFlux = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", "__pycache__" };
    private static readonly Dictionary<string, string> NoGitLocks = new() { ["GIT_OPTIONAL_LOCKS"] = "0" };

    private readonly string _progressDirectory = initial.ProgressDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "progress");

    public IReadOnlyList<string>? RepoState(WatchedRepo repo)
    {
        if (string.IsNullOrWhiteSpace(repo.Path)) return null;
        // The full hash: a short one grows a character as the repo does, which would look like a new commit.
        var head = ToolRunner.Run("git", ["-C", repo.Path, "log", "-1", "--format=%H %s"], ToolLimit, NoGitLocks);
        if (!head.Success) return null;
        var lines = new List<string> { $"commit {head.Output}" };
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path));
        foreach (var file in repo.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            var full = Path.GetFullPath(Path.Combine(root, file));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{file} outside the repo");
                continue;
            }
            if (file.IndexOfAny(['*', '?']) >= 0 || Directory.Exists(full))
            {
                lines.Add($"{file} is a folder or wildcard, not a file");
                continue;
            }
            try
            {
                // Written in the last moments: it may be half written (an editor or git mid-write). Next round.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(full) < InFlux) return null;
                lines.Add($"{file} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)))[..12]}");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // git checkout and some editors delete and write again: missing only if it stays so.
                Thread.Sleep(InFlux);
                if (File.Exists(full)) return null;
                lines.Add($"{file} missing");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // locked for the moment: not a change
            }
        }
        return lines;
    }

    /// <summary>
    /// What ssh is run with. Null: not usable. A target or command starting with "-" would be read as an ssh option, and
    /// some of those run programs.
    /// </summary>
    public static IReadOnlyList<string>? SshArguments(WatchedServer server) =>
        !WatchdogEngine.ValidSshTarget(server.SshTarget) || server.Command is not { } command || command.TrimStart().StartsWith('-') || command.Trim().Length == 0
            ? null
            : ["-o", "BatchMode=yes", "-o", "ConnectTimeout=5", server.SshTarget, command];

    public string? ServerState(WatchedServer server)
    {
        if (SshArguments(server) is not { } arguments) return null;
        return StateFrom(ToolRunner.Run("ssh", arguments, ToolLimit));
    }

    internal static string? StateFrom(ToolResult result)
    {
        // ssh's own failures (no connection, no key) are 255; a timeout has no proper code; 126 and 127 are a command
        // the server can't run (mistyped, not installed, no permission), which would never change. Anything else is
        // the server's command answering with an error, such as "systemctl is-active" for a stopped service.
        if (!result.Success && (result.ExitCode is 255 or 126 or 127 or < 0 || result.Output.Contains("exited, but its output was still open"))) return null;
        var text = (result.Success ? result.Output : $"(exit {result.ExitCode}) {result.Output}").ReplaceLineEndings(" ");
        return text.Length <= 1000 ? text : text[..1000];
    }

    public ActivityMark? NewestActivity(IReadOnlyList<string> paths)
    {
        ActivityMark? newest = null;
        var scanned = 0;
        var complete = true;
        var latestBelievable = DateTime.UtcNow.AddMinutes(5); // a file dated in the future would hide real quiet
        var folders = new Stack<string>(paths);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (folders.TryPop(out var folder))
        {
            if (scanned >= MaxFilesScanned)
            {
                complete = false;
                break;
            }
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
                {
                    if (entry is DirectoryInfo)
                    {
                        if (!SkippedFolders.Contains(entry.Name)) folders.Push(entry.FullName);
                        continue;
                    }
                    if (++scanned > MaxFilesScanned)
                    {
                        complete = false;
                        break;
                    }
                    if (newest is not null && entry.LastWriteTimeUtc <= newest.AtUtc || entry.LastWriteTimeUtc > latestBelievable) continue;
                    if (IsOwnOutput((FileInfo)entry)) continue;
                    newest = new ActivityMark(entry.LastWriteTimeUtc, entry.FullName);
                }
            }
            catch (Exception ex)
            {
                // Gone, locked or not a usable path: the rest still count.
                Log.Debug($"Watchdog: couldn't scan {folder}: {ex.Message}");
            }
        }
        return complete || newest is null ? newest : newest with { Complete = false };
    }

    /// <summary>A listener's own output, empty or holding alerts, isn't work: counting it would make every alert look like progress.</summary>
    private static bool IsOwnOutput(FileInfo file)
    {
        if (file.Name.StartsWith("watchdog.", StringComparison.OrdinalIgnoreCase)) return true;
        if (!file.Extension.Equals(".output", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Length == 0) return true;
        try
        {
            using var reader = new StreamReader(file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var start = new char[9];
            return reader.Read(start, 0, 9) == 9 && new string(start) == "WATCHDOG:";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> SetupProblems(WatchdogSettings settings)
    {
        var problems = new List<string>();
        if (!Directory.Exists(_progressDirectory))
            problems.Add($"the progress folder {_progressDirectory} doesn't exist, so no session's work can be judged");
        foreach (var path in (settings.ActivityPaths ?? []).Where(p => p is not null))
        {
            // With {session}, each session's folder may not exist yet, but the folder holding them must.
            var at = path.IndexOf("{session}", StringComparison.Ordinal);
            var folder = at < 0 ? path
                : path[..at] is var prefix && (prefix.EndsWith('\\') || prefix.EndsWith('/')) ? prefix : Path.GetDirectoryName(path[..at]);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                problems.Add($"the activity folder {path} doesn't exist");
        }
        return problems;
    }

    public ProgressMark? Progress(string session)
    {
        if (!WatchdogEngine.ValidSession(session)) return null;
        var path = Path.Combine(_progressDirectory, session + ".txt");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var buffer = new char[300];
            var read = reader.Read(buffer, 0, buffer.Length);
            return new ProgressMark(new string(buffer, 0, read).Trim(), File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
