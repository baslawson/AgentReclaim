using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Collection;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Native;
using AgentReclaim.Core.Diagnostics;
using Microsoft.Win32.SafeHandles;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Actions;

public sealed record TerminationResult(bool Success, string Message, long FreedBytes, ActionLogEntry? Entry = null);

/// <summary>
/// Stops exactly the process instances the user (or an opt-in rule) approved, and nothing else:
/// no recursive "kill tree" that would also take processes started after approval.
/// </summary>
public sealed class ProcessTerminator(
    ActionLog log,
    Func<IReadOnlyList<(ProcessKey Key, int ParentPid, string Name)>>? readIdentities = null,
    Func<IReadOnlySet<int>, int?>? readConnections = null,
    Func<IReadOnlySet<int>, int?>? readListeners = null)
{
    private readonly Func<IReadOnlyList<(ProcessKey Key, int ParentPid, string Name)>> _readIdentities =
        readIdentities ?? ProcessCollector.ReadIdentities;

    private readonly Func<IReadOnlySet<int>, int?> _readConnections = readConnections ?? TcpTable.EstablishedFor;
    private readonly Func<IReadOnlySet<int>, int?> _readListeners = readListeners ?? TcpTable.ListenersFor;

    private const int ERROR_ACCESS_DENIED = 5;

    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <param name="trigger">"manual" or the name of the auto rule, recorded in the log.</param>
    /// <param name="requireNoConnections">
    /// Re-read the TCP tables right before stopping and refuse if any approved process has an established
    /// connection, or if the tables can't be read. Used by automatic stops.
    /// </param>
    public TerminationResult StopTree(OrphanTree tree, string trigger, bool requireNoConnections = false, Func<bool>? stillWanted = null) =>
        StopSubtree(tree.Root, AgentSignatures.DisplayName(tree.Kind), tree.Attribution.ToString(), trigger, requireNoConnections,
            stillWanted: stillWanted);

    /// <summary>
    /// Stops <paramref name="rootNode"/> and the descendants it had when it was checked, with the same
    /// re-checks as <see cref="StopTree"/>. <paramref name="agent"/> and <paramref name="attribution"/> only go in the log.
    /// </summary>
    /// <param name="requireNoListeners">
    /// Also refuse if any approved process listens on a TCP port or holds a UDP one (a server), or if those tables
    /// can't be read. Used for stuck commands, which run under a live agent.
    /// </param>
    /// <param name="stillWanted">Asked last, just before anything irreversible; false refuses the stop.</param>
    public TerminationResult StopSubtree(ProcessNode rootNode, string agent, string attribution, string trigger,
        bool requireNoConnections = false, bool requireNoListeners = false, Func<bool>? stillWanted = null)
    {
        var root = rootNode.Process;
        var approved = rootNode.SelfAndDescendants().Select(x => x.Node.Process).ToList(); // root first
        var approvedKeys = approved.Select(p => p.Key).ToHashSet();

        // 1. The tree may have changed while the user was reading the confirmation. Refuse if it gained anything.
        var current = _readIdentities();
        if (!current.Any(c => c.Key == root.Key))
            return new TerminationResult(false, $"{root.Name} (pid {root.Pid}) already exited.", 0);

        var added = LiveDescendants(root.Key, current).Where(c => !approvedKeys.Contains(c.Key)).ToList();
        if (added.Count > 0)
        {
            var names = string.Join(", ", added.Take(5).Select(a => $"{a.Name} (pid {a.Key.Pid})"));
            return new TerminationResult(false,
                $"Not stopped: {root.Name} started new processes since it was checked ({names}). Review it again.", 0);
        }

        if (requireNoConnections)
        {
            var connections = _readConnections(approvedKeys.Select(k => k.Pid).ToHashSet());
            if (connections is null)
                return new TerminationResult(false, $"Not stopped: couldn't read network connections for {root.Name}.", 0);
            if (connections > 0)
                return new TerminationResult(false, $"Not stopped: {root.Name} now has {connections} open connection(s).", 0);
        }

        if (requireNoListeners)
        {
            var listeners = _readListeners(approvedKeys.Select(k => k.Pid).ToHashSet());
            if (listeners is null)
                return new TerminationResult(false, $"Not stopped: couldn't read the ports {root.Name} uses.", 0);
            if (listeners > 0)
                return new TerminationResult(false, $"Not stopped: {root.Name} now listens on {listeners} port(s), so it may be a server.", 0);
        }

        var cwd = NativeProcess.GetCurrentDirectory(root.Pid);
        var handles = new List<(ProcessInfo Process, SafeProcessHandle Handle)>();
        try
        {
            // 2. Hold a handle on each approved instance. An open handle also stops Windows reusing its PID meanwhile.
            //    Access denied means it's still running but can't be stopped (a service, or elevated): refuse the
            //    whole tree up front rather than stop part of it and report success.
            var denied = new List<string>();
            foreach (var p in approved)
            {
                var handle = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)p.Pid);
                if (handle.IsInvalid)
                {
                    if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED) denied.Add($"{p.Name} (pid {p.Pid})");
                    handle.Dispose(); // otherwise it has exited
                    continue;
                }
                if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created != p.Key.StartTicks)
                {
                    handle.Dispose(); // a different process now has this PID
                    continue;
                }
                handles.Add((p, handle));
            }
            if (denied.Count > 0)
                return new TerminationResult(false,
                    $"Not stopped: Windows won't let AgentReclaim stop {string.Join(", ", denied.Take(5))}. It probably runs as a service or as administrator.", 0);
            if (handles.Count == 0)
                return new TerminationResult(false, $"{root.Name} (pid {root.Pid}) already exited.", 0);

            // The rule that asked for this may have been turned off since (an automatic stop planned before the switch).
            if (stillWanted?.Invoke() == false)
                return new TerminationResult(false, $"Not stopped: {root.Name} (pid {root.Pid}), because the rule was turned off.", 0);

            // 3. Record the intent before doing anything irreversible.
            var intent = Entry("intent", true, "About to stop", 0);
            if (!log.TryWrite(intent, out var logError))
                return new TerminationResult(false, $"Not stopped: couldn't write the action log ({logError}).", 0);

            // 4. Ask nicely first.
            var graceful = ConsoleSignal.TrySendCtrlC(handles.Select(h => h.Process.Pid), approvedKeys.Select(k => k.Pid).ToHashSet())
                           || TryCloseMainWindow(root.Pid);
            if (graceful) WaitForExit(handles, GracePeriod);

            // 5. Force-stop whatever approved instance is still running.
            var forced = 0;
            var failed = new List<string>();
            foreach (var (process, handle) in handles)
            {
                if (WaitForSingleObject(handle, 0) == WAIT_OBJECT_0) continue;
                if (TerminateProcess(handle, 1)) forced++;
                else failed.Add($"{process.Name} (pid {process.Pid})");
            }
            WaitForExit(handles, TimeSpan.FromSeconds(3));

            var stopped = handles.Count - failed.Count;
            var message = failed.Count == 0
                ? $"Stopped {root.Name} and {Math.Max(0, stopped - 1)} child process(es){(graceful && forced == 0 ? " gracefully" : "")}."
                : $"Stopped {stopped} of {handles.Count}; could not stop {string.Join(", ", failed)}.";
            var freed = handles.Where(h => !failed.Contains($"{h.Process.Name} (pid {h.Process.Pid})")).Sum(h => h.Process.PrivateBytes);

            var done = Entry("done", failed.Count == 0, message, freed);
            Log.Info($"Stop ({trigger}) {root.Name} pid {root.Pid}: {message}");
            if (!log.TryWrite(done, out logError))
                message += $" (Warning: couldn't record this in the action log: {logError})";
            return new TerminationResult(failed.Count == 0, message, freed, done);
        }
        finally
        {
            foreach (var (_, handle) in handles) handle.Dispose();
        }

        ActionLogEntry Entry(string phase, bool success, string message, long freed) => new(
            DateTime.UtcNow, "stop-tree", phase, trigger, success, message, root.Pid, root.Name, root.ExePath,
            root.CommandLine, cwd, approved.Count, freed, agent, attribution);
    }

    /// <summary>Current descendants of <paramref name="rootKey"/>, using the same parent-must-be-older rule as the analyzer.</summary>
    internal static IEnumerable<(ProcessKey Key, int ParentPid, string Name)> LiveDescendants(
        ProcessKey rootKey, IReadOnlyList<(ProcessKey Key, int ParentPid, string Name)> current)
    {
        var byPid = current.GroupBy(c => c.Key.Pid).ToDictionary(g => g.Key, g => g.First());
        var children = current
            .Where(c => c.ParentPid != c.Key.Pid && byPid.TryGetValue(c.ParentPid, out var parent)
                        && (parent.Key.StartTicks <= c.Key.StartTicks || c.Key.StartTicks == 0))
            .GroupBy(c => c.ParentPid)
            .ToDictionary(g => g.Key, g => g.ToList());

        var stack = new Stack<int>([rootKey.Pid]);
        var seen = new HashSet<int> { rootKey.Pid };
        while (stack.Count > 0)
        {
            if (!children.TryGetValue(stack.Pop(), out var list)) continue;
            foreach (var child in list.Where(c => seen.Add(c.Key.Pid)))
            {
                yield return child;
                stack.Push(child.Key.Pid);
            }
        }
    }

    private static bool TryCloseMainWindow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void WaitForExit(List<(ProcessInfo Process, SafeProcessHandle Handle)> handles, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        foreach (var (_, handle) in handles)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            WaitForSingleObject(handle, (uint)remaining.TotalMilliseconds);
        }
    }
}
