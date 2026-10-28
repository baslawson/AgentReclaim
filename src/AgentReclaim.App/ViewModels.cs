using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;

namespace AgentReclaim.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    /// <summary>Refreshes every binding on this object.</summary>
    protected void RaiseAll() => Raise(string.Empty);
}

/// <summary>A row wrapping a model that changes every poll. Rows are reused so selection and scroll survive refreshes.</summary>
public abstract class Row<T>(T model) : ObservableObject
{
    public T Model { get; private set; } = model;

    public void Update(T model)
    {
        Model = model;
        RaiseAll();
    }
}

public sealed class OrphanRow(OrphanTree tree) : Row<OrphanTree>(tree)
{
    private ProcessInfo Root => Model.Root.Process;

    public string Agent => AgentSignatures.DisplayName(Model.Kind);
    public string Name => Root.Name;
    public int Pid => Root.Pid;
    public int Processes => Model.Root.TotalCount;
    public long MemoryBytes => Model.Root.TotalPrivateBytes;
    public string Memory => Format.Bytes(MemoryBytes);
    public string Cpu => Format.Percent(Model.Root.TotalCpuPercent);
    public string Idle => Format.Duration(Model.Root.SelfAndDescendants().Min(x => x.Node.IdleFor));
    public string Ports => string.Join(", ", Model.Root.SelfAndDescendants().SelectMany(x => x.Node.Process.ListeningPorts).Distinct());
    public string Owner => Model.FormerOwner;
    public string Confidence => Model.Attribution == Attribution.Observed ? "Seen" : "Guessed";
    public string CommandLine => Root.CommandLine ?? "";
}

public sealed class FindingRow(Finding finding) : Row<Finding>(finding)
{
    public string Title => Model.Title;
    public string Detail => Model.Detail;
    public string Memory => Format.Bytes(Model.Bytes);
}

public sealed class SessionRow(AgentSession session) : Row<AgentSession>(session)
{
    public ObservableCollection<MemberRow> Members { get; } = [];

    public string Title => $"{AgentSignatures.DisplayName(Model.Kind)} · pid {Model.Root.Process.Pid}";
    public string Summary => $"{Model.Root.TotalCount} process{(Model.Root.TotalCount == 1 ? "" : "es")} ·{Format.Bytes(Model.Root.TotalPrivateBytes)} · {Format.Percent(Model.Root.TotalCpuPercent)} CPU";
    public string Started => Model.Root.Process.StartTimeUtc == DateTime.MinValue
        ? ""
        : $"Started {Model.Root.Process.StartTimeUtc.ToLocalTime():g}";
    public string CommandLine => Model.Root.Process.CommandLine ?? "";

    private IReadOnlyDictionary<ProcessKey, StuckProcess> _stuck = new Dictionary<ProcessKey, StuckProcess>();

    /// <summary>"1 process may be stuck", or empty.</summary>
    public string StuckText => _stuck.Count == 0 ? "" : $"{_stuck.Count} process{(_stuck.Count == 1 ? "" : "es")} may be stuck";
    public bool HasStuck => _stuck.Count > 0;

    /// <summary>Takes this session's share of the poll's possibly-stuck processes.</summary>
    public void SetStuck(IEnumerable<StuckProcess> all)
    {
        _stuck = all.Where(s => s.Session.Root.Process.Key == Model.Root.Process.Key)
            .GroupBy(s => s.Node.Process.Key).ToDictionary(g => g.Key, g => g.First());
        Raise(nameof(StuckText));
        Raise(nameof(HasStuck));
    }

    public void SyncMembers() => CollectionSync.Sync(
        Members,
        Model.Root.SelfAndDescendants(),
        x => x.Node.Process.Key,
        r => r.Model.Node.Process.Key,
        x => new MemberRow(x, StuckOf),
        (row, x) => row.Update(x));

    private StuckProcess? StuckOf(ProcessKey key) => _stuck.GetValueOrDefault(key);
}

