using System.Collections.ObjectModel;
using AgentReclaim.Core.Actions;
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
