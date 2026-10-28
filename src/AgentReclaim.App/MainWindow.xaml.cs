using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AgentReclaim.Core;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Updates;
using AgentReclaim.Core.Cleanup;
using AgentReclaim.Core.Collection;
using AgentReclaim.Core.Integration;

namespace AgentReclaim.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsViewModel _settings;
    private readonly CleanupViewModel _cleanup;
    private readonly ProcessTerminator _terminator;
    private readonly MonitorService _monitor;

    public MainWindow(MainViewModel viewModel, SettingsViewModel settings, CleanupViewModel cleanup,
        ProcessTerminator terminator, MonitorService monitor)
    {
        Resources.Add("BoolToVisibility", new BooleanToVisibilityConverter());
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        SettingsTab.DataContext = _settings = settings;
        CleanupTab.DataContext = _cleanup = cleanup;
        _terminator = terminator;
        _monitor = monitor;

        _settings.RefreshHookStatus();
        Loaded += (_, _) => RefreshWsl();
    }

    public event Action<TerminationResult>? ActionCompleted;

    // ---- Leftovers ----

    private async void StopOrphan_Click(object sender, RoutedEventArgs e)
    {
        // async void: anything escaping here would take the app down, so nothing may escape.
        try
        {
            if ((sender as FrameworkElement)?.DataContext is not OrphanRow row) return;
            var tree = row.Model;

            var check = SafetyPolicy.Check(tree, Environment.ProcessId);
            if (!check.Allowed)
            {
                MessageBox.Show(this, check.BlockedReason, "AgentReclaim can't stop this", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var message = new StringBuilder()
                .AppendLine($"Stop {tree.Root.Process.Name} (pid {tree.Root.Process.Pid}) and {tree.Root.TotalCount - 1} child process(es)?")
                .AppendLine($"This frees about {row.Memory}. It gets Ctrl+C first where that's safe, then is force-stopped.")
                .AppendLine()
                .AppendLine($"Started by: {tree.FormerOwner}")
                .AppendLine($"Command line: {tree.Root.Process.CommandLine ?? "(unavailable)"}");
            if (check.Warnings.Count > 0)
            {
                message.AppendLine().AppendLine("Heads up:");
                foreach (var warning in check.Warnings) message.AppendLine($"  • {warning}");
            }

            var icon = check.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question;
            if (MessageBox.Show(this, message.ToString(), "Stop leftover processes?", MessageBoxButton.YesNo, icon, MessageBoxResult.No)
                != MessageBoxResult.Yes)
                return;

            // The terminator re-reads the tree and refuses if it changed while this dialog was open.
            var result = await Task.Run(() => _terminator.StopTree(tree, "manual"));
            _viewModel.AddActivity(result.Success ? $"{result.Message} Freed ~{Format.Bytes(result.FreedBytes)}." : result.Message, result.Entry);
            ActionCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            _viewModel.AddActivity($"Stop failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Stop failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _monitor.RefreshNow();
        }
    }

    // ---- Ports ----

    private void PortFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = PortFilter.Text;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.Ports).Filter =
            string.IsNullOrWhiteSpace(filter) ? null : item => item is PortRow row && row.Matches(filter);
    }

    private async void FreePort_Click(object sender, RoutedEventArgs e)
    {
        // async void: anything escaping here would take the app down, so nothing may escape.
        try
        {
            if ((sender as FrameworkElement)?.DataContext is not PortRow row) return;
            var entry = row.Model;
            var root = entry.StopRoot;

            SafetyCheck check;
            string agent, attribution;
            if (entry.Leftover is { } leftover)
            {
                check = SafetyPolicy.Check(leftover, Environment.ProcessId);
                agent = AgentSignatures.DisplayName(leftover.Kind);
                attribution = leftover.Attribution.ToString();
            }
            else
            {
                var caution = entry.Holder == PortHolder.Session
                    ? $"It is running under a live {entry.Owner}, which may still be using it."
                    : "It wasn't started by Claude Code or Codex. Make sure you know what it is before stopping it.";
                check = SafetyPolicy.CheckSubtree(root, Environment.ProcessId, [caution]);
                agent = entry.Kind is { } kind ? AgentSignatures.DisplayName(kind) : "none";
                attribution = entry.Holder == PortHolder.Session ? "live session" : "not an AI tool";
            }

            if (!check.Allowed)
            {
                MessageBox.Show(this, check.BlockedReason, "AgentReclaim can't free this port", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var message = new StringBuilder().AppendLine($"Free port {entry.Port}?").AppendLine();
            message.AppendLine(root.Process.Pid == entry.Process.Pid
                ? $"Stops {root.Process.Name} (pid {root.Process.Pid}) and {root.TotalCount - 1} child process(es)."
                : $"{entry.Process.Name} (pid {entry.Process.Pid}) is part of a leftover tree, so the whole tree is stopped: " +
                  $"{root.Process.Name} (pid {root.Process.Pid}) and {root.TotalCount - 1} child process(es).");
            message.AppendLine($"This frees about {Format.Bytes(root.TotalPrivateBytes)}. It gets Ctrl+C first where that's safe, then is force-stopped.")
                .AppendLine()
                .AppendLine($"Belongs to: {entry.Owner}");
            if (entry.ProjectPath is { } project) message.AppendLine($"Folder: {project}");
            message.AppendLine($"Command line: {root.Process.CommandLine ?? "(unavailable)"}");
            if (check.Warnings.Count > 0)
            {
                message.AppendLine().AppendLine("Heads up:");
                foreach (var warning in check.Warnings) message.AppendLine($"  • {warning}");
            }

            if (MessageBox.Show(this, message.ToString(), $"Free port {entry.Port}?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                != MessageBoxResult.Yes)
                return;

            // The terminator re-reads the tree and refuses if it changed while this dialog was open.
            var result = await Task.Run(() => _terminator.StopSubtree(root, agent, attribution, $"manual: free port {entry.Port}"));
            _viewModel.AddActivity(result.Success ? $"Port {entry.Port}: {result.Message} Freed ~{Format.Bytes(result.FreedBytes)}." : result.Message, result.Entry);
            ActionCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            _viewModel.AddActivity($"Freeing the port failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Free port failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _monitor.RefreshNow();
        }
    }

    // ---- Activity ----

    private void Relaunch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityItem { Entry: { } entry }) return;
        var where = entry.WorkingDirectory ?? "(unknown folder)";
        if (MessageBox.Show(this, $"Start this again in {where}?\n\n{entry.CommandLine}", "Relaunch",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var (_, message) = Relauncher.Relaunch(entry);
        _viewModel.AddActivity(message);
    }

    // ---- Cleanup: WSL ----

    private async void RefreshWsl() => await RefreshWslAsync();

    private async Task RefreshWslAsync()
    {
        try
        {
            _cleanup.IsBusy = true;
            var snapshot = await Task.Run(() => new ProcessCollector().Collect());
            _cleanup.SetWsl(await Task.Run(() => WslManager.GetStatus(snapshot)));
        }
        catch (Exception ex)
        {
            _cleanup.ScanStatus = $"Couldn't check WSL: {ex.Message}";
        }
        finally
        {
            _cleanup.IsBusy = false;
        }
    }

    private void WslRefresh_Click(object sender, RoutedEventArgs e) => RefreshWsl();

    private async void WslShutdown_Click(object sender, RoutedEventArgs e) => await ShutDownWslAsync();

    private async Task ShutDownWslAsync()
    {
        var running = _cleanup.Wsl?.RunningDistros ?? [];
        var text = "This stops every WSL distro, including Docker Desktop's, and frees all of the VM's memory.\n" +
                   "Anything running inside them (servers, containers, open shells) is killed.\n\n" +
                   $"Running now: {(running.Count == 0 ? "nothing" : string.Join(", ", running))}\n\nShut down WSL?";
        if (MessageBox.Show(this, text, "Shut down WSL", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes)
            return;

        try
        {
            _cleanup.IsBusy = true;
            var result = await Task.Run(WslManager.Shutdown);
            _viewModel.AddActivity(result.Success ? "Shut down WSL." : $"WSL shutdown failed: {result.Output}");
        }
        catch (Exception ex)
        {
            _viewModel.AddActivity($"WSL shutdown failed: {ex.Message}");
        }
        finally
        {
            _cleanup.IsBusy = false;
            RefreshWsl();
        }
    }

    private void WslReclaim_Click(object sender, RoutedEventArgs e)
    {
        var text = $"Sets this in {WslManager.ConfigPath} (a backup is kept):\n\n[experimental]\nautoMemoryReclaim=dropCache\n\n" +
                   "WSL then hands memory it no longer needs back to Windows. \"dropCache\" is WSL's default and the mode known to work with Docker.\n" +
                   "It takes effect the next time WSL starts.";
        if (MessageBox.Show(this, text, "Automatic WSL memory reclaim", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
            return;

        try
        {
            _viewModel.AddActivity($"Turned on WSL autoMemoryReclaim in {WslManager.EnableAutoMemoryReclaim()}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't update .wslconfig", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshWsl();
    }

    private async void WslReclaimOff_Click(object sender, RoutedEventArgs e)
    {
        var text = $"Sets autoMemoryReclaim=disabled in {WslManager.ConfigPath} (a backup is kept; anything else in the file stays).\n" +
                   "Just removing the line isn't enough: WSL reclaims memory by default.\n\n" +
                   "WSL then keeps memory it has used until it shuts down.\n" +
                   "It takes effect the next time WSL starts.";
        if (MessageBox.Show(this, text, "Turn off automatic WSL memory reclaim", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
            return;

        try
        {
            _viewModel.AddActivity(WslManager.DisableAutoMemoryReclaim()
                ? $"Turned off WSL autoMemoryReclaim in {WslManager.ConfigPath}."
                : "WSL autoMemoryReclaim was already off.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't update .wslconfig", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshWsl();
            return;
        }
        await RefreshWslAsync();

        if (_cleanup.Wsl?.RunningDistros is { Count: > 0 }
            && MessageBox.Show(this, "WSL is running, so it still uses automatic reclaim until it restarts. Shut it down now?",
                "Turn off automatic WSL memory reclaim", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await ShutDownWslAsync(); // asks again, listing what will be stopped
    }

    // ---- Cleanup: disk ----

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _cleanup.IsBusy = true;
            _cleanup.ScanStatus = "Scanning…";
            var settings = _settings.Current.Clone();
            _cleanup.SetTargets(await Task.Run(() => DiskCleanup.Scan(settings)));
        }
        catch (Exception ex)
        {
            _cleanup.ScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _cleanup.IsBusy = false;
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CleanupRow row) return;
        var target = row.Model;
        var what = target.Method switch
        {
            CleanupMethod.RecycleFolders => $"Move {target.Paths.Count} folder(s) to the Recycle Bin?",
            CleanupMethod.ToolCommand => $"Run `{Path.GetFileName(target.Command)} {target.Arguments}`?",
            _ => $"Run `git {target.Arguments}`?\n\nThis deletes the worktree folder PERMANENTLY (not to the Recycle Bin). " +
                 "It is checked again right before removal: no changes, and no ignored files other than build output (node_modules, bin, obj, …).",
        };
        var details = string.Join("\n", target.Paths.Take(8)) + (target.Paths.Count > 8 ? $"\n… and {target.Paths.Count - 8} more" : "");
        if (MessageBox.Show(this, $"{what}\n\n{details}", target.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes)
            return;

        try
        {
            _cleanup.IsBusy = true;
            var (success, message) = await Task.Run(() => DiskCleanup.Clean(target));
            _viewModel.AddActivity(success ? $"{message} ({target.Title}, ~{row.Size})" : message);
            if (success) _cleanup.Targets.Remove(row);
        }
        catch (Exception ex)
        {
            _viewModel.AddActivity($"Cleanup failed: {ex.Message}");
        }
        finally
        {
            _cleanup.IsBusy = false;
        }
    }

    // ---- Settings: integrations ----

    private void InstallHook_Click(object sender, RoutedEventArgs e)
    {
        var text = $"Add this SessionEnd hook to {ClaudeHookInstaller.SettingsPath}?\n\n{_settings.HookCommand}\n\n" +
                   "A backup of the file is kept next to it. New Claude Code sessions pick it up.";
        if (MessageBox.Show(this, text, "Install Claude Code hook", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes)
            return;
        try
        {
            ClaudeHookInstaller.Install(SettingsViewModel.ExePath);
            _viewModel.AddActivity("Installed the Claude Code SessionEnd hook.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't install the hook", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        _settings.RefreshHookStatus();
    }

    private void RemoveHook_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClaudeHookInstaller.Uninstall();
            _viewModel.AddActivity("Removed the Claude Code SessionEnd hook.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't remove the hook", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        _settings.RefreshHookStatus();
    }

    private void CopyMcp_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(_settings.McpCommand);

    // ---- Settings: updates ----

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Updates is null) return;
        try
        {
            await _settings.Updates.CheckAsync(manual: true);
        }
        catch (Exception ex)
        {
            Log.Error("Manual update check failed", ex);
        }
        _settings.RefreshUpdateStatus();
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Updates?.Available is not { } update) return;
        if (UpdateInstaller.CannotInstallReason(update) is { } reason)
        {
            if (MessageBox.Show(this, $"{reason}\n\nOpen the release page?", "Can't install here",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenUrl(update.PageUrl);
            return;
        }

        var text = $"Install AgentReclaim {update.Version}?\n\nThe download is checked against its published SHA-256, then the app restarts in the tray.";
        if (MessageBox.Show(this, text, "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            await _settings.Updates.InstallAsync(update); // on success the app exits and the new version starts
        }
        catch (Exception ex)
        {
            Log.Error("Update install failed", ex);
        }
        _settings.RefreshUpdateStatus();
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(_settings.Updates?.Available?.PageUrl ?? AppInfo.ReleasesUrl);

    // ---- Settings: diagnostics ----

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Directory);
        OpenUrl(Log.Directory);
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(Log.Diagnostics());
        _viewModel.AddActivity("Copied diagnostics (versions and settings, no process data) to the clipboard.");
    }

    private static void OpenUrl(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Couldn't open {target}", ex);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps monitoring in the tray; Exit is in the tray menu.
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
