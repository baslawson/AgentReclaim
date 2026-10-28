using System.Text.Json.Nodes;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

/// <summary>A Claude session (10) with a dev server (12 under bash 11), and a leftover (21) from an earlier Claude (20).</summary>
internal static class Scenario
{
    public static AnalysisResult Build(out Analyzer analyzer)
    {
        analyzer = new Analyzer(new LineageStore());
        // Minute 0: the earlier Claude (20) is alive with its server 21.
        analyzer.Analyze(Snapshot(0,
            Proc(20, 1, "claude.exe"), Proc(21, 20, "node.exe", "node vite", startMinute: 0, mb: 200, ports: [5173])));
        // Minute 30: 20 has exited; 21 is left behind. A new session 10 runs a dev server; postgres is unrelated.
        return analyzer.Analyze(Snapshot(30,
            Proc(21, 20, "node.exe", "node vite", startMinute: 0, mb: 200, ports: [5173]),
            Proc(10, 1, "claude.exe", startMinute: 10, mb: 500),
            Proc(11, 10, "bash.exe", startMinute: 11),
            Proc(12, 11, "node.exe", "node next dev", startMinute: 12, mb: 300, ports: [3000]),
            Proc(13, 12, "node.exe", "node worker", startMinute: 13, mb: 50),
            Proc(40, 1, "postgres.exe", startMinute: 1, mb: 80, ports: [5432])));
    }

    public static string? ProjectOf(ProcessInfo p) => p.Pid switch
    {
        10 or 11 or 12 or 13 => @"C:\src\app",
        21 => @"C:\src\old",
        _ => null,
    };
}

public class PortsTests
{
    [Fact]
    public void Each_port_names_its_holder_and_what_freeing_it_stops()
    {
        var ports = Insights.Ports(Scenario.Build(out _), Scenario.ProjectOf);

        Assert.Equal([3000, 5173, 5432], ports.Select(p => p.Port));

        var dev = ports[0];
        Assert.Equal(PortHolder.Session, dev.Holder);
        Assert.Contains("Claude Code session (pid 10)", dev.Owner);
        Assert.Equal(12, dev.StopRoot.Process.Pid); // the holder and its children, never the agent or the shell
        Assert.Equal(2, dev.StopRoot.TotalCount);
        Assert.Equal(@"C:\src\app", dev.ProjectPath);

        var leftover = ports[1];
        Assert.Equal(PortHolder.Leftover, leftover.Holder);
        Assert.NotNull(leftover.Leftover);
        Assert.Equal(21, leftover.StopRoot.Process.Pid);

        var other = ports[2];
        Assert.Equal(PortHolder.Other, other.Holder);
        Assert.Equal(40, other.StopRoot.Process.Pid);
        Assert.Null(other.Kind);
    }

    [Fact]
    public void Freeing_a_port_inside_a_session_is_checked_like_a_leftover()
    {
        var result = Scenario.Build(out _);
        var dev = Insights.Ports(result, Scenario.ProjectOf).Single(p => p.Port == 3000);

        var check = SafetyPolicy.CheckSubtree(dev.StopRoot, selfPid: 99, ["under a live session"]);

        Assert.True(check.Allowed);
        Assert.Equal("under a live session", check.Warnings[0]); // the caller's warning first
        Assert.Contains(check.Warnings, w => w.Contains("3000"));
    }

    [Fact]
    public void A_port_held_by_an_agent_can_not_be_freed()
    {
        var analyzer = new Analyzer(new LineageStore());
        var result = analyzer.Analyze(Snapshot(0, Proc(10, 1, "claude.exe", ports: [8123])));
        var entry = Insights.Ports(result, _ => null).Single();

        Assert.False(SafetyPolicy.CheckSubtree(entry.StopRoot, selfPid: 99).Allowed);
    }

    [Fact]
    public void Analyzer_indexes_every_process_and_survives_parent_loops()
    {
        // Unreadable processes (no start time) whose stale parent PIDs point at each other.
        static ProcessInfo NoStart(int pid, int parent) =>
            new(new ProcessKey(pid, 0), parent, "x.exe", null, null, DateTime.MinValue, 0, 0, 0, [], 0);

        var result = new Analyzer(new LineageStore()).Analyze(Snapshot(0, NoStart(100, 200), NoStart(200, 100), Proc(5, 1, "a.exe"), Proc(6, 5, "b.exe")));

        Assert.Equal(4, result.Nodes.Count);
        Assert.Equal(2, result.Nodes[Proc(5, 1, "a.exe").Key].TotalCount);
    }
}

