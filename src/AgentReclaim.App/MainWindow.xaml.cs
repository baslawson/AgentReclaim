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
using AgentReclaim.Core.Firewall;

namespace AgentReclaim.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsViewModel _settings;
    private readonly CleanupViewModel _cleanup;
    private readonly ProcessTerminator _terminator;
    private readonly MonitorService _monitor;
    private readonly ActionLog _log;
    private readonly HistoryViewModel _history = new();
    private readonly FirewallViewModel _firewall = new();
    private readonly HealthViewModel _health;
    private bool _firewallLoaded;

    public MainWindow(MainViewModel viewModel, SettingsViewModel settings, CleanupViewModel cleanup,
        ProcessTerminator terminator, MonitorService monitor, ActionLog log, HealthViewModel health)
    {
        Resources.Add("BoolToVisibility", new BooleanToVisibilityConverter());
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        SettingsTab.DataContext = _settings = settings;
        CleanupTab.DataContext = _cleanup = cleanup;
        HistoryTab.DataContext = _history;
        FirewallTab.DataContext = _firewall;
        HealthTab.DataContext = _health = health;
        _terminator = terminator;
        _monitor = monitor;
        _log = log;

        _settings.RefreshHookStatus();
        Loaded += (_, _) => RefreshWsl();
    }

    public event Action<TerminationResult>? ActionCompleted;

    /// <summary>"Run health checks" on the Health tab (the app runs them and reports the result).</summary>
    public event Action? HealthChecksRequested;

    public void ShowHealthTab() => HealthTab.IsSelected = true;

    private void RunHealthChecks_Click(object sender, RoutedEventArgs e) => HealthChecksRequested?.Invoke();

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

    /// <summary>Stops a possibly-stuck process in a live session (and what it started), after the usual checks and a confirmation.</summary>
    private async void StopStuck_Click(object sender, RoutedEventArgs e)
    {
        // async void: anything escaping here would take the app down, so nothing may escape.
        try
        {
            if ((sender as FrameworkElement)?.DataContext is not MemberRow { Stuck: { } stuck }) return;
            var root = stuck.Node;
            var agent = AgentSignatures.DisplayName(stuck.Session.Kind);
            var check = SafetyPolicy.CheckSubtree(root, Environment.ProcessId,
                [$"It is running under a live {agent} session (pid {stuck.Session.Root.Process.Pid}), which will see it fail."],
                requireKnownNodes: true); // a nested agent whose command line can't be read mustn't pass for a tool
            if (!check.Allowed)
            {
                MessageBox.Show(this, check.BlockedReason, "AgentReclaim can't stop this", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var message = new StringBuilder()
                .AppendLine($"Stop {root.Process.Name} (pid {root.Process.Pid})?").AppendLine()
                .AppendLine(stuck.Kind == StuckKind.Waiting
                    ? $"It has done nothing for {(int)stuck.For.TotalMinutes} min: it may be waiting for input nobody can see, or hung."
                    : $"It has kept a whole CPU core busy for {(int)stuck.For.TotalMinutes} min.")
                .AppendLine($"Stops it and {root.TotalCount - 1} child process(es), about {Format.Bytes(root.TotalPrivateBytes)}. It gets Ctrl+C first where that's safe, then is force-stopped.")
                .AppendLine()
                .AppendLine($"Command line: {root.Process.CommandLine ?? "(unavailable)"}");
            if (check.Warnings.Count > 0)
            {
                message.AppendLine().AppendLine("Heads up:");
                foreach (var warning in check.Warnings) message.AppendLine($"  • {warning}");
            }
            if (MessageBox.Show(this, message.ToString(), "Stop a stuck process?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                != MessageBoxResult.Yes)
                return;

            // The terminator re-reads the tree and refuses if it changed while this dialog was open.
            var result = await Task.Run(() => _terminator.StopSubtree(root, agent, "live session", "manual: stop stuck process"));
            _viewModel.AddActivity(result.Success ? $"{result.Message} Freed ~{Format.Bytes(result.FreedBytes)}." : result.Message, result.Entry);
            ActionCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            _viewModel.AddActivity($"Stopping the process failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Stop failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _monitor.RefreshNow();
        }
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
                    : "It wasn't started by Claude Code, Codex or Gemini CLI. Make sure you know what it is before stopping it.";
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

    // ---- History ----

    /// <summary>Redraws the History tab if it's showing (it is also refreshed whenever it's opened).</summary>
    public void RefreshHistory()
    {
        if (!IsVisible || !HistoryTab.IsSelected) return;
        try
        {
            _history.Update(_monitor.History?.Samples ?? [], _log.ReadRecent(5000), DateTime.UtcNow);
            Chart.SetData(_history.Times, _history.Series);
        }
        catch (Exception ex)
        {
            Log.Warning("History refresh failed", ex);
        }
    }

    private bool _containersListed;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return; // a grid or combo box inside a tab
        if (HistoryTab.IsSelected) RefreshHistory();
        if (FirewallTab.IsSelected && !_firewallLoaded && _firewall.IsIdle)
        {
            _firewallLoaded = true;
            _ = RefreshFirewallAsync();
        }
        if (CleanupTab.IsSelected && !_containersListed && _cleanup.IsIdle)
        {
            _containersListed = true;
            _ = RefreshContainersAsync();
        }
    }

    /// <summary>Legend and table swatches take their series colour from the theme, so they follow a theme switch.</summary>
    private void Swatch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Shapes.Rectangle { Tag: string key } swatch)
            swatch.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
    }

    // ---- Docker containers ----

    private async void DockerRefresh_Click(object sender, RoutedEventArgs e) => await RefreshContainersAsync();

    private async Task RefreshContainersAsync()
    {
        // async void callers: nothing may escape.
        try
        {
            _cleanup.IsBusy = true;
            _cleanup.DockerSummary = "Asking Docker…";
            var list = await Task.Run(() => DockerContainers.List());
            _cleanup.SetContainers(list, _viewModel.LatestProjects, _monitor.History?.TopProjects(50) ?? []);
        }
        catch (Exception ex)
        {
            _cleanup.DockerSummary = $"Couldn't list containers: {ex.Message}";
        }
        finally
        {
            _cleanup.IsBusy = false;
        }
    }

    private async void StopContainer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContainerRow row) return;
        var c = row.Info;
        var why = row.Link == ContainerLink.None
            ? "No AI agent has worked in its folder today, so it may be something you use. "
            : $"{row.Ai}. ";
        var text = $"Stop container {c.Name}?\n\nImage: {c.Image}\nCompose folder: {row.Folder}\n" +
                   (c.Ports.Count > 0 ? $"Ports: {string.Join(", ", c.Ports)}\n" : "") +
                   (c.MemoryBytes > 0 ? $"Memory: {row.Memory}\n" : "") +
                   $"\n{why}This runs `docker stop`: the container gets 10 seconds to shut down. " +
                   (c.AutoRemove
                       ? "\n\nTHIS CONTAINER WAS STARTED WITH --rm: when it stops, Docker DELETES it, together with its anonymous volumes " +
                         "(data not in a named volume or a folder on your PC is lost). It can't be started again, only recreated."
                       : "The container is kept (with its data), so `docker start` or `docker compose up` brings it back. Nothing is deleted.");
        if (MessageBox.Show(this, text, c.AutoRemove ? "Stop and delete container" : "Stop container", MessageBoxButton.YesNo,
                row.Link == ContainerLink.None || c.AutoRemove ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            _cleanup.IsBusy = true;
            var result = await Task.Run(() => DockerContainers.StopLogged(c, _log, "manual: stop container", row.ProjectPath));
            _viewModel.AddActivity(result.Output);
            ActionCompleted?.Invoke(new TerminationResult(result.Success, result.Output, result.Success ? c.MemoryBytes : 0));
        }
        catch (Exception ex)
        {
            _viewModel.AddActivity($"Couldn't stop container {c.Name}: {ex.Message}");
        }
        finally
        {
            _cleanup.IsBusy = false;
        }
        await RefreshContainersAsync();
    }

    // ---- Firewall ----

    private async Task RefreshFirewallAsync()
    {
        try
        {
            _firewall.IsBusy = true;
            var (status, sockets) = await Task.Run(() => (WindowsFirewall.ReadStatus(), SocketSnapshot.Read()));
            _firewall.Load(status, sockets, ProcessName);
        }
        catch (Exception ex)
        {
            Log.Warning("Couldn't read the firewall", ex);
            _viewModel.AddActivity($"Couldn't read Windows Firewall: {ex.Message}");
        }
        finally
        {
            _firewall.IsBusy = false;
        }
    }

    private static string ProcessName(int pid)
    {
        if (pid == 4) return "System";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"pid {pid}";
        }
    }

    private async void FirewallReload_Click(object sender, RoutedEventArgs e) => await RefreshFirewallAsync();

    private void FirewallAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_firewall.AddCustom(CustomPort.Text, CustomProtocol.SelectedIndex, CustomDirection.SelectedIndex) is { } error)
        {
            MessageBox.Show(this, error, "Add a port", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CustomPort.Clear();
    }

    private void FirewallRemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FirewallRow row) _firewall.RemoveCustom(row);
    }

    private async void FirewallApply_Click(object sender, RoutedEventArgs e) => await ApplyFirewallAsync(removeAll: false);

    private async void FirewallRemoveAll_Click(object sender, RoutedEventArgs e) => await ApplyFirewallAsync(removeAll: true);

    /// <summary>
    /// Plans against a fresh read of the firewall, refuses what would lock the user out, asks, then runs the
    /// elevated helper and reports what's really in place afterwards.
    /// </summary>
    private async Task ApplyFirewallAsync(bool removeAll)
    {
        // async void callers: nothing may escape.
        try
        {
            _firewall.IsBusy = true;
            var (status, sockets, remote, rdpPort) = await Task.Run(() =>
                (WindowsFirewall.ReadStatus(), SocketSnapshot.Read(), FirewallSafety.IsRemoteSession(), FirewallSafety.RdpPort()));
            if (!status.Readable)
            {
                MessageBox.Show(this, status.Error, "Firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var plan = removeAll ? FirewallPlan.Create(status, []) : _firewall.Plan(status)!;
            if (plan.IsEmpty)
            {
                MessageBox.Show(this, "The firewall already matches what's ticked.", "Firewall", MessageBoxButton.OK, MessageBoxImage.Information);
                _firewall.Load(status, sockets, ProcessName);
                return;
            }

            var issues = FirewallSafety.Check(plan.Add.Concat(plan.Repair), sockets, ProcessName, remote, rdpPort)
                .Concat(FirewallSafety.CheckRevocations(plan.Touched(status), remote, rdpPort)).ToList();
            if (issues.Where(i => i.Blocking).ToList() is { Count: > 0 } refused)
            {
                MessageBox.Show(this, "AgentReclaim won't apply this:\n\n" + string.Join("\n", refused.Select(i => "• " + i.Text)) +
                                      "\n\nUntick that port (or change its direction) and apply again.",
                    "Firewall change refused", MessageBoxButton.OK, MessageBoxImage.Stop);
                return;
            }

            var text = new StringBuilder();
            if (plan.Add.Count > 0) text.AppendLine("Block:").AppendJoin("", plan.Add.Select(r => $"  • {r.Describe()}\n"));
            if (plan.RemovalLines(status) is { Count: > 0 } removals)
                text.AppendLine("Replace or remove:").AppendJoin("", removals.Select(line => $"  • {line}\n"));
            if (plan.Blocked.Count > 0)
                text.AppendLine("Skipped (a rule AgentReclaim didn't create has the same name):").AppendJoin("", plan.Blocked.Select(r => $"  • {r.Describe()}\n"));
            if (issues.Count > 0)
            {
                text.AppendLine().AppendLine("Heads up:");
                foreach (var issue in issues) text.AppendLine($"  • {issue.Text}");
            }
            text.AppendLine()
                .AppendLine("Windows will ask for administrator permission. Block rules win over allow rules (except IPsec authenticated-bypass rules); localhost keeps working.");
            if (!status.OnForCurrentNetworks) text.AppendLine("Windows Firewall is off for the network you're on: these rules do nothing until it's on.");
            if (MessageBox.Show(this, text.ToString(), removeAll ? "Remove AgentReclaim's firewall blocks" : "Change the firewall",
                    MessageBoxButton.YesNo, issues.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var result = await FirewallChange.ApplyLoggedAsync(plan, _log, removeAll ? "manual: firewall remove all" : "manual: firewall",
                request => FirewallChange.RunElevatedAsync(Environment.ProcessPath!, request), WindowsFirewall.ReadStatus);
            _viewModel.AddActivity(result.Message);
            if (!result.Success) MessageBox.Show(this, result.Message, "Firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Warning("Firewall change failed", ex);
            _viewModel.AddActivity($"Firewall change failed: {ex.Message}");
        }
        finally
        {
            _firewall.IsBusy = false;
        }
        await RefreshFirewallAsync();
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
