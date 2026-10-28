using AgentReclaim.Core.Analysis;

namespace AgentReclaim.Core.Actions;

public sealed record SafetyCheck(bool Allowed, string? BlockedReason, IReadOnlyList<string> Warnings);

/// <summary>
/// Decides whether an orphan tree may be stopped. Phase 1 only stops orphan trees, and only
/// after the user confirms, so this blocks the dangerous cases and lists warnings for the rest.
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
    };

    public static readonly TimeSpan MinimumIdle = TimeSpan.FromMinutes(2);

    public static SafetyCheck Check(OrphanTree tree, int selfPid)
    {
        var nodes = tree.Root.SelfAndDescendants().Select(x => x.Node).ToList();

        if (nodes.Any(n => n.Process.Pid == selfPid))
            return Blocked("This tree contains AgentReclaim itself.");
        if (nodes.FirstOrDefault(n => ProtectedNames.Contains(n.Process.Name)) is { } protectedNode)
            return Blocked($"{protectedNode.Process.Name} is on the protected list.");
        if (nodes.FirstOrDefault(n => AgentSignatures.AgentRootKind(n.Process) is not null) is { } agent)
            return Blocked($"{agent.Process.Name} (pid {agent.Process.Pid}) is an agent itself, not a leftover.");

        var warnings = new List<string>();
        if (tree.Attribution == Attribution.Heuristic)
            warnings.Add("Its owner is guessed from its file path. Its parent had already exited when AgentReclaim started.");

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