public class ProjectTests
{
    [Fact]
    public void Sessions_and_leftovers_group_by_project_with_their_ports()
    {
        var projects = Insights.Projects(Scenario.Build(out _), Scenario.ProjectOf);

        Assert.Equal(2, projects.Count);
        var app = projects.Single(p => p.Path == @"C:\src\app");
        Assert.Single(app.Sessions);
        Assert.Empty(app.Leftovers);
        Assert.Equal([3000], app.Ports);
        Assert.Equal(4, app.Processes);
        Assert.Equal("app", app.Name);

        var old = projects.Single(p => p.Path == @"C:\src\old");
        Assert.Single(old.Leftovers);
        Assert.Equal([5173], old.Ports);
    }

    [Fact]
    public void Unknown_folders_come_last()
    {
        var analyzer = new Analyzer(new LineageStore());
        var result = analyzer.Analyze(Snapshot(0, Proc(10, 1, "claude.exe", mb: 900), Proc(30, 1, "codex.exe", mb: 10)));

        var projects = Insights.Projects(result, p => p.Pid == 30 ? @"C:\small" : null);

        Assert.Equal(@"C:\small", projects[0].Path);
        Assert.Null(projects[1].Path);
        Assert.Equal("(folder unknown)", projects[1].Name);
    }

    [Fact]
    public void Resolver_uses_the_git_root_and_caches_per_process()
    {
        var reads = 0;
        var resolver = new ProjectResolver(
            pid => { reads++; return pid == 1 ? @"C:\src\app\packages\web" : pid == 2 ? @"C:\Temp\scratch" : null; },
            dir => dir.Equals(@"C:\src\app", StringComparison.OrdinalIgnoreCase));

        var web = Proc(1, 0, "node.exe");
        Assert.Equal(@"C:\src\app", resolver.ProjectOf(web));
        Assert.Equal(@"C:\src\app", resolver.ProjectOf(web));
        Assert.Equal(1, reads);

        Assert.Equal(@"C:\Temp\scratch", resolver.ProjectOf(Proc(2, 0, "node.exe"))); // not in a repo: the folder itself
        Assert.Null(resolver.ProjectOf(Proc(3, 0, "node.exe")));

        resolver.Retain([web.Key]);
        resolver.ProjectOf(Proc(2, 0, "node.exe"));
        Assert.Equal(4, reads); // forgotten after it left the snapshot
    }

    [Fact]
    public void Network_folders_are_not_probed()
    {
        var probed = false;
        var resolver = new ProjectResolver(_ => @"\\server\share\repo", _ => probed = true);

        Assert.Equal(@"\\server\share\repo", resolver.ProjectOf(Proc(1, 0, "node.exe")));
        Assert.False(probed);
    }
}

public class MemoryAlertTests
{
    private static AnalysisResult At(double load, params ProcessInfo[] processes)
    {
        var total = 32L << 30;
        var snapshot = new ProcessSnapshot(T0, processes, new SystemStats(total, (long)(total * (1 - load)), 5));
        return new Analyzer(new LineageStore()).Analyze(snapshot);
    }

    private static readonly AppSettings On = new() { LowMemoryAlerts = true, LowMemoryAlertPercent = 90 };

    [Fact]
    public void Fires_once_after_sustained_high_memory_and_rearms_after_it_drops()
    {
        var rule = new MemoryAlertRule { Cooldown = TimeSpan.Zero };
        var now = T0;
        MemoryAlert? Poll(double load) => rule.Evaluate(At(load, Proc(10, 1, "claude.exe", mb: 2048)), On, now = now.AddSeconds(3), () => 6L << 30);

        Assert.Null(Poll(0.50));
        Assert.Null(Poll(0.93)); // one poll is a spike
        var alert = Poll(0.94);
        Assert.NotNull(alert);
        Assert.Contains("94%", alert.Text);
        Assert.Equal("WSL/Docker VM", alert.Consumers[0].What);
        Assert.Contains("Claude Code (pid 10)", alert.Text);

        Assert.Null(Poll(0.95)); // still the same episode
        Assert.Null(Poll(0.88)); // dipped, but not clearly below
        Assert.Null(Poll(0.93));
        Assert.Null(Poll(0.93));
        Assert.Null(Poll(0.80)); // re-armed
        Assert.Null(Poll(0.92));
        Assert.NotNull(Poll(0.92));
    }

    [Fact]
    public void Cooldown_limits_repeats_and_turning_it_off_silences_it()
    {
        var rule = new MemoryAlertRule { Cooldown = TimeSpan.FromMinutes(30) };
        var high = At(0.95);
        Assert.Null(rule.Evaluate(high, On, T0, () => 0));
        Assert.NotNull(rule.Evaluate(high, On, T0.AddSeconds(3), () => 0));
        rule.Evaluate(At(0.5), On, T0.AddMinutes(1), () => 0);
        rule.Evaluate(high, On, T0.AddMinutes(2), () => 0);
        Assert.Null(rule.Evaluate(high, On, T0.AddMinutes(3), () => 0)); // re-armed but within the cooldown

        var off = new MemoryAlertRule();
        var settings = new AppSettings { LowMemoryAlerts = false };
        Assert.Null(off.Evaluate(high, settings, T0, () => 0));
        Assert.Null(off.Evaluate(high, settings, T0.AddSeconds(3), () => 0));
    }

