using System.Collections.ObjectModel;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Cleanup;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.App;

public sealed class ActivityItem(DateTime time, string message, ActionLogEntry? entry)
{
    public string Time => time.ToString("g");
    public string Message => message;
    public ActionLogEntry? Entry => entry;
    public bool CanRelaunch => entry?.CanRelaunch == true;
}

public sealed class CleanupRow(CleanupTarget target) : Row<CleanupTarget>(target)
{
    public string Title => Model.Title;
    public string Description => Model.Description;
    public string Size => Model.SizeBytes > 0 ? Format.Bytes(Model.SizeBytes) : "";
    public string Method => Model.Method switch
    {
        CleanupMethod.RecycleFolders => "To Recycle Bin",
        CleanupMethod.ToolCommand => "Tool's own clean command",
        CleanupMethod.GitWorktreeRemove => "git worktree remove",
        _ => "",
    };
}

/// <summary>A running container, tied to an AI project by its compose folder when it can be.</summary>
public sealed class ContainerRow(ContainerInfo info, string? project, ContainerLink link)
{
    public ContainerInfo Info { get; } = info;
    public string? ProjectPath { get; } = project;
    public ContainerLink Link { get; } = link;
    public string Name => Info.Name;
    public string Image => Info.Image;
    public string Memory => Info.MemoryBytes > 0 ? Format.Bytes(Info.MemoryBytes) : "–";
    public string Ports => string.Join(", ", Info.Ports.Take(3)) + (Info.Ports.Count > 3 ? ", …" : "");
    public string Folder => Info.WorkingDirectory ?? "(not started by docker compose)";
    public string Note => Info.AutoRemove ? "Deleted when stopped (--rm)" : "";
    public string Running => Info.StartedAtUtc is { } at ? Format.Duration(DateTime.UtcNow - at) : "";

    public string Ai => Link switch
    {
        ContainerLink.LiveSession => $"Agent working in {ProjectResolver.DisplayName(ProjectPath)}",
        ContainerLink.Leftover => $"{ProjectResolver.DisplayName(ProjectPath)} has leftovers",
        ContainerLink.RecentActivity => $"Agent worked in {ProjectResolver.DisplayName(ProjectPath)} today",
        _ => "",
    };
}

public sealed class CleanupViewModel : ObservableObject
{
    private string _wslSummary = "Checking WSL…";
    private string _scanStatus = "Scan to find old temp folders, caches and stale worktrees.";
    private bool _isBusy;

    public ObservableCollection<CleanupRow> Targets { get; } = [];
    public WslStatus? Wsl { get; private set; }

    /// <summary>.wslconfig turns automatic memory reclaim on (shows "Turn off" instead of "Turn on").</summary>
    public bool ReclaimOn { get; private set; }
    public bool ReclaimOff => !ReclaimOn;

    public string WslSummary { get => _wslSummary; private set { _wslSummary = value; Raise(); } }
    public string ScanStatus { get => _scanStatus; set { _scanStatus = value; Raise(); } }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Raise(); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_isBusy;

    public void SetWsl(WslStatus status)
    {
        Wsl = status;
        WslSummary = !status.Installed
            ? "WSL isn't installed."
            : $"VM memory in use: {Format.Bytes(status.VmMemoryBytes)} · Running: {(status.RunningDistros.Count == 0 ? "nothing" : string.Join(", ", status.RunningDistros))}" +
              $" · Automatic memory reclaim: {status.AutoMemoryReclaim ?? $"{WslManager.DefaultAutoMemoryReclaim} (WSL default)"}";
        ReclaimOn = WslManager.IsAutoMemoryReclaimOn(status.AutoMemoryReclaim);
        Raise(nameof(Wsl));
        Raise(nameof(ReclaimOn));
        Raise(nameof(ReclaimOff));
    }

    public void SetTargets(IReadOnlyList<CleanupTarget> targets)
    {
        Targets.Clear();
        foreach (var target in targets.OrderByDescending(t => t.SizeBytes)) Targets.Add(new CleanupRow(target));
        ScanStatus = targets.Count == 0
            ? "Nothing to clean up."
            : $"Found {targets.Count} item(s), {Format.Bytes(targets.Sum(t => t.SizeBytes))} in total.";
    }