public sealed class MemberRow((ProcessNode Node, int Depth) item, Func<ProcessKey, StuckProcess?> stuckOf)
    : Row<(ProcessNode Node, int Depth)>(item)
{
    private ProcessInfo Process => Model.Node.Process;

    /// <summary>Set when this process may be stuck (the Stop… button acts on it).</summary>
    public StuckProcess? Stuck => stuckOf(Process.Key);
    public bool IsStuck => Stuck is not null;
    public string Status => Stuck?.Status ?? "";

    /// <summary>
    /// A stuck agent itself is only shown: AgentReclaim never stops an agent, nor a tree holding one or a node.exe
    /// whose command line couldn't be read (it might be one).
    /// </summary>
    public bool CanStop => Stuck is { } stuck && stuck.Node.SelfAndDescendants().All(x =>
        !AgentSignatures.MightBeAgent(x.Node.Process)
        && !(x.Node.Process.CommandLine is null && x.Node.Process.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)));

    public string Name => Model.Depth == 0 ? Process.Name : new string(' ', (Model.Depth - 1) * 4) + "└ " + Process.Name;
    public int Pid => Process.Pid;
    public long MemoryBytes => Process.PrivateBytes;
    public string Memory => Format.Bytes(MemoryBytes);
    public double CpuValue => Process.CpuPercent;
    public string Cpu => Format.Percent(Process.CpuPercent);
    public string Idle => Format.Duration(Model.Node.IdleFor);
    public string Ports => string.Join(", ", Process.ListeningPorts);
    public string CommandLine => Process.CommandLine ?? "";
}

public sealed class PortRow(PortEntry entry) : Row<PortEntry>(entry)
{
    public int Port => Model.Port;
    public string Name => Model.Process.Name;
    public int Pid => Model.Process.Pid;
    public string Owner => Model.Owner;
    public string Project => Model.ProjectPath is null ? "" : ProjectResolver.DisplayName(Model.ProjectPath);
    public string ProjectPath => Model.ProjectPath ?? "";
    public long MemoryBytes => Model.StopRoot.TotalPrivateBytes;
    public string Memory => MemoryBytes == 0 ? "–" : Format.Bytes(MemoryBytes); // 0: can't be read (system or elevated)
    public string CommandLine => Model.Process.CommandLine ?? "";

    /// <summary>
    /// False for what the safety policy never stops (system processes, agents, AgentReclaim) and for processes
    /// AgentReclaim can't even open (services, elevated apps: no start time read), so Free… is greyed out.
    /// </summary>
    public bool CanFree => Model.StopRoot.Process.Key.StartTicks != 0 && SafetyPolicy.CheckSubtree(Model.StopRoot, Environment.ProcessId).Allowed;

    public bool Matches(string filter) =>
        string.IsNullOrWhiteSpace(filter)
        || Port.ToString() == filter.Trim()
        || Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || Pid.ToString() == filter.Trim()
        || Project.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed class ProjectRow(ProjectSummary project) : Row<ProjectSummary>(project)
{
    public string Name => Model.Name;
    public string Path => Model.Path ?? "Working folder couldn't be read";
    public string Sessions => Model.Sessions.Count == 0 ? "" : string.Join(", ", Model.Sessions
        .GroupBy(s => s.Kind).Select(g => $"{g.Count()} {AgentSignatures.DisplayName(g.Key)}"));
    public int Processes => Model.Processes;
    public long MemoryBytes => Model.TotalBytes;
    public string Memory => Format.Bytes(MemoryBytes);
    public string Leftovers => Model.Leftovers.Count == 0 ? "" : $"{Model.Leftovers.Count} ({Format.Bytes(Model.LeftoverBytes)})";
    public string Ports => string.Join(", ", Model.Ports);

    /// <summary>e.g. "AgentReclaim: 2 sessions, 1.4 GB, port 5173".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Model.Sessions.Count > 0) parts.Add($"{Model.Sessions.Count} session{(Model.Sessions.Count == 1 ? "" : "s")}");
            if (Model.Leftovers.Count > 0) parts.Add($"{Model.Leftovers.Count} leftover{(Model.Leftovers.Count == 1 ? "" : "s")}");
            parts.Add(Memory);
            if (Model.Ports.Count > 0) parts.Add($"port{(Model.Ports.Count == 1 ? "" : "s")} {Ports}");
            return $"{Name}: {string.Join(", ", parts)}";
        }
    }
}

public sealed class MainViewModel : ObservableObject
{
    private AnalysisResult? _latest;
    private SessionRow? _selectedSession;
    private string _status = "Collecting first snapshot…";

    public ObservableCollection<OrphanRow> Orphans { get; } = [];
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public ObservableCollection<FindingRow> Findings { get; } = [];
    public ObservableCollection<PortRow> Ports { get; } = [];
    public ObservableCollection<ProjectRow> Projects { get; } = [];
    public ObservableCollection<ActivityItem> Activity { get; } = [];

    public SessionRow? SelectedSession
    {
        get => _selectedSession;
        set
        {
            _selectedSession = value;
            _selectedSession?.SyncMembers();
            Raise();
        }
    }