    [Fact]
    public void Consumers_are_biggest_first_and_skip_small_ones()
    {
        var result = At(0.95, Proc(10, 1, "claude.exe", mb: 700), Proc(30, 1, "codex.exe", mb: 20));

        var consumers = MemoryAlertRule.Consumers(result, wslVmBytes: 3L << 30);

        Assert.Equal(["WSL/Docker VM", "Claude Code (pid 10)"], consumers.Select(c => c.What));
    }

    [Fact]
    public void Wsl_size_is_only_read_when_alerting()
    {
        var rule = new MemoryAlertRule();
        var reads = 0;
        rule.Evaluate(At(0.5), On, T0, () => ++reads);
        rule.Evaluate(At(0.95), On, T0, () => ++reads);
        Assert.Equal(0, reads);
    }
}

public class AgentScopeTests
{
    private static (ProcessKey, int, string) Row(ProcessInfo p) => (p.Key, p.ParentPid, p.Name);
    private static ProcessKey K(int pid, int startMinute) => new(pid, T0.AddMinutes(startMinute).ToFileTimeUtc());

    // claude (10) → cmd (14) → AgentReclaim mcp (15); claude → bash (11) → dev server (12) → worker (13).
    private static readonly ProcessInfo Claude = Proc(10, 1, "claude.exe", startMinute: 10);
    private static readonly ProcessInfo Cmd = Proc(14, 10, "cmd.exe", startMinute: 14);
    private static readonly ProcessInfo Mcp = Proc(15, 14, "AgentReclaim.exe", startMinute: 15);

    private static CallerAgent Caller() =>
        AgentScope.Identify(15, [Row(Proc(1, 0, "explorer.exe")), Row(Claude), Row(Cmd), Row(Mcp)], _ => null)!;

    private static AnalysisResult Result(out Analyzer analyzer)
    {
        analyzer = new Analyzer(new LineageStore());
        return analyzer.Analyze(Snapshot(30,
            Claude, Cmd, Mcp,
            Proc(11, 10, "bash.exe", startMinute: 11),
            Proc(12, 11, "node.exe", "node next dev", startMinute: 12, ports: [3000]),
            Proc(13, 12, "node.exe", "node worker", startMinute: 13),
            Proc(50, 1, "codex.exe", startMinute: 5),
            Proc(51, 50, "node.exe", "codex child", startMinute: 6)));
    }

    [Fact]
    public void Identifies_the_nearest_agent_and_the_chain_to_it()
    {
        var caller = Caller();

        Assert.Equal(Claude.Key, caller.Agent);
        Assert.Equal(AgentKind.ClaudeCode, caller.Kind);
        Assert.Equal(new HashSet<int> { 15, 14 }, caller.Chain);
    }

    [Fact]
    public void Npm_installed_agent_is_found_from_its_command_line()
    {
        var node = Proc(10, 1, "node.exe", startMinute: 10);
        var caller = AgentScope.Identify(15, [Row(node), Row(Cmd), Row(Mcp)],
            pid => pid == 10 ? @"node C:\npm\node_modules\@anthropic-ai\claude-code\cli.js" : null);

        Assert.Equal(node.Key, caller!.Agent);
    }

    [Fact]
    public void A_nested_agent_launched_unusually_never_hands_its_server_the_outer_agents_scope()
    {
        // claude (10) → codex under node (20) → cmd (21) → mcp (22): the caller is codex, not claude.
        var codex = Proc(20, 10, "node.exe", startMinute: 16);
        var cmd = Proc(21, 20, "cmd.exe", startMinute: 17);
        var mcp = Proc(22, 21, "AgentReclaim.exe", startMinute: 18);
        (ProcessKey, int, string)[] table = [Row(Proc(1, 0, "explorer.exe")), Row(Claude), Row(codex), Row(cmd), Row(mcp)];

        // An option whose value comes as the next argument: still recognized.
        var titled = AgentScope.Identify(22, table, pid => pid == 20 ? "node --title nested C:/npm/node_modules/@openai/codex/bin/codex.js" : null);
        Assert.Equal((codex.Key, AgentKind.Codex), (titled!.Agent, titled.Kind));

        // One AgentReclaim doesn't know: no caller, rather than claude.
        Assert.Null(AgentScope.Identify(22, table, pid => pid == 20 ? "node --some-new-flag x C:/npm/node_modules/@openai/codex/bin/codex.js" : null));
    }

