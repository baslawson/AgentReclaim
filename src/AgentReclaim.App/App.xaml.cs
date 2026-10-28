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
    private UpdateService? _updates;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private readonly CancellationTokenSource _pipeStop = new();
    private AppSettings _published = new();

    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        var terminator = new ProcessTerminator(log);
        var viewModel = new MainViewModel();
        foreach (var entry in log.ReadRecent(100).Where(x => x.Phase == "done").Reverse())
            viewModel.AddActivity(entry.Message, entry, entry.TimeUtc);

        _monitor = new MonitorService(new ProcessCollector(), new Analyzer(new LineageStore(AppPaths.LineageFile)),
            new AutoRuleEngine(), terminator, () => Volatile.Read(ref _published));
        _updates = new UpdateService(
            () => Volatile.Read(ref _published),
            when => Dispatcher.BeginInvoke(() => settingsViewModel.RecordUpdateCheck(when)),
            () => Dispatcher.Invoke(() => _window?.IsVisible == true));
        settingsViewModel.Updates = _updates;

        _window = new MainWindow(viewModel, settingsViewModel, new CleanupViewModel(), terminator, _monitor);
        _tray = new TrayIcon(ShowWindow, paused => _monitor.Paused = paused, CheckForUpdatesFromTray, ExitApp);

        _window.ActionCompleted += result =>
            _tray.ShowMessage(result.Success ? "Freed resources" : "Couldn't stop process", result.Message, !result.Success);

        _monitor.Updated += update => Dispatcher.BeginInvoke(() =>
        {
            viewModel.Update(update);
            _tray.Update(update.Result);
        });
        _monitor.MemoryLow += alert => Dispatcher.BeginInvoke(() =>
        {
            viewModel.AddActivity(alert.Text);
            _tray.ShowMessage(alert.Title, alert.Text + " Click to open AgentReclaim.", isError: true);
        });
        _monitor.Failed += ex =>
        {
            Log.Error("Monitoring poll failed", ex);
            Dispatcher.BeginInvoke(() => viewModel.SetStatus($"Monitoring error: {ex.Message}"));
        };
        _monitor.AutoAction += action => Dispatcher.BeginInvoke(() =>
        {
            viewModel.AddActivity(action.Message, action.Entry);
            _tray.ShowMessage(action.Success ? "AgentReclaim" : "AgentReclaim: action failed", action.Message, !action.Success);
        });
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
        _monitor?.RefreshNow();
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
        _monitor?.Dispose();
        _updates?.Dispose();
        _tray?.Dispose();
        _window?.Close();
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }
}