    public string Status => _status;

    public string SystemMemory => _latest is null ? "–" : $"{Format.Bytes(_latest.Snapshot.System.UsedMemoryBytes)} / {Format.Bytes(_latest.Snapshot.System.TotalMemoryBytes)}";
    public string SystemCpu => _latest is null ? "–" : Format.Percent(_latest.Snapshot.System.CpuPercent);
    public string AgentMemory => _latest is null ? "–" : Format.Bytes(_latest.AgentPrivateBytes);
    public string AgentMemoryDetail => _latest is null ? "" : $"{_latest.Sessions.Count} live session(s)";
    public string Leftovers => _latest is null ? "–" : _latest.Orphans.Count == 0 ? "None" : Format.Bytes(_latest.OrphanPrivateBytes);
    public string LeftoversDetail => _latest is null ? "" : $"{_latest.Orphans.Count} leftover tree(s)";

    public string OrphansHeader => $"Leftovers ({Orphans.Count})";
    public string SessionsHeader => $"Sessions ({Sessions.Count})";
    public string FindingsHeader => $"Findings ({Findings.Count})";
    public string PortsHeader => $"Ports ({Ports.Count})";
    public string ProjectsHeader => $"Projects ({Projects.Count})";

    public bool HasNoOrphans => Orphans.Count == 0;
    public bool HasNoFindings => Findings.Count == 0;
    public bool HasNoProjects => Projects.Count == 0;

    /// <summary>The last poll's projects (for tying containers to them).</summary>
    public IReadOnlyList<ProjectSummary> LatestProjects { get; private set; } = [];

    public void Update(MonitorUpdate update)
    {
        var result = update.Result;
        _latest = result;
        LatestProjects = update.Projects;

        CollectionSync.Sync(Ports, update.Ports, p => (p.Port, p.Process.Key), r => (r.Model.Port, r.Model.Process.Key),
            p => new PortRow(p), (row, p) => row.Update(p));
        CollectionSync.Sync(Projects, update.Projects, p => p.Path ?? "", r => r.Model.Path ?? "",
            p => new ProjectRow(p), (row, p) => row.Update(p));

        CollectionSync.Sync(Orphans, result.Orphans, o => o.Root.Process.Key, r => r.Model.Root.Process.Key,
            o => new OrphanRow(o), (row, o) => row.Update(o));
        CollectionSync.Sync(Sessions, result.Sessions, s => s.Root.Process.Key, r => r.Model.Root.Process.Key,
            s => new SessionRow(s), (row, s) => row.Update(s));
        foreach (var row in Sessions) row.SetStuck(result.Stuck);
        CollectionSync.Sync(Findings, result.Findings, f => f.Title, r => r.Model.Title,
            f => new FindingRow(f), (row, f) => row.Update(f));

        if (_selectedSession is null || !Sessions.Contains(_selectedSession))
            SelectedSession = Sessions.FirstOrDefault();
        else
            _selectedSession.SyncMembers();

        _status = $"Updated {DateTime.Now:T} · {result.Snapshot.Processes.Count} processes";
        RaiseAll();
    }

    public void SetStatus(string status)
    {
        _status = status;
        Raise(nameof(Status));
    }

    public void AddActivity(string message, Core.Actions.ActionLogEntry? entry = null, DateTime? whenUtc = null)
    {
        Activity.Insert(0, new ActivityItem((whenUtc ?? DateTime.UtcNow).ToLocalTime(), message, entry));
        while (Activity.Count > 500) Activity.RemoveAt(Activity.Count - 1);
    }
}

internal static class CollectionSync
{
    /// <summary>Makes <paramref name="target"/> match <paramref name="models"/>, reusing rows with the same key.</summary>
    public static void Sync<TModel, TRow, TKey>(
        ObservableCollection<TRow> target,
        IEnumerable<TModel> models,
        Func<TModel, TKey> modelKey,
        Func<TRow, TKey> rowKey,
        Func<TModel, TRow> create,
        Action<TRow, TModel> update)
        where TKey : notnull
        where TRow : class
    {
        var existing = new Dictionary<TKey, TRow>();
        foreach (var row in target) existing.TryAdd(rowKey(row), row);

        var desired = new List<TRow>();
        foreach (var model in models)
        {
            if (existing.Remove(modelKey(model), out var row))
                update(row, model);
            else
                row = create(model);
            desired.Add(row);
        }

        var keep = new HashSet<TRow>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var at = target.IndexOf(desired[i]);
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, desired[i]);
        }
    }
}