    [Fact]
    public void A_reused_parent_pid_or_no_agent_means_no_caller()
    {
        var youngerParent = Proc(14, 10, "cmd.exe", startMinute: 20); // started after the MCP server: not its parent
        Assert.Null(AgentScope.Identify(15, [Row(Claude), Row(youngerParent), Row(Mcp)], _ => null));
        Assert.Null(AgentScope.Identify(15, [Row(Proc(14, 1, "cmd.exe", startMinute: 14)), Row(Mcp)], _ => null));
    }

    [Fact]
    public void Lists_own_processes_but_never_the_agent_or_the_mcp_chain()
    {
        var own = AgentScope.OwnProcesses(Result(out _), Caller(), selfPid: 15);

        Assert.Equal([11, 12, 13], own.Select(p => p.Node.Process.Pid).Order());
        Assert.All(own, p => Assert.True(p.Check.Allowed));
    }

    [Fact]
    public void Refuses_the_agent_the_chain_and_other_agents_processes()
    {
        var result = Result(out _);
        var caller = Caller();

        Assert.NotNull(AgentScope.Resolve(result, caller, 15, K(12, 12)).Process);
        Assert.Contains("agent itself", AgentScope.Resolve(result, caller, 15, K(10, 10)).Refusal);
        Assert.Contains("MCP server", AgentScope.Resolve(result, caller, 15, K(14, 14)).Refusal);
        Assert.Contains("isn't one of", AgentScope.Resolve(result, caller, 15, K(51, 6)).Refusal); // Codex's child
        Assert.Contains("isn't one of", AgentScope.Resolve(result, caller, 15, K(50, 5)).Refusal);
        Assert.Contains("isn't one of", AgentScope.Resolve(result, caller, 15, K(999, 1)).Refusal);
    }

    [Fact]
    public void A_process_whose_subtree_holds_the_mcp_server_is_listed_but_not_stoppable()
    {
        // claude → bash (11) → AgentReclaim mcp (15): stopping bash would take the server with it.
        var caller = AgentScope.Identify(15, [Row(Claude), Row(Proc(11, 10, "bash.exe", startMinute: 11)), Row(Proc(15, 11, "AgentReclaim.exe", startMinute: 15))], _ => null)!;
        Assert.Contains(11, caller.Chain);

        var result = new Analyzer(new LineageStore()).Analyze(Snapshot(30,
            Claude, Proc(11, 10, "bash.exe", startMinute: 11), Proc(15, 11, "AgentReclaim.exe", startMinute: 15),
            Proc(19, 11, "conhost.exe", startMinute: 11),
            Proc(17, 10, "cmd.exe", startMinute: 17), Proc(18, 17, "AgentReclaim.exe", startMinute: 18)));
        var own = AgentScope.OwnProcesses(result, caller, selfPid: 15);

        Assert.DoesNotContain(own, p => p.Node.Process.Pid is 11 or 15 or 19); // nor the launcher's conhost
        Assert.Contains(own, p => p.Node.Process.Pid == 17); // some other helper in its tree is fine
    }

    [Fact]
    public void Own_leftovers_are_in_scope_and_other_agents_leftovers_are_not()
    {
        var analyzer = new Analyzer(new LineageStore());
        analyzer.Analyze(Snapshot(0,
            Claude, Cmd, Mcp, Proc(11, 10, "bash.exe", startMinute: 11), Proc(12, 11, "node.exe", "server", startMinute: 12),
            Proc(60, 1, "claude.exe", startMinute: 1), Proc(61, 60, "bash.exe", startMinute: 2), Proc(62, 61, "node.exe", "theirs", startMinute: 3)));
        // Bash 11 exits, leaving 12 behind as the caller's own leftover; the other Claude (60) and its bash exit, leaving 62.
        var result = analyzer.Analyze(Snapshot(30,
            Claude, Cmd, Mcp, Proc(12, 11, "node.exe", "server", startMinute: 12), Proc(62, 61, "node.exe", "theirs", startMinute: 3)));

        var caller = Caller();
        Assert.Contains(result.Orphans, o => o.Root.Process.Pid == 12 && o.Owner == Claude.Key);
        var own = AgentScope.OwnProcesses(result, caller, 15);
        Assert.Contains(own, p => p.Node.Process.Pid == 12 && p.IsLeftover);
        Assert.Contains("isn't one of", AgentScope.Resolve(result, caller, 15, K(62, 3)).Refusal);
    }
}

public class McpSelfStopTests
{
    private static readonly ProcessInfo Claude = Proc(10, 1, "claude.exe", startMinute: 10);

