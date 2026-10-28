using AgentReclaim.Core.Model;
using AgentReclaim.Core.Native;

namespace AgentReclaim.Core.Analysis;

public enum PortHolder
{
    /// <summary>Under a live agent session.</summary>
    Session,

    /// <summary>Part of a leftover tree.</summary>
    Leftover,

    /// <summary>Not started by an AI coding tool, as far as AgentReclaim knows.</summary>
    Other,
}

/// <summary>One listening TCP port, who holds it, and what "Free this port" would stop.</summary>
public sealed record PortEntry(
    int Port,
    ProcessInfo Process,
    PortHolder Holder,
    string Owner,
    AgentKind? Kind,
    ProcessNode StopRoot,
    string? ProjectPath)
{
    /// <summary>A leftover's port is freed by stopping the whole leftover tree; otherwise just the holder and its children.</summary>
    public OrphanTree? Leftover { get; init; }
}

/// <summary>Everything AI-related running for one repo or working folder.</summary>
public sealed record ProjectSummary(
    string? Path,
    IReadOnlyList<AgentSession> Sessions,
    IReadOnlyList<OrphanTree> Leftovers,
    IReadOnlyList<int> Ports)
{
    public string Name => ProjectResolver.DisplayName(Path);
    public long SessionBytes => Sessions.Sum(s => s.Root.TotalPrivateBytes);
    public long LeftoverBytes => Leftovers.Sum(o => o.Root.TotalPrivateBytes);
    public long TotalBytes => SessionBytes + LeftoverBytes;
    public int Processes => Sessions.Sum(s => s.Root.TotalCount) + Leftovers.Sum(o => o.Root.TotalCount);
}

public static class Insights
{
    /// <summary>Every listening port with its holder's session or leftover: AI-related first, then by port.</summary>
    public static IReadOnlyList<PortEntry> Ports(AnalysisResult result, Func<ProcessInfo, string?> projectOf)
    {
        var session = new Dictionary<ProcessKey, AgentSession>();
        foreach (var s in result.Sessions)
        foreach (var (node, _) in s.Root.SelfAndDescendants())
            session.TryAdd(node.Process.Key, s);

        var leftover = new Dictionary<ProcessKey, OrphanTree>();
        foreach (var o in result.Orphans)
        foreach (var (node, _) in o.Root.SelfAndDescendants())
            leftover.TryAdd(node.Process.Key, o);

        var entries = new List<PortEntry>();
        foreach (var p in result.Snapshot.Processes)
        {
            if (p.ListeningPorts.Count == 0) continue;
            var node = result.Nodes.TryGetValue(p.Key, out var n) ? n : new ProcessNode(p, TimeSpan.Zero, []);
            var project = projectOf(p);
            foreach (var port in p.ListeningPorts.Distinct())
            {
                if (session.TryGetValue(p.Key, out var s))
                {
                    entries.Add(new PortEntry(port, p, PortHolder.Session,
                        $"{AgentSignatures.DisplayName(s.Kind)} session (pid {s.Root.Process.Pid})", s.Kind, node, project));
                }
                else if (leftover.TryGetValue(p.Key, out var o))
                {
                    entries.Add(new PortEntry(port, p, PortHolder.Leftover,
                        $"{AgentSignatures.DisplayName(o.Kind)} leftover (started by {o.FormerOwner})", o.Kind, o.Root, project) { Leftover = o });
                }
                else
                {
                    entries.Add(new PortEntry(port, p, PortHolder.Other, "Not an AI tool", null, node, project));
                }
            }
        }
        // AI-related ports first: those are what this view is usually opened for.
        return entries.OrderBy(e => e.Holder == PortHolder.Other).ThenBy(e => e.Port).ThenBy(e => e.Process.Pid).ToList();
    }

    /// <summary>Sessions and leftovers grouped by the repo (or folder) they run in, biggest first.</summary>
    public static IReadOnlyList<ProjectSummary> Projects(AnalysisResult result, Func<ProcessInfo, string?> projectOf)
    {
        var groups = new Dictionary<string, (List<AgentSession> Sessions, List<OrphanTree> Leftovers)>(StringComparer.OrdinalIgnoreCase);
        (List<AgentSession>, List<OrphanTree>) Group(string? path)
        {
            var key = path ?? "";
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = ([], []);
            return g;
        }

        foreach (var s in result.Sessions) Group(projectOf(s.Root.Process)).Item1.Add(s);
        foreach (var o in result.Orphans) Group(projectOf(o.Root.Process)).Item2.Add(o);

        return groups
            .Select(g => new ProjectSummary(
                g.Key.Length == 0 ? null : g.Key,
                g.Value.Sessions,
                g.Value.Leftovers,
                g.Value.Sessions.Select(s => s.Root).Concat(g.Value.Leftovers.Select(o => o.Root))
                    .SelectMany(r => r.SelfAndDescendants().SelectMany(x => x.Node.Process.ListeningPorts))
                    .Distinct().Order().ToList()))
            .OrderBy(p => p.Path is null) // "folder unknown" last
            .ThenByDescending(p => p.TotalBytes)
            .ToList();
    }
}

/// <summary>
/// Maps a process to its project: the git repo containing its working folder, or the folder itself.
/// Results are cached per process instance and per folder; call <see cref="Retain"/> each poll.
/// </summary>
public sealed class ProjectResolver(Func<int, string?>? readWorkingDirectory = null, Func<string, bool>? isRepoRoot = null)
{
    private readonly Func<int, string?> _readCwd = readWorkingDirectory ?? NativeProcess.GetCurrentDirectory;
    private readonly Func<string, bool> _isRepoRoot = isRepoRoot ?? HasGitEntry;
    private readonly Dictionary<ProcessKey, string?> _byProcess = new();
    private readonly Dictionary<string, string> _byFolder = new(StringComparer.OrdinalIgnoreCase);

    public string? ProjectOf(ProcessInfo p)
    {
        if (_byProcess.TryGetValue(p.Key, out var cached)) return cached;
        var cwd = _readCwd(p.Pid);
        return _byProcess[p.Key] = cwd is null ? null : RootOf(cwd);
    }

    /// <summary>Forgets processes that have exited (folder lookups stay cached; there are few).</summary>
    public void Retain(IEnumerable<ProcessKey> live)
    {
        var keep = live.ToHashSet();
        foreach (var key in _byProcess.Keys.Where(k => !keep.Contains(k)).ToList()) _byProcess.Remove(key);
        if (_byFolder.Count > 2000) _byFolder.Clear();
    }

    private string RootOf(string folder)
    {
        if (_byFolder.TryGetValue(folder, out var root)) return root;
        root = folder;
        // Network paths can stall for seconds per probe; group by the folder itself.
        if (!folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var dir = folder;
            for (var depth = 0; depth < 32 && !string.IsNullOrEmpty(dir); depth++)
            {
                if (_isRepoRoot(dir))
                {
                    root = dir;
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        return _byFolder[folder] = root;
    }

    /// <summary>A .git folder, or a .git file (worktrees and submodules).</summary>
    private static bool HasGitEntry(string dir)
    {
        try
        {
            var git = Path.Combine(dir, ".git");
            return Directory.Exists(git) || File.Exists(git);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static string DisplayName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "(folder unknown)";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(path.TrimEnd('\\'), home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "~ (home folder)";
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
