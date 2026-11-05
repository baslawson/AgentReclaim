using System.Diagnostics;
using System.IO;
using System.Windows;
using AgentReclaim.Core;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Collection;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using AgentReclaim.Core.Updates;

namespace AgentReclaim.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private MonitorService? _monitor;
    private Core.Watchdog.WatchdogService? _watchdog;
    private UpdateService? _updates;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private HealthViewModel? _health;
    private MainViewModel? _viewModel;
    private readonly CancellationTokenSource _pipeStop = new();
    private AppSettings _published = new();
    private MonitorUpdate? _latest;
    private ReclaimTally _tally = new();
    private ActionLog? _log;

    public static bool IsExiting { get; private set; }

    private bool _watchingStopped;

    /// <summary>Once only: on quitting, or when Windows ends the session, which skips <see cref="ExitApp"/>.</summary>
    private void StopWatching()
    {
        if (_watchingStopped) return;
        _watchingStopped = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _monitor?.Dispose(); // first: a stuck command noted during the watchdog's last save would be saved as told, never told
        _watchdog?.Dispose(); // its last save: alerts handed out since the previous one aren't sent again after a reboot
    }

    // Not on SessionEnding: that only asks, and a cancelled shutdown leaves this app running. If nothing cancels, WPF
    // shuts the app down there and then, before Windows says the session has ended, so Exit is the one place that's
    // reached on every way out.
    // A wait doesn't count sleep, so the next round could be minutes off, with watchdog_status calling it stopped.
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) _watchdog?.SettingsChanged();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopWatching();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        var settings = AppSettings.Load(AppPaths.SettingsFile);
        Log.Initialize();
        Log.MinimumLevel = settings.LogLevel;
        HookUpCrashLogging();
        ThemeManager.Apply(settings.Theme);

        // After an update, the new exe waits for the old one to exit before taking over.
        WaitForPreviousInstance(e.Args);

        _singleInstance = new Mutex(true, AppPaths.SingleInstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("AgentReclaim is already running. Look for it in the system tray.", "AgentReclaim");
            Shutdown();
            return;
        }

        Log.Info($"AgentReclaim {AppInfo.Version} starting ({(AppInfo.IsSingleFileBuild ? "release" : "dev")} build, {Environment.ProcessPath}).");
        UpdateInstaller.CleanUpAfterUpdate();

        _published = settings.Clone();
        var settingsViewModel = new SettingsViewModel(settings, Publish);

        var log = new ActionLog(AppPaths.ActionLogFile);
        // Positioned before the history below is read, so a stop logged in between isn't missed.
        var logTail = new ActionLogTail(AppPaths.ActionLogFile);
        var terminator = new ProcessTerminator(log);
        var viewModel = new MainViewModel();
        _log = log;
        _tally = ReclaimTally.Load(AppPaths.ReclaimTallyFile);
        foreach (var entry in log.ReadRecent(100).Where(x => x.Phase == "done").Reverse())
            viewModel.AddActivity(entry.Message, entry, entry.TimeUtc);

        try
        {
            File.Delete(AppPaths.LegacyLineageFile); // untrusted: see AppPaths.LineageFile
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Couldn't delete the old lineage file", ex);
        }
        _monitor = new MonitorService(new ProcessCollector(), new Analyzer(new LineageStore(AppPaths.LineageFile)),
            new AutoRuleEngine(), terminator, () => Volatile.Read(ref _published), new MemoryHistory(AppPaths.HistoryFile), logTail);
        _updates = new UpdateService(
            () => Volatile.Read(ref _published),
            when => Dispatcher.BeginInvoke(() => settingsViewModel.RecordUpdateCheck(when)),
            () => Dispatcher.Invoke(() => _window?.IsVisible == true));
        settingsViewModel.Updates = _updates;

        _viewModel = viewModel;
        _health = new HealthViewModel();
        _window = new MainWindow(viewModel, settingsViewModel, new CleanupViewModel(), terminator, _monitor, log, _health);
        _tray = new TrayIcon(ShowWindow, paused => _monitor.Paused = paused, FreeSafeLeftovers, CheckForUpdatesFromTray, ExitApp,
            () => RunHealthChecks(fromTray: true));
        _window.HealthChecksRequested += () => RunHealthChecks(fromTray: false);

        _window.FreeSafeRequested += FreeSafeLeftovers;
        RefreshReclaimed();

        _window.ActionCompleted += result =>
        {
            _tray.ShowMessage(result.Success ? "Nice and tidy" : "Couldn't stop process", result.Message, !result.Success);
            ReclaimedSomething(result.Entry);
        };

        _monitor.Updated += update => Dispatcher.BeginInvoke(() =>
        {
            _latest = update;
            viewModel.Record(update.Result);
            if (_tally.NoteDay(DateOnly.FromDateTime(DateTime.Now), update.Result.Orphans.Count > 0))
            {
                _tally.Save(AppPaths.ReclaimTallyFile);
                viewModel.SetTally(_tally);
            }
            if (_window?.IsVisible == true) viewModel.Update(update); // nobody's looking otherwise
            _tray.Update(update.Result, update.SafeLeftovers);
        });
        _window.IsVisibleChanged += (_, e) =>
        {
            var visible = (bool)e.NewValue;
            _monitor.WindowVisible = visible;
            if (!visible) return;
            if (_latest is { } latest) viewModel.Update(latest); // what's known now, while the fresh poll runs
            _monitor.RefreshNow();
        };
        _monitor.WindowVisible = _window.IsVisible;
        _monitor.MemoryLow += alert => Dispatcher.BeginInvoke(() => Slowdown(alert.Title, alert.Text));
        _monitor.CpuBusy += alert => Dispatcher.BeginInvoke(() => Slowdown(alert.Title, alert.Text));
        _monitor.LeftoverAppeared += notice => Dispatcher.BeginInvoke(() =>
        {
            viewModel.AddActivity(notice.Text);
            _tray.ShowMessage(notice.Title, notice.Text + " Click to open AgentReclaim.", isError: false);
        });
        _monitor.StuckFound += notice => Dispatcher.BeginInvoke(() =>
        {
            viewModel.AddActivity(notice.Text);
            _tray.ShowMessage(notice.Title, notice.Text + " Click to open AgentReclaim.", isError: false);
        });
        _monitor.HistoryUpdated +=() => Dispatcher.BeginInvoke(() => _window?.RefreshHistory());
        _monitor.Failed += ex =>
        {
            Log.Error("Monitoring poll failed", ex);
            Dispatcher.BeginInvoke(() => viewModel.SetStatus($"Monitoring error: {ex.Message}"));
        };
        _monitor.AutoAction += action => Dispatcher.BeginInvoke(() =>
        {
            viewModel.AddActivity(action.Message, action.Entry, action.Entry?.TimeUtc);
            ReclaimedSomething(action.Entry);
            if (action.Notify)
                _tray.ShowMessage(action.Success ? "AgentReclaim" : "AgentReclaim: action failed", action.Message, !action.Success);
        });
        _watchdog = new Core.Watchdog.WatchdogService(() => Volatile.Read(ref _published), AppPaths.WatchdogFile,
            alert => Dispatcher.BeginInvoke(() =>
            {
                var text = alert.Text.Replace("WATCHDOG: ", "");
                viewModel.AddActivity(text);
                // A notification is cut short at 255 characters, so what it is now comes first.
                var lines = text.Split('\n');
                _tray.ShowMessage("AgentReclaim watchdog", lines.Length > 1 ? $"{lines[^1]}\n{lines[0]}" : text, isError: true);
            }));
        _monitor.StuckSeen += _watchdog.NoteStuck;
        _watchdog.Start();
        _monitor.Start();

        _updates.Changed += () => Dispatcher.BeginInvoke(settingsViewModel.RefreshUpdateStatus);
        _updates.Found += update => Dispatcher.BeginInvoke(() =>
        {
            if (Volatile.Read(ref _published).UpdateMode == UpdateMode.Notify)
                _tray.ShowMessage("Update available", $"AgentReclaim {update.Version} is available. Install it from Settings.", isError: false);
        });
        _updates.RestartRequired += () => Dispatcher.BeginInvoke(ExitApp);
        _updates.Start();

        // Session-end notices from the Claude Code hook.
        _ = NotificationPipe.ServeAsync(message =>
        {
            Log.Info($"Hook: session ended for agent pid {message.AgentPid} (reason {message.Reason ?? "?"}).");
            _monitor.NotifySessionEnded(message);
            Dispatcher.BeginInvoke(() => viewModel.AddActivity(
                $"Claude Code session ended (pid {message.AgentPid}{(message.Cwd is null ? "" : $", {message.Cwd}")}). Its leftovers get a short grace period."));
        }, _pipeStop.Token);

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowWindow();

        // Started by an update: tell the rollback supervisor this version came up.
        var ready = Array.FindIndex(e.Args, a => a.Equals("--ready-file", StringComparison.OrdinalIgnoreCase));
        if (ready >= 0 && ready + 1 < e.Args.Length)
        {
            UpdateInstaller.ReportReady(e.Args[ready + 1]);
            // The supervisor runs from the .old exe; remove it once that has exited.
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => UpdateInstaller.CleanUpAfterUpdate(), TaskScheduler.Default);
        }
    }

    /// <summary>Brings the lifetime total up to date from the action log.</summary>
    private void RefreshReclaimed()
    {
        if (_log is null || _viewModel is null) return;
        if (_tally.Count(_log.ReadRecent(5000))) _tally.Save(AppPaths.ReclaimTallyFile);
        _viewModel.SetTally(_tally);
    }

    /// <summary>After any stop, by anyone: adds it to the total, and if it's good news, a small celebration.</summary>
    private void ReclaimedSomething(ActionLogEntry? entry)
    {
        if (entry is null) return;
        RefreshReclaimed();
        if (!Personality.ShouldCelebrate(entry)) return;
        _tray?.Wiggle();
        _window?.Celebrate(entry.FreedBytes);
    }

    private static void WaitForPreviousInstance(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals("--wait-for-exit", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out var pid) || pid <= 0) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (!previous.WaitForExit(TimeSpan.FromSeconds(30)))
                Log.Warning($"Previous instance (pid {pid}) didn't exit within 30 s.");
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private void HookUpCrashLogging()
    {
        DispatcherUnhandledException += (_, args) => Log.Error("Unhandled UI exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error("Unobserved task exception", args.Exception);
    }

    /// <summary>
    /// Tray: one confirmation listing every leftover that passes the auto-stop checks, then the monitor stops
    /// them one per poll, re-checking each against a fresh snapshot first.
    /// </summary>
    private void FreeSafeLeftovers()
    {
        if (_monitor is null || _tray is null) return;
        if (_monitor.Paused)
        {
            _tray.ShowMessage("Monitoring is paused", "Resume monitoring to free leftovers: each one is re-checked on a fresh scan first.", isError: false);
            return;
        }
        var safe = _latest?.SafeLeftovers ?? [];
        if (safe.Count == 0)
        {
            _tray.ShowMessage("Nothing to free", "No leftover passes the safety checks right now (seen under an agent, idle, no connections).", isError: false);
            return;
        }

        var settings = Volatile.Read(ref _published);
        var lines = safe.Take(12).Select(t =>
            $"• {t.Root.Process.Name} (pid {t.Root.Process.Pid}) left by {t.FormerOwner}: {t.Root.TotalCount} process(es), {Format.Bytes(t.Root.TotalPrivateBytes)}");
        var more = safe.Count > 12 ? $"\n…and {safe.Count - 12} more" : "";
        var text = $"Stop these {safe.Count} leftover(s), {Format.Bytes(safe.Sum(t => t.Root.TotalPrivateBytes))} in all?\n\n" +
                   string.Join("\n", lines) + more +
                   $"\n\nEach was seen running under an agent that has exited, has been idle for at least {settings.AutoStopIdleMinutes} minutes, " +
                   "and has no open connections. Each is checked again just before it is stopped.";
        if (MessageBox.Show(text, "Free safe leftovers", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _monitor.RequestFree(safe.Select(t => t.Root.Process.Key));
    }

    /// <summary>
    /// A memory or CPU alert. With "run health checks on a slowdown" on, the agents' checks run in the background (at most
    /// every 30 minutes) and report on their own; otherwise the Health tab offers to run them.
    /// </summary>
    private void Slowdown(string title, string text)
    {
        if (_viewModel is null || _tray is null || _health is null) return;
        _viewModel.AddActivity(text);
        _health.Slowdown = $"{DateTime.Now:t} {title}: {text}";
        if (Volatile.Read(ref _published).HealthChecksOnSlowdown && _health.TryStartAutoRun(DateTime.UtcNow))
        {
            _tray.ShowMessage(title, text + " Checking the agents' health…", isError: true);
            RunHealthChecks(fromTray: true);
        }
        else
        {
            _tray.ShowMessage(title, text + " Click to open AgentReclaim.", isError: true);
        }
    }

    /// <summary>Runs the agents' health checks; a run from the tray or a slowdown reports in a notification.</summary>
    private async void RunHealthChecks(bool fromTray)
    {
        // async void: anything escaping here would take the app down, so nothing may escape.
        try
        {
            if (_health is null || _viewModel is null) return;
            if (!_health.IsIdle)
            {
                if (fromTray) _tray?.ShowMessage("Agent health checks", "Already running.", isError: false);
                return;
            }
            var reports = await _health.RunAsync();
            if (reports is null || reports.Count == 0) return;

            var line = AgentDoctor.OneLine(reports);
            Log.Info($"Agent health checks: {line}");
            _viewModel.AddActivity($"Agent health checks: {line}");
            if (fromTray)
                _tray?.ShowMessage("Agent health checks", line + " Click for details.",
                    isError: reports.Any(r => r.Status is HealthStatus.Warning or HealthStatus.Failed), onClick: ShowHealth);
        }
        catch (Exception ex)
        {
            Log.Error("Agent health checks failed", ex);
        }
    }

    private void ShowHealth()
    {
        ShowWindow();
        _window?.ShowHealthTab();
    }

    private async void CheckForUpdatesFromTray()
    {
        if (_updates is null || _tray is null) return;
        try
        {
            var update = await _updates.CheckAsync(manual: true);
            _tray.ShowMessage("AgentReclaim updates",
                update is null ? _updates.Status : $"Version {update.Version} is available. Open Settings to install it.", isError: false);
        }
        catch (Exception ex)
        {
            Log.Error("Tray update check failed", ex);
        }
    }

    private void Publish(AppSettings settings)
    {
        try
        {
            settings.Save(AppPaths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Settings not saved", ex);
            _tray?.ShowMessage("Settings not saved", ex.Message, isError: true);
        }
        Log.MinimumLevel = settings.LogLevel;
        if (settings.Theme != Volatile.Read(ref _published).Theme) ThemeManager.Apply(settings.Theme);
        Volatile.Write(ref _published, settings);
        _monitor?.SettingsChanged(settings);
        _watchdog?.SettingsChanged();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        Log.Info("AgentReclaim exiting.");
        IsExiting = true;
        _pipeStop.Cancel();
        StopWatching();
        _updates?.Dispose();
        _tray?.Dispose();
        _window?.Close();
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }
}