    // ---- Docker containers ----

    private string _dockerSummary = "Refresh to list running containers.";

    public ObservableCollection<ContainerRow> Containers { get; } = [];
    public string DockerSummary { get => _dockerSummary; set { _dockerSummary = value; Raise(); } }
    public bool HasContainers => Containers.Count > 0;

    public void SetContainers(ContainerList list, IReadOnlyList<ProjectSummary> live, IEnumerable<string> recent)
    {
        Containers.Clear();
        var recentList = recent.ToList();
        var rows = list.Containers
            .Select(c => (Info: c, Relation: DockerContainers.Relate(c, live, recentList)))
            .OrderBy(x => x.Relation.Link == ContainerLink.None)
            .ThenByDescending(x => x.Info.MemoryBytes)
            .Select(x => new ContainerRow(x.Info, x.Relation.Project, x.Relation.Link));
        foreach (var row in rows) Containers.Add(row);

        var ai = Containers.Count(c => c.Link != ContainerLink.None);
        DockerSummary = list.Error ?? (Containers.Count == 0
            ? "No containers running."
            : $"{Containers.Count} running, {Format.Bytes(Containers.Sum(c => c.Info.MemoryBytes))} in all. " +
              $"{ai} in a folder an AI agent is working in or worked in today (listed first).");
        Raise(nameof(HasContainers));
    }
}

/// <summary>Edits a working copy of the settings and publishes each change (saved to disk, handed to the monitor).</summary>
public sealed class SettingsViewModel(AppSettings settings, Action<AppSettings> publish) : ObservableObject
{
    private string _hookStatus = "";

    public static string ExePath => Environment.ProcessPath ?? "AgentReclaim.exe";
    public string McpCommand => $"claude mcp add agentreclaim -- \"{ExePath}\" mcp";
    public string HookCommand => ClaudeHookInstaller.HookCommand(ExePath);

    public string HookStatus { get => _hookStatus; private set { _hookStatus = value; Raise(); } }

    public void RefreshHookStatus()
    {
        try
        {
            HookStatus = ClaudeHookInstaller.IsInstalled()
                ? "Installed: Claude Code tells AgentReclaim when a session ends."
                : "Not installed.";
        }
        catch (Exception ex)
        {
            HookStatus = $"Couldn't read {ClaudeHookInstaller.SettingsPath}: {ex.Message}";
        }
    }

    public bool AutoStopLeftovers { get => settings.AutoStopLeftovers; set => Set(() => settings.AutoStopLeftovers = value); }
    public int AutoStopIdleMinutes { get => settings.AutoStopIdleMinutes; set => Set(() => settings.AutoStopIdleMinutes = Math.Clamp(value, 1, 24 * 60)); }
    public int HookGraceSeconds { get => settings.HookGraceSeconds; set => Set(() => settings.HookGraceSeconds = Math.Clamp(value, 10, 3600)); }
    public bool AutoThrottleBuilds { get => settings.AutoThrottleBuilds; set => Set(() => settings.AutoThrottleBuilds = value); }
    public int ThrottleCpuPercent { get => settings.ThrottleCpuPercent; set => Set(() => settings.ThrottleCpuPercent = Math.Clamp(value, 10, 100)); }
    public int ThrottleAfterSeconds { get => settings.ThrottleAfterSeconds; set => Set(() => settings.ThrottleAfterSeconds = Math.Clamp(value, 5, 3600)); }
    public bool LeakWarnings { get => settings.LeakWarnings; set => Set(() => settings.LeakWarnings = value); }
    public int CleanupMinAgeDays { get => settings.CleanupMinAgeDays; set => Set(() => settings.CleanupMinAgeDays = Math.Clamp(value, 1, 365)); }