    private static (List<JsonNode> Replies, List<int> Stopped) Run(bool enabled, params string[] requests)
    {
        var result = new Analyzer(new LineageStore()).Analyze(Snapshot(30,
            Claude, Proc(14, 10, "AgentReclaim.exe", startMinute: 14),
            Proc(12, 10, "node.exe", "node next dev", startMinute: 12, ports: [3000]),
            Proc(50, 1, "codex.exe", startMinute: 5), Proc(51, 50, "node.exe", startMinute: 6)));
        var caller = new CallerAgent(Claude.Key, AgentKind.ClaudeCode, new HashSet<int> { 14 });
        var stopped = new List<int>();
        var scope = new McpSelfScope(
            () => new AppSettings { AllowAgentSelfStop = enabled },
            () => caller,
            (root, agent, attribution, trigger) =>
            {
                Assert.Equal("Claude Code", agent);
                Assert.StartsWith("mcp: Claude Code (pid 10)", trigger);
                stopped.Add(root.Process.Pid);
                return new TerminationResult(true, $"Stopped {root.Process.Name}.", 1);
            },
            selfPid: 14);

        var output = new StringWriter();
        new McpServer(new StringReader(string.Join('\n', requests)), output, () => result, scope).Run();
        return (output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList(), stopped);
    }

