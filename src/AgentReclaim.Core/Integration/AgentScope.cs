using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Integration;

/// <summary>
/// The agent that started this MCP server (its nearest agent ancestor), and the processes between
/// them (<see cref="Chain"/>: the server itself and any launcher such as cmd or npx).
/// </summary>
public sealed record CallerAgent(ProcessKey Agent, AgentKind Kind, IReadOnlySet<int> Chain)
{
    public string Label => $"{AgentSignatures.DisplayName(Kind)} (pid {Agent.Pid})";
}

/// <summary>A process the calling agent may see, and whether it may stop it.</summary>
public sealed record ScopedProcess(ProcessNode Node, int Depth, bool IsLeftover, OrphanTree? Leftover, SafetyCheck Check);

/// <summary>
/// What an agent may stop through MCP: processes in its own tree and leftovers it was seen starting.
/// Never itself, never the MCP server or anything on the way to it, never another agent, and never
/// what the safety policy protects.
/// <para>
/// This is a guardrail against an agent stopping the wrong thing by mistake, not a security boundary.
/// The caller is identified from the parent-process chain, which a program running as the same user can
/// fake (Windows lets a launcher name any process it can open as the new process's parent). Such a
/// program could stop those processes directly anyway, so the opt-in grants nothing it couldn't already do.
/// </para>
/// </summary>
public static class AgentScope
{
    /// <summary>Walks up from <paramref name="selfPid"/> to the nearest agent, trusting a parent only if it started first.</summary>
    public static CallerAgent? Identify(int selfPid, IReadOnlyList<(ProcessKey Key, int ParentPid, string Name)> table,
        Func<int, string?> readCommandLine)
    {
        var byPid = table.GroupBy(t => t.Key.Pid).ToDictionary(g => g.Key, g => g.First());
        var chain = new HashSet<int> { selfPid };
        var pid = selfPid;
        for (var depth = 0; depth < 16; depth++)
        {
            if (!byPid.TryGetValue(pid, out var me) || me.ParentPid == pid || !byPid.TryGetValue(me.ParentPid, out var parent))
                return null;
            // Windows reuses PIDs: a "parent" that started after this process isn't its parent.
            if (me.Key.StartTicks == 0 || parent.Key.StartTicks == 0 || parent.Key.StartTicks > me.Key.StartTicks)
                return null;

            string? commandLine = null;
            if (parent.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
            {
                // A node.exe might be an npm-installed agent. Not knowing means not knowing who the caller is:
                // guessing past it could hand this server an outer agent's scope.
                commandLine = readCommandLine(parent.Key.Pid);
                if (commandLine is null) return null;
            }
            var probe = new ProcessInfo(parent.Key, parent.ParentPid, parent.Name, null, commandLine, DateTime.MinValue, 0, 0, 0, [], 0);
            if (AgentSignatures.AgentRootKind(probe) is { } kind)
                return new CallerAgent(parent.Key, kind, chain);
            // Names an agent's package but isn't recognized as one (an unusual launch): it may be the real caller,
            // so walking on to an outer agent could hand this server that agent's scope.
            if (AgentSignatures.MightBeAgent(probe)) return null;

            chain.Add(parent.Key.Pid);
            pid = parent.Key.Pid;
        }
        return null;
    }

    /// <summary>The caller's processes (tree order) and its own leftovers, each with whether it may be stopped.</summary>
    public static IReadOnlyList<ScopedProcess> OwnProcesses(AnalysisResult result, CallerAgent caller, int selfPid)
    {
        var list = new List<ScopedProcess>();

        // Skip the chain and everything hanging off it (e.g. the conhost of the shell that launched this
        // server), in live trees and leftovers alike: stopping those could take the server, or its launcher, down.
        void Add(ProcessNode node, int depth, OrphanTree? leftover)
        {
            if (caller.Chain.Contains(node.Process.Pid)) return;
            list.Add(new ScopedProcess(node, depth, leftover is not null, leftover, Check(node, caller, selfPid)));
            foreach (var child in node.Children) Add(child, depth + 1, leftover);
        }

        if (FindNode(result, caller.Agent) is { } agentNode)
            foreach (var child in agentNode.Children) Add(child, 0, null);

        foreach (var leftover in result.Orphans.Where(o => o.Owner == caller.Agent || o.Agents.Contains(caller.Agent)))
            Add(leftover.Root, 0, leftover);

        return list;
    }

    /// <summary>That exact process instance if it is in the caller's scope (a reused pid never matches), else why not.</summary>
    public static (ScopedProcess? Process, string? Refusal) Resolve(AnalysisResult result, CallerAgent caller, int selfPid, ProcessKey key)
    {
        if (key.Pid == caller.Agent.Pid) return (null, "That is the agent itself. AgentReclaim never stops an agent.");
        if (caller.Chain.Contains(key.Pid)) return (null, "That is this MCP server or the process that started it.");
        var match = OwnProcesses(result, caller, selfPid).FirstOrDefault(p => p.Node.Process.Key == key);
        return match is null
            ? (null, $"pid {key.Pid} (that instance) isn't one of {caller.Label}'s processes or leftovers, so it can't be stopped from here.")
            : (match, null);
    }

    private static SafetyCheck Check(ProcessNode node, CallerAgent caller, int selfPid)
    {
        var inside = node.SelfAndDescendants().Select(x => x.Node.Process.Pid).ToList();
        if (inside.Any(caller.Chain.Contains) || inside.Contains(caller.Agent.Pid))
            return new SafetyCheck(false, "It contains this MCP server or the process that started it.", []);
        return SafetyPolicy.CheckSubtree(node, selfPid, requireKnownNodes: true);
    }

    private static ProcessNode? FindNode(AnalysisResult result, ProcessKey key)
    {
        if (result.Nodes.TryGetValue(key, out var node)) return node;
        return result.Sessions.SelectMany(s => s.Root.SelfAndDescendants()).Select(x => x.Node)
            .FirstOrDefault(n => n.Process.Key == key);
    }
}