    public string ProjectRoots
    {
        get => string.Join(Environment.NewLine, settings.ProjectRoots);
        set => Set(() => settings.ProjectRoots = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    public bool StartWithWindows
    {
        get => settings.StartWithWindows;
        set => Set(() =>
        {
            AutoStart.Set(value, ExePath);
            settings.StartWithWindows = value;
        });
    }

    public bool LowMemoryAlerts { get => settings.LowMemoryAlerts; set => Set(() => settings.LowMemoryAlerts = value); }
    public int LowMemoryAlertPercent { get => settings.LowMemoryAlertPercent; set => Set(() => settings.LowMemoryAlertPercent = Math.Clamp(value, 50, 99)); }
    public bool CpuAlerts { get => settings.CpuAlerts; set => Set(() => settings.CpuAlerts = value); }
    public int CpuAlertPercent { get => settings.CpuAlertPercent; set => Set(() => settings.CpuAlertPercent = Math.Clamp(value, 50, 100)); }
    public int CpuAlertMinutes { get => settings.CpuAlertMinutes; set => Set(() => settings.CpuAlertMinutes = Math.Clamp(value, 1, 60)); }
    public bool HealthChecksOnSlowdown { get => settings.HealthChecksOnSlowdown; set => Set(() => settings.HealthChecksOnSlowdown = value); }
    public bool AllowAgentSelfStop { get => settings.AllowAgentSelfStop; set => Set(() => settings.AllowAgentSelfStop = value); }
    public bool NotifyNewLeftovers { get => settings.NotifyNewLeftovers; set => Set(() => settings.NotifyNewLeftovers = value); }
    public bool NotifyStuck { get => settings.NotifyStuck; set => Set(() => settings.NotifyStuck = value); }
    public bool AutoStopStuckWaiting { get => settings.AutoStopStuckWaiting; set => Set(() => settings.AutoStopStuckWaiting = value); }
    public bool AutoStopStuckSpinning { get => settings.AutoStopStuckSpinning; set => Set(() => settings.AutoStopStuckSpinning = value); }
    public int StuckWaitingMinutes { get => settings.StuckWaitingMinutes; set => Set(() => settings.StuckWaitingMinutes = Math.Clamp(value, 2, 240)); }
    public int StuckSpinningMinutes { get => settings.StuckSpinningMinutes; set => Set(() => settings.StuckSpinningMinutes = Math.Clamp(value, 2, 240)); }
    public bool CapSessionMemory { get => settings.CapSessionMemory; set => Set(() => settings.CapSessionMemory = value); }

    public int SessionMemoryCapGb
    {
        get => settings.SessionMemoryCapGb;
        set => Set(() => settings.SessionMemoryCapGb = Math.Clamp(value, Core.Actions.SessionMemoryCaps.MinimumGb, Core.Actions.SessionMemoryCaps.MaximumGb));
    }

    public AppSettings Current => settings;

    // ---- Diagnostics ----

    public bool DebugLogging
    {
        get => settings.LogLevel == Core.Diagnostics.LogLevel.Debug;
        set => Set(() => settings.LogLevel = value ? Core.Diagnostics.LogLevel.Debug : Core.Diagnostics.LogLevel.Info);
    }

    // ---- Appearance ----

    /// <summary>0 = Auto (match Windows), 1 = Light, 2 = Dark.</summary>
    public int ThemeIndex
    {
        get => (int)settings.Theme;
        set => Set(() => settings.Theme = (ThemePreference)Math.Clamp(value, 0, 2));
    }

    // ---- Updates ----

    public UpdateService? Updates { get; set; }

    public string VersionText => $"Version {Core.AppInfo.Version}";

    public int UpdateModeIndex
    {
        get => (int)settings.UpdateMode;
        set => Set(() => settings.UpdateMode = (Core.Updates.UpdateMode)Math.Clamp(value, 0, 2));
    }

    public string UpdateStatus => Updates?.Status ?? "";
    public bool UpdateAvailable => Updates?.Available is not null;

    public void RefreshUpdateStatus()
    {
        Raise(nameof(UpdateStatus));
        Raise(nameof(UpdateAvailable));
    }

    public void RecordUpdateCheck(DateTime whenUtc)
    {
        settings.LastUpdateCheckUtc = whenUtc;
        publish(settings.Clone());
    }

    private void Set(Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply();
        publish(settings.Clone());
        Raise(name);
    }
}

/// <summary>A project on the history chart (also the chart's table view).</summary>
public sealed class ProjectHistoryRow(string name, string path, string? brushKey, long now, long peak)
{
    public string Name => name;
    public string Path => path;
    public string? BrushKey => brushKey;
    public bool OnChart => brushKey is not null;
    public string Now => now > 0 ? Format.Bytes(now) : "–";
    public string Peak => Format.Bytes(peak);
}

/// <summary>The History tab: AI-tool memory over the last day, per project, and what was freed.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private static readonly string[] Slots = ["Series1", "Series2", "Series3", "Series4"];
    private readonly Dictionary<string, string> _slotOf = new(StringComparer.OrdinalIgnoreCase);
    private string _freedToday = "";
    private string _freedAllTime = "";

    public ObservableCollection<ProjectHistoryRow> Projects { get; } = [];
    public IReadOnlyList<DateTime> Times { get; private set; } = [];
    public IReadOnlyList<ChartSeries> Series { get; private set; } = [];
    public IReadOnlyList<ProjectHistoryRow> OnChart { get; private set; } = [];
    public string FreedToday { get => _freedToday; private set { _freedToday = value; Raise(); } }
    public string FreedAllTime { get => _freedAllTime; private set { _freedAllTime = value; Raise(); } }

    public void Update(IReadOnlyList<HistorySample> samples, IReadOnlyList<ActionLogEntry> log, DateTime nowUtc)
    {
        Times = samples.Select(s => s.TimeUtc.ToLocalTime()).ToList();

        // The four projects with the highest peak get a line. A project keeps its colour while it stays in
        // the top four, so a change in ranking doesn't repaint the others.
        var peaks = samples.SelectMany(s => s.Projects)
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, Peak: g.Max(x => x.Value)))
            .OrderByDescending(x => x.Peak)
            .ToList();
        var top = peaks.Take(Slots.Length).Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _slotOf.Keys.Where(k => !top.Contains(k)).ToList()) _slotOf.Remove(gone);
        foreach (var path in top.Where(p => !_slotOf.ContainsKey(p)))
            _slotOf[path] = Slots.First(s => !_slotOf.ContainsValue(s));

        var series = new List<ChartSeries> { new("All AI tools", "ChartInk", samples.Select(s => (long?)s.AiBytes).ToList(), IsTotal: true) };
        foreach (var (path, slot) in _slotOf.OrderBy(kv => Array.IndexOf(Slots, kv.Value)))
            series.Add(new ChartSeries(ProjectResolver.DisplayName(path), slot,
                samples.Select(s => s.Projects.TryGetValue(path, out var v) ? v : (long?)null).ToList()));
        Series = series;

        var latest = samples.Count > 0 ? samples[^1].Projects : new Dictionary<string, long>();
        Projects.Clear();
        foreach (var (path, peak) in peaks.Take(20))
            Projects.Add(new ProjectHistoryRow(ProjectResolver.DisplayName(path), path, _slotOf.GetValueOrDefault(path),
                latest.GetValueOrDefault(path), peak));

        var day = MemoryHistory.Freed(log, nowUtc - MemoryHistory.Window);
        var all = MemoryHistory.Freed(log, DateTime.MinValue);
        OnChart = _slotOf.OrderBy(kv => Array.IndexOf(Slots, kv.Value))
            .Select(kv => Projects.FirstOrDefault(p => string.Equals(p.Path, kv.Key, StringComparison.OrdinalIgnoreCase)))
            .OfType<ProjectHistoryRow>().ToList();
        Raise(nameof(OnChart));

        FreedToday = day.Stops == 0
            ? "Nothing freed in the last 24 hours."
            : $"Freed {Format.Bytes(day.Bytes)} in the last 24 hours ({day.Stops} stop(s)): " +
              $"{Format.Bytes(day.ManualBytes)} by you, {Format.Bytes(day.AutoBytes)} by automatic rules, {Format.Bytes(day.AgentBytes)} by agents through MCP.";
        FreedAllTime = all.Stops == 0 ? "" : $"In the recent action log: {Format.Bytes(all.Bytes)} over {all.Stops} stop(s).";
        Raise(nameof(Times));
        Raise(nameof(Series));
    }
}
