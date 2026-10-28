using AgentReclaim.Core.Analysis;

namespace AgentReclaim.Core.Actions;

public sealed record SafetyCheck(bool Allowed, string? BlockedReason, IReadOnlyList<string> Warnings);

/// <summary>
/// Decides whether a process tree (a leftover, a port holder, or a process an agent asks to stop) may
/// be stopped: blocks the dangerous cases and lists warnings for the rest.
/// </summary>
public static class SafetyPolicy
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "svchost.exe", "dwm.exe", "explorer.exe", "sihost.exe", "fontdrvhost.exe",
        "code.exe", "cursor.exe", "windsurf.exe", "devenv.exe", "rider64.exe", "idea64.exe",
        "windowsterminal.exe", "openconsole.exe", "wsl.exe", "wslservice.exe", "vmmem", "vmmemwsl",
        "docker desktop.exe", "com.docker.backend.exe",
        // More of Windows itself (also blocked by their unreadable start times, but never by name alone either).
        "secure system", "memory compression", "lsaiso.exe", "spoolsv.exe", "wmiprvse.exe", "wmiapsrv.exe", "dllhost.exe",
        "searchindexer.exe", "searchhost.exe", "msmpeng.exe", "nissrv.exe", "mpdefendercoreservice.exe", "securityhealthservice.exe",
        "audiodg.exe", "ctfmon.exe", "taskhostw.exe", "runtimebroker.exe", "vmcompute.exe", "startmenuexperiencehost.exe",
        "shellexperiencehost.exe", "textinputhost.exe", "lockapp.exe", "userinit.exe", "logonui.exe",
    };

    /// <summary>System processes, IDEs, terminals and WSL/Docker: never stopped, and never memory-capped.</summary>
    public static bool IsProtected(string name) => ProtectedNames.Contains(name);

    public static readonly TimeSpan MinimumIdle = TimeSpan.FromMinutes(2);

    public static SafetyCheck Check(OrphanTree tree, int selfPid) =>
        CheckSubtree(tree.Root, selfPid, tree.Attribution == Attribution.Heuristic
            ? ["Its owner is guessed from its file path. Its parent had already exited when AgentReclaim started."]
            : []);

    /// <summary>
    /// The same checks for any process subtree (a port holder, or a process an agent asks to stop): never
    /// AgentReclaim, a protected process or an agent. <paramref name="extraWarnings"/> are listed first.
    /// </summary>
    /// <param name="requireKnownNodes">
    /// Refuse any node.exe whose command line couldn't be read: it could be an npm-installed agent. Used where
    /// no person confirms the stop (MCP).
    /// </param>
    public static SafetyCheck CheckSubtree(ProcessNode root, int selfPid, IReadOnlyList<string>? extraWarnings = null,
        bool requireKnownNodes = false)
    {
        var nodes = root.SelfAndDescendants().Select(x => x.Node).ToList();

        if (nodes.Any(n => n.Process.Pid == selfPid))
            return Blocked("This tree contains AgentReclaim itself.");
        if (nodes.FirstOrDefault(n => ProtectedNames.Contains(n.Process.Name)) is { } protectedNode)
            return Blocked($"{protectedNode.Process.Name} is on the protected list.");
        // Without a start time a PID can't be told apart from a later process that reused it, and Windows hides
        // start times only for processes AgentReclaim shouldn't touch anyway (other users', system services).
        if (nodes.FirstOrDefault(n => n.Process.StartTimeUtc == DateTime.MinValue) is { } unverifiable)
            return Blocked($"Couldn't read when {unverifiable.Process.Name} (pid {unverifiable.Process.Pid}) started, so AgentReclaim can't be sure it's the process it saw (it may be a system or another user's process).");
        if (nodes.FirstOrDefault(n => AgentSignatures.MightBeAgent(n.Process)) is { } agent)
            return Blocked($"{agent.Process.Name} (pid {agent.Process.Pid}) is an agent itself, which AgentReclaim never stops.");
        if (requireKnownNodes && nodes.FirstOrDefault(n => n.Process.CommandLine is null
                && n.Process.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)) is { } unknown)
            return Blocked($"Couldn't read the command line of node.exe (pid {unknown.Process.Pid}), so it might be an agent. Try again in a moment.");

        var warnings = new List<string>(extraWarnings ?? []);

        var connections = nodes.Sum(n => n.Process.EstablishedConnections);
        if (connections > 0)
            warnings.Add($"It has {connections} open network connection(s), so something may still be using it.");

        var ports = nodes.SelectMany(n => n.Process.ListeningPorts).Distinct().ToList();
        if (ports.Count > 0)
            warnings.Add($"It is listening on port(s) {string.Join(", ", ports)}.");

        if (nodes.Any(n => n.IdleFor < MinimumIdle))
            warnings.Add($"Part of it used CPU in the last {MinimumIdle.TotalMinutes:0} minutes.");

        return new SafetyCheck(true, null, warnings);

        static SafetyCheck Blocked(string reason) => new(false, reason, []);
    }
}