    private static string Call(int id, string tool, string args = "{}") =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{args}}}}}""";

    private static string Id(int pid, int startMinute) => $$"""{"instance_id":"{{pid}}:{{T0.AddMinutes(startMinute).ToFileTimeUtc()}}"}""";

    private static string Text(JsonNode reply) => reply["result"]!["content"]![0]!["text"]!.GetValue<string>();
    private static bool IsError(JsonNode reply) => reply["result"]!["isError"]?.GetValue<bool>() == true;

    [Fact]
    public void Stop_tool_is_listed_as_destructive()
    {
        var (replies, _) = Run(false, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = replies[0]["result"]!["tools"]!.AsArray();

        var stop = tools.Single(t => t!["name"]!.GetValue<string>() == "stop_my_process")!;
        Assert.True(stop["annotations"]!["destructiveHint"]!.GetValue<bool>());
        Assert.Equal("instance_id", stop["inputSchema"]!["required"]![0]!.GetValue<string>());
        Assert.True(tools.Single(t => t!["name"]!.GetValue<string>() == "list_my_processes")!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    [Fact]
    public void Stopping_is_off_by_default()
    {
        Assert.False(new AppSettings().AllowAgentSelfStop);

        var (replies, stopped) = Run(false, Call(1, "stop_my_process", Id(12, 12)));

        Assert.True(IsError(replies[0]));
        Assert.Contains("turned off", Text(replies[0]));
        Assert.Empty(stopped);
    }

    [Fact]
    public void When_allowed_it_stops_only_the_callers_own_processes()
    {
        var (replies, stopped) = Run(true,
            Call(1, "stop_my_process", Id(12, 12)),
            Call(2, "stop_my_process", Id(51, 6)),
            Call(3, "stop_my_process", Id(10, 10)),
            Call(4, "stop_my_process", Id(14, 14)),
            Call(5, "stop_my_process", """{"pid":12}"""));

        Assert.Equal([12], stopped);
        Assert.False(IsError(replies[0]));
        Assert.True(IsError(replies[1]));
        Assert.True(IsError(replies[2]));
        Assert.True(IsError(replies[3]));
        Assert.NotNull(replies[4]["error"]); // a bare pid isn't accepted: it needs the exact instance
    }

    [Fact]
    public void Lists_own_processes_with_whether_they_can_be_stopped()
    {
        var (replies, _) = Run(true, Call(1, "list_my_processes"));
        var listing = JsonNode.Parse(Text(replies[0]))!;

        Assert.Equal("Claude Code (pid 10)", listing["agent"]!.GetValue<string>());
        Assert.True(listing["stopping_enabled"]!.GetValue<bool>());
        var pids = listing["processes"]!.AsArray().Select(p => p!["pid"]!.GetValue<int>()).ToList();
        Assert.Equal([12], pids); // not the MCP server (14), not Codex's tree
    }
}

/// <summary>Codex review round 9.</summary>
public class CodexRound9Tests
{
    [Fact]
    public void A_reused_pid_never_matches_the_listed_instance()
    {
        var claude = Proc(10, 1, "claude.exe", startMinute: 10);
        var caller = new CallerAgent(claude.Key, AgentKind.ClaudeCode, new HashSet<int> { 14 });
        // The agent listed node 12 started at minute 12; it exited and a new in-scope process got pid 12.
        var result = new Analyzer(new LineageStore()).Analyze(Snapshot(30, claude, Proc(12, 10, "node.exe", startMinute: 25)));

        var (target, refusal) = AgentScope.Resolve(result, caller, 14, new ProcessKey(12, T0.AddMinutes(12).ToFileTimeUtc()));

        Assert.Null(target);
        Assert.Contains("that instance", refusal);
    }

    [Fact]
    public void A_process_that_cannot_be_opened_for_stopping_is_reported_as_not_stopped()
    {
        // pid 4 (System) is always alive and no one may terminate it, not even an administrator.
        var system = new ProcessInfo(new ProcessKey(4, 0), 0, "System", null, null, DateTime.MinValue, 0, 0, 0, [], 0);
        var logPath = Path.Combine(Path.GetTempPath(), $"ar-r9-{Guid.NewGuid():N}.jsonl");
        var terminator = new ProcessTerminator(new ActionLog(logPath), () => [(system.Key, 0, "System")]);

        var result = terminator.StopSubtree(new ProcessNode(system, TimeSpan.FromHours(1), []), "none", "test", "test");

        Assert.False(result.Success);
        Assert.Contains("won't let AgentReclaim stop", result.Message);
        Assert.False(File.Exists(logPath)); // nothing was attempted, so nothing logged as done
    }

    [Fact]
    public void A_failing_wsl_probe_still_alerts_and_keeps_the_rule_working()
    {
        var rule = new MemoryAlertRule();
        var settings = new AppSettings { LowMemoryAlerts = true, LowMemoryAlertPercent = 90 };
        var total = 32L << 30;
        var high = new Analyzer(new LineageStore()).Analyze(
            new ProcessSnapshot(T0, [], new SystemStats(total, (long)(total * 0.03), 5)));

        rule.Evaluate(high, settings, T0, () => throw new InvalidOperationException("vmmem unreadable"));
        var alert = rule.Evaluate(high, settings, T0.AddSeconds(3), () => throw new InvalidOperationException("vmmem unreadable"));

        Assert.NotNull(alert);
        Assert.Contains("97%", alert.Text);
    }
}

/// <summary>Codex review round 10.</summary>
public class CodexRound10Tests
{
    [Fact]
    public void A_nested_agent_keeps_its_own_leftovers_after_its_shell_exits()
    {
        // Claude (10) → Codex (30) → bash (31) → server (32); then bash exits.
        var claude = Proc(10, 1, "claude.exe", startMinute: 10);
        var codex = Proc(30, 10, "codex.exe", startMinute: 11);
        var server = Proc(32, 31, "node.exe", "server", startMinute: 13, ports: [8080]);
        var analyzer = new Analyzer(new LineageStore());
        analyzer.Analyze(Snapshot(20, claude, codex, Proc(31, 30, "bash.exe", startMinute: 12), server));
        var result = analyzer.Analyze(Snapshot(40, claude, codex, server));

        var leftover = Assert.Single(result.Orphans);
        Assert.Equal(claude.Key, leftover.Owner); // the session is still Claude's
        Assert.Contains(codex.Key, leftover.Agents);

        var asCodex = new CallerAgent(codex.Key, AgentKind.Codex, new HashSet<int> { 99 });
        Assert.Contains(AgentScope.OwnProcesses(result, asCodex, 99), p => p.Node.Process.Pid == 32 && p.IsLeftover);
        var asClaude = new CallerAgent(claude.Key, AgentKind.ClaudeCode, new HashSet<int> { 99 });
        Assert.Contains(AgentScope.OwnProcesses(result, asClaude, 99), p => p.Node.Process.Pid == 32 && p.IsLeftover);
    }

    [Fact]
    public void Merging_the_apps_lineage_keeps_what_the_mcp_server_saw_itself()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-lineage-{Guid.NewGuid():N}.json");
        try
        {
            var app = new LineageStore(path);
            app.Record(new ProcessKey(1, 100), new LineageEntry(AgentKind.Codex, 50, "codex.exe", OwnerStartTicks: 5));
            app.SaveIfDirty();

            var mcp = new LineageStore(path, readOnly: true);
            mcp.Record(new ProcessKey(2, 200), new LineageEntry(AgentKind.ClaudeCode, 10, "claude.exe", OwnerStartTicks: 7));
            mcp.Record(new ProcessKey(3, 300), new LineageEntry(AgentKind.ClaudeCode, 0, "guess", Guessed: true));

            app.Record(new ProcessKey(3, 300), new LineageEntry(AgentKind.ClaudeCode, 10, "claude.exe", OwnerStartTicks: 7));
            app.Record(new ProcessKey(4, 400), new LineageEntry(AgentKind.Codex, 50, "codex.exe", OwnerStartTicks: 5));
            app.SaveIfDirty();
            mcp.MergeFromDisk();

            Assert.Equal(10, mcp.Get(new ProcessKey(2, 200))!.OwnerPid); // its own observation survives
            Assert.False(mcp.Get(new ProcessKey(3, 300))!.Guessed);     // a seen owner beats its guess
            Assert.NotNull(mcp.Get(new ProcessKey(4, 400)));            // and it learns what the app saw since
            mcp.SaveIfDirty();
            Assert.Null(new LineageStore(path).Get(new ProcessKey(2, 200))); // still never writes the file
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Codex review round 11.</summary>
public class CodexRound11Tests
{
    [Fact]
    public void Every_agent_in_a_nested_chain_keeps_its_leftovers()
    {
        // A (10) → B (20) → C (30) → shell (31) → server (32); then the shell exits.
        var a = Proc(10, 1, "claude.exe", startMinute: 10);
        var b = Proc(20, 10, "codex.exe", startMinute: 11);
        var c = Proc(30, 20, "claude.exe", startMinute: 12);
        var server = Proc(32, 31, "node.exe", "server", startMinute: 14);
        var analyzer = new Analyzer(new LineageStore());
        analyzer.Analyze(Snapshot(20, a, b, c, Proc(31, 30, "bash.exe", startMinute: 13), server));
        var result = analyzer.Analyze(Snapshot(40, a, b, c, server));

        foreach (var agent in new[] { a, b, c })
        {
            var caller = new CallerAgent(agent.Key, AgentKind.ClaudeCode, new HashSet<int> { 99 });
            Assert.Contains(AgentScope.OwnProcesses(result, caller, 99), p => p.Node.Process.Pid == 32 && p.IsLeftover);
        }
        var stranger = new CallerAgent(Proc(50, 1, "codex.exe", startMinute: 1).Key, AgentKind.Codex, new HashSet<int> { 99 });
        Assert.DoesNotContain(AgentScope.OwnProcesses(result, stranger, 99), p => p.Node.Process.Pid == 32);
    }

    [Fact]
    public void Ancestry_round_trips_through_the_file_and_null_rows_are_skipped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-lineage-{Guid.NewGuid():N}.json");
        try
        {
            var keys = new[] { new ProcessKey(10, 5), new ProcessKey(20, 6) };
            var store = new LineageStore(path);
            store.Record(new ProcessKey(1, 100), new LineageEntry(AgentKind.ClaudeCode, 10, "claude.exe", OwnerStartTicks: 5,
                Agents: LineageEntry.FormatAgents(keys)));
            store.SaveIfDirty();
            Assert.Equal(keys.ToHashSet(), new LineageStore(path).Get(new ProcessKey(1, 100))!.AgentKeys);

            File.WriteAllText(path, "[null]");
            Assert.Null(new LineageStore(path, readOnly: true).Get(new ProcessKey(1, 100))); // no crash
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Codex review round 12.</summary>
public class CodexRound12Tests
{
    [Fact]
    public void Ancestry_survives_a_launcher_in_between_exiting()
    {
        // A (10) → wrapper (11) → B (20) → shell (21) → server (22). The wrapper exits, then the shell.
        var a = Proc(10, 1, "claude.exe", startMinute: 10);
        var b = Proc(20, 11, "codex.exe", startMinute: 12);
        var server = Proc(22, 21, "node.exe", "server", startMinute: 14);
        var shell = Proc(21, 20, "bash.exe", startMinute: 13);
        var analyzer = new Analyzer(new LineageStore());
        analyzer.Analyze(Snapshot(20, a, Proc(11, 10, "cmd.exe", startMinute: 11), b, shell, server));
        analyzer.Analyze(Snapshot(25, a, b, shell, server)); // B is now its own session root
        var result = analyzer.Analyze(Snapshot(30, a, b, server));

        var asA = new CallerAgent(a.Key, AgentKind.ClaudeCode, new HashSet<int> { 99 });
        Assert.Contains(AgentScope.OwnProcesses(result, asA, 99), p => p.Node.Process.Pid == 22 && p.IsLeftover);
    }

    [Fact]
    public void A_failed_caller_lookup_is_retried()
    {
        var calls = 0;
        var caller = new CallerAgent(new ProcessKey(10, 5), AgentKind.ClaudeCode, new HashSet<int>());
        var scope = new McpSelfScope(() => new AppSettings(),
            () => ++calls == 1 ? throw new System.ComponentModel.Win32Exception("snapshot failed") : caller,
            (_, _, _, _) => throw new InvalidOperationException(), selfPid: 1);

        Assert.Null(scope.Caller);
        Assert.Equal(caller, scope.Caller);
        Assert.Equal(caller, scope.Caller);
        Assert.Equal(2, calls); // cached once found
    }
}

/// <summary>Codex review round 13.</summary>
public class CodexRound13Tests
{
    private static (ProcessKey, int, string) Row(ProcessInfo p) => (p.Key, p.ParentPid, p.Name);

    [Fact]
    public void An_unreadable_node_ancestor_means_no_caller_rather_than_the_outer_agent()
    {
        // Claude (10) → npm Codex under node (20) → MCP (21); node's command line can't be read this time.
        var claude = Proc(10, 1, "claude.exe", startMinute: 10);
        var node = Proc(20, 10, "node.exe", startMinute: 11);
        var mcp = Proc(21, 20, "AgentReclaim.exe", startMinute: 12);
        var codexCmd = @"node C:\npm\node_modules\@openai\codex\bin\codex.js";

        Assert.Null(AgentScope.Identify(21, [Row(claude), Row(node), Row(mcp)], _ => null));
        Assert.Equal(node.Key, AgentScope.Identify(21, [Row(claude), Row(node), Row(mcp)], pid => pid == 20 ? codexCmd : null)!.Agent);
    }

    [Fact]
    public void A_detached_launcher_and_its_conhost_stay_out_of_scope()
    {
        // Agent (10) → wrapper (11) → launcher (12) → MCP (13), launcher also has conhost (14). Wrapper exits.
        var agent = Proc(10, 1, "claude.exe", startMinute: 10);
        var launcher = Proc(12, 11, "cmd.exe", startMinute: 12);
        var mcp = Proc(13, 12, "AgentReclaim.exe", startMinute: 13);
        var conhost = Proc(14, 12, "conhost.exe", startMinute: 12);
        var analyzer = new Analyzer(new LineageStore());
        analyzer.Analyze(Snapshot(20, agent, Proc(11, 10, "cmd.exe", startMinute: 11), launcher, mcp, conhost));
        var result = analyzer.Analyze(Snapshot(30, agent, launcher, mcp, conhost));
        Assert.Contains(result.Orphans, o => o.Root.Process.Pid == 12);

        var caller = new CallerAgent(agent.Key, AgentKind.ClaudeCode, new HashSet<int> { 13, 12, 11 });
        var own = AgentScope.OwnProcesses(result, caller, selfPid: 13);

        Assert.DoesNotContain(own, p => p.Node.Process.Pid is 12 or 13 or 14);
        Assert.NotNull(AgentScope.Resolve(result, caller, 13, conhost.Key).Refusal);
    }
}

/// <summary>Codex review round 14.</summary>
public class CodexRound14Tests
{
    [Fact]
    public void An_unreadable_node_under_the_caller_can_not_be_stopped_over_mcp()
    {
        // Claude (10) → node.exe (20) whose command line couldn't be read: it may be an npm-installed Codex.
        var claude = Proc(10, 1, "claude.exe", startMinute: 10);
        var unknown = Proc(20, 10, "node.exe", cmd: null, startMinute: 11);
        var child = Proc(21, 20, "node.exe", "worker", startMinute: 12);
        var result = new Analyzer(new LineageStore()).Analyze(Snapshot(30, claude, unknown, child));
        var caller = new CallerAgent(claude.Key, AgentKind.ClaudeCode, new HashSet<int> { 99 });

        var (target, _) = AgentScope.Resolve(result, caller, 99, unknown.Key);
        Assert.False(target!.Check.Allowed);
        Assert.Contains("might be an agent", target.Check.BlockedReason);
        Assert.True(AgentScope.Resolve(result, caller, 99, child.Key).Process!.Check.Allowed); // a readable one below it is fine

        // The app's own (person-confirmed) checks are unchanged.
        Assert.True(SafetyPolicy.CheckSubtree(result.Nodes[unknown.Key], 99).Allowed);
    }
}

/// <summary>Codex review round 15.</summary>
public class CodexRound15Tests
{
    [Fact]
    public void Merging_adds_agents_the_app_saw_to_what_the_mcp_server_recorded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-lineage-{Guid.NewGuid():N}.json");
        try
        {
            var a = new ProcessKey(10, 5);
            var b = new ProcessKey(20, 6);
            var server = new ProcessKey(32, 9);

            // The MCP server saw the server only under A (B's command line was unreadable then).
            var mcp = new LineageStore(path, readOnly: true);
            mcp.Record(server, new LineageEntry(AgentKind.ClaudeCode, 10, "claude.exe", OwnerStartTicks: 5, Agents: LineageEntry.FormatAgents([a])));

            // The app saw it under A and B.
            var app = new LineageStore(path);
            app.Record(server, new LineageEntry(AgentKind.ClaudeCode, 10, "claude.exe", OwnerStartTicks: 5, Agents: LineageEntry.FormatAgents([a, b])));
            app.SaveIfDirty();

            mcp.MergeFromDisk();

            Assert.Equal(new HashSet<ProcessKey> { a, b }, mcp.Get(server)!.AgentKeys);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
