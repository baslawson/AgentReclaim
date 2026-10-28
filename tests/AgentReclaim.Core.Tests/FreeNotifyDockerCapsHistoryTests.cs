using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Cleanup;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Native;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

public class ActionLogTailTests
{
    private static ActionLogEntry Entry(string trigger, string phase = "done") =>
        new(DateTime.UtcNow, "stop-tree", phase, trigger, true, $"Stopped by {trigger}", 1, "node.exe", null, null, null, 1, 100, "", "");

    [Fact]
    public void Sees_only_lines_written_after_it_started_and_waits_for_a_whole_line()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-tail-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new ActionLog(path);
            log.TryWrite(Entry("manual"), out _);
            var tail = new ActionLogTail(path);
            Assert.Empty(tail.ReadNew()); // history, already shown at startup

            log.TryWrite(Entry("mcp: Claude Code (pid 5)"), out _);
            Assert.Equal("mcp: Claude Code (pid 5)", Assert.Single(tail.ReadNew()).Trigger);
            Assert.Empty(tail.ReadNew());

            // Another process is half-way through writing a line: nothing until it is complete.
            var line = System.Text.Json.JsonSerializer.Serialize(Entry("mcp: Codex (pid 6)"));
            File.AppendAllText(path, line[..20]);
            Assert.Empty(tail.ReadNew());
            File.AppendAllText(path, line[20..] + Environment.NewLine);
            Assert.Equal("mcp: Codex (pid 6)", Assert.Single(tail.ReadNew()).Trigger);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_replaced_log_is_read_from_the_start_and_a_missing_one_is_not_an_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-tail-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tail = new ActionLogTail(path);
            Assert.Empty(tail.ReadNew());

            var log = new ActionLog(path);
            log.TryWrite(Entry("mcp: a"), out _);
            log.TryWrite(Entry("mcp: b"), out _);
            Assert.Equal(2, tail.ReadNew().Count);

            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(Entry("mcp: c")) + "\n");
            Assert.Equal("mcp: c", Assert.Single(tail.ReadNew()).Trigger);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class FreeSafeLeftoversTests
{
    private static AnalysisResult IdleLeftover(int idleMinutes, int connections = 0)
    {
        var analyzer = new Analyzer(new LineageStore());
        var server = Proc(12, 10, "node.exe", startMinute: 1, connections: connections);
        analyzer.Analyze(Snapshot(2, Proc(10, 1, "claude.exe"), server));
        analyzer.Analyze(Snapshot(3, server));
        return analyzer.Analyze(Snapshot(3 + idleMinutes, server));
    }

    [Fact]
    public void Safe_leftovers_ignore_the_auto_stop_switch_but_keep_every_check()
    {
        var off = new AppSettings { AutoStopLeftovers = false, AutoStopIdleMinutes = 15 };
        var rules = new AutoRuleEngine();

        Assert.Empty(rules.LeftoversToStop(IdleLeftover(60), off, 1)); // automatic stopping is off
        Assert.Single(rules.SafeLeftovers(IdleLeftover(60), off, 1)); // but the tray can offer it

        Assert.Empty(rules.SafeLeftovers(IdleLeftover(5), off, 1)); // not idle long enough
        Assert.Empty(rules.SafeLeftovers(IdleLeftover(60, connections: 1), off, 1)); // something is talking to it
        Assert.Empty(rules.SafeLeftovers(IdleLeftover(60) is var r ? r with { Snapshot = r.Snapshot with { NetworkComplete = false } } : null!, off, 1));
    }

    [Fact]
    public void A_tree_an_auto_stop_already_tried_is_still_offered_to_the_user()
    {
        var on = new AppSettings { AutoStopLeftovers = true, AutoStopIdleMinutes = 15 };
        var rules = new AutoRuleEngine();
        var result = IdleLeftover(60);
        rules.RecordStopAttempt(Assert.Single(rules.LeftoversToStop(result, on, 1)));

        Assert.Empty(rules.LeftoversToStop(result, on, 1));
        Assert.Single(rules.SafeLeftovers(result, on, 1));
    }
}

public class LeftoverWatchTests
{
    private static AnalysisResult Poll(Analyzer analyzer, int minute, params ProcessInfo[] processes) =>
        analyzer.Analyze(Snapshot(minute, processes));

    [Fact]
    public void Announces_a_new_leftover_once_after_two_polls_and_never_the_ones_there_at_startup()
    {
        var analyzer = new Analyzer(new LineageStore());
        var watch = new LeftoverWatch();
        var old = Proc(31, 30, "node.exe", startMinute: 0);
        var vite = Proc(21, 20, "node.exe", "node vite", startMinute: 1, ports: [5173]);

        // Startup: an earlier leftover (31, owner 30 gone) is already there.
        Poll(analyzer, 0, Proc(30, 1, "claude.exe"), old);
        Assert.Null(watch.Evaluate(Poll(analyzer, 1, old, Proc(20, 1, "claude.exe"), vite), true, _ => null));

        // Claude 20 exits, leaving vite.
        Assert.Null(watch.Evaluate(Poll(analyzer, 2, old, vite), true, _ => @"C:\src\shop")); // first sighting: wait
        var notice = watch.Evaluate(Poll(analyzer, 3, old, vite), true, _ => @"C:\src\shop");
        Assert.NotNull(notice);
        Assert.Equal("Claude Code left node.exe on port 5173 (shop) running.", notice!.Text);
        Assert.Single(notice.Trees);

        Assert.Null(watch.Evaluate(Poll(analyzer, 4, old, vite), true, _ => null)); // once only
    }

    [Fact]
    public void A_leftover_that_is_gone_by_the_next_poll_is_never_announced_and_off_means_silent()
    {
        var analyzer = new Analyzer(new LineageStore());
        var watch = new LeftoverWatch();
        var shell = Proc(21, 20, "bash.exe", startMinute: 1);
        Assert.Null(watch.Evaluate(Poll(analyzer, 1, Proc(20, 1, "claude.exe"), shell), true, _ => null));
        Assert.Null(watch.Evaluate(Poll(analyzer, 2, shell), true, _ => null));
        Assert.Null(watch.Evaluate(Poll(analyzer, 3), true, _ => null)); // finished on its own

        var server = Proc(23, 22, "node.exe", startMinute: 4);
        Assert.Null(watch.Evaluate(Poll(analyzer, 4, Proc(22, 1, "claude.exe"), server), false, _ => null));
        Assert.Null(watch.Evaluate(Poll(analyzer, 5, server), false, _ => null));
        Assert.Null(watch.Evaluate(Poll(analyzer, 6, server), false, _ => null)); // off: tracked, not announced
        Assert.Null(watch.Evaluate(Poll(analyzer, 7, server), true, _ => null)); // turning it on doesn't announce old ones
    }
}

public class DockerContainerTests
{
    private const string IdA = "0aef237f07eb7e36eb7b29996933a53182c265c395d78e4d35622c47e55f2d35";
    private const string IdB = "24fa92a1daab200ccea52b8a09e1d2ee9edee48adffcd7b75931ff9b42484e0d";

    private static readonly string Inspect = """
        [
          {"Id":"ID_A","Name":"/shop-db-1","State":{"Status":"running","StartedAt":"2026-10-28T08:00:00.123Z"},
           "Config":{"Image":"postgres:16","Labels":{"com.docker.compose.project":"shop","com.docker.compose.service":"db",
             "com.docker.compose.project.working_dir":"C:\\src\\shop\\infra"}},
           "NetworkSettings":{"Ports":{"5432/tcp":[{"HostIp":"127.0.0.1","HostPort":"5432"}],"9000/tcp":null}}},
          {"Id":"ID_B","Name":"/loose","State":{"Status":"running","StartedAt":"0001-01-01T00:00:00Z"},
           "Config":{"Image":"redis","Labels":null},"HostConfig":{"AutoRemove":true},"NetworkSettings":{"Ports":{}}},
          {"Id":"not-an-id","Name":"/bad"}
        ]
        """.Replace("ID_A", IdA).Replace("ID_B", IdB);

    [Fact]
    public void Parses_inspect_output_with_compose_folder_ports_and_start_time()
    {
        var list = DockerContainers.ParseInspect("warning: something\n" + Inspect);
        Assert.Equal(2, list.Count);

        var db = list[0];
        Assert.Equal("shop-db-1", db.Name);
        Assert.Equal("postgres:16", db.Image);
        Assert.Equal(@"C:\src\shop\infra", db.WorkingDirectory);
        Assert.Equal("shop", db.ComposeProject);
        Assert.Equal(["5432→5432/tcp"], db.Ports);
        Assert.Equal(new DateTime(2026, 10, 28, 8, 0, 0, 123, DateTimeKind.Utc), db.StartedAtUtc);

        Assert.Null(list[1].WorkingDirectory);
        Assert.Null(list[1].StartedAtUtc);
        Assert.False(db.AutoRemove);
        Assert.True(list[1].AutoRemove); // --rm: stopping deletes it
        Assert.Empty(DockerContainers.ParseInspect("Error: No such object"));
    }

    [Fact]
    public void Stats_memory_is_read_per_full_id()
    {
        var stats = DockerContainers.ParseStats($"{IdA}|127.3MiB / 15.56GiB\n{IdB}|1.5GiB / 15.56GiB\nshort|1MiB / 2GiB\n{IdA[..63]}x|junk");
        Assert.Equal((long)(127.3 * 1024 * 1024), stats[IdA]);
        Assert.Equal((long)(1.5 * 1024 * 1024 * 1024), stats[IdB]);
        Assert.Equal(2, stats.Count);
    }

    [Fact]
    public void A_container_belongs_to_the_deepest_ai_project_holding_its_compose_folder()
    {
        var db = DockerContainers.ParseInspect(Inspect)[0];
        Assert.True(DockerContainers.IsInProject(db, @"C:\src\shop"));
        Assert.True(DockerContainers.IsInProject(db, "C:/src/shop/"));
        Assert.False(DockerContainers.IsInProject(db, @"C:\src\sho"));
        Assert.False(DockerContainers.IsInProject(db, @"C:\src\shop\infra2"));

        var result = Scenario.Build(out _);
        var session = new ProjectSummary(@"C:\src\shop", result.Sessions, [], []);
        var leftover = new ProjectSummary(@"C:\src\shop\infra", [], result.Orphans, []);
        Assert.Equal((@"C:\src\shop", ContainerLink.LiveSession), DockerContainers.Relate(db, [session, leftover], []));
        Assert.Equal((@"C:\src\shop\infra", ContainerLink.Leftover), DockerContainers.Relate(db, [leftover], []));
        Assert.Equal((@"C:\src\shop", ContainerLink.RecentActivity), DockerContainers.Relate(db, [], [@"C:\src", @"C:\src\shop", @"C:\other"]));
        Assert.Equal(((string?)null, ContainerLink.None), DockerContainers.Relate(db, [], [@"C:\other"]));
    }

    [Fact]
    public void Stopping_writes_intent_first_and_refuses_without_a_log()
    {
        var c = DockerContainers.ParseInspect(Inspect)[0] with { MemoryBytes = 50L << 20 };
        var path = Path.Combine(Path.GetTempPath(), $"ar-docker-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new ActionLog(path);
            var stopped = new List<string>();
            var result = DockerContainers.StopLogged(c, log, "manual: stop container", @"C:\src\shop", id =>
            {
                Assert.Single(log.ReadRecent(10)); // the intent line is already there
                stopped.Add(id);
                return new ToolResult(true, 0, id);
            });
            Assert.True(result.Success);
            Assert.Equal([IdA], stopped);
            var done = log.ReadRecent(10)[0];
            Assert.Equal(("docker-stop", "done", 50L << 20), (done.Action, done.Phase, done.FreedBytes));

            // A log that can't be written means no stop.
            var blocked = new ActionLog(Path.GetTempPath()); // a folder, so appending fails
            var called = false;
            var refused = DockerContainers.StopLogged(c, blocked, "manual", null, _ => { called = true; return new ToolResult(true, 0, ""); });
            Assert.False(refused.Success);
            Assert.False(called);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.False(DockerContainers.Stop("shop-db-1").Success); // names never, only full ids
    }
}

public class SessionMemoryCapTests
{
    private static Process StartPing() =>
        Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;

    private static ProcessInfo Real(Process p, string? name = null) =>
        new(new ProcessKey(p.Id, NativeProcess.GetStartTicks(p.Id)!.Value), 1, name ?? "PING.EXE", null, "ping -n 30 127.0.0.1",
            DateTime.UtcNow, 1, 1, 0, [], 0);

    private static ProcessInfo FakeAgent(int pid) => new(new ProcessKey(pid, 123_456_789), 1, "claude.exe", null, null, DateTime.UtcNow, 1, 1, 0, [], 0);

    /// <summary>A session for <paramref name="agent"/> (if given) with these children; every process listed is "running".</summary>
    private static AnalysisResult Poll(ProcessInfo? agent, params ProcessInfo[] children)
    {
        var sessions = agent is null
            ? new List<AgentSession>()
            : [new AgentSession(AgentKind.ClaudeCode, new ProcessNode(agent, TimeSpan.Zero, children.Select(c => new ProcessNode(c, TimeSpan.Zero, [])).ToList()))];
        var running = (agent is null ? children : children.Prepend(agent)).ToArray();
        return new AnalysisResult(Snapshot(0, running), sessions, [], []);
    }

    private static bool InJob(Process p, SafeHandle? job)
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)p.Id);
        return job is Microsoft.Win32.SafeHandles.SafeFileHandle j && NativeMethods.IsProcessInJob(handle, j, out var inJob) && inJob;
    }

    private static (uint Flags, ulong Limit) Limits(SafeHandle job)
    {
        NativeMethods.QueryInformationJobObject((Microsoft.Win32.SafeHandles.SafeFileHandle)job, NativeMethods.JobObjectExtendedLimitInformation, out var info,
            Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero);
        return (info.BasicLimitInformation.LimitFlags, (ulong)info.JobMemoryLimit);
    }

    [Fact]
    public void Caps_what_the_agent_started_but_never_the_agent_a_nested_agent_or_a_console()
    {
        using var ping = StartPing();
        using var nested = StartPing();
        using var console = StartPing();
        try
        {
            var agent = FakeAgent(999_321);
            using var caps = new SessionMemoryCaps();
            var poll = Poll(agent, Real(ping), Real(nested, "claude.exe"), Real(console, "conhost.exe"));

            Assert.Empty(caps.Apply(poll, enabled: true, capGb: 1, selfPid: Environment.ProcessId));
            Assert.Equal(1, caps.Capped[agent.Key]);
            var job = caps.JobOf(agent.Key)!;
            Assert.True(InJob(ping, job));
            Assert.False(InJob(nested, job));
            Assert.False(InJob(console, job));
            Assert.Equal((NativeMethods.JOB_OBJECT_LIMIT_JOB_MEMORY | NativeMethods.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK, 2UL << 30), Limits(job)); // 1 GB is raised to the 2 GB minimum

            Assert.Empty(caps.Apply(poll, true, 6, Environment.ProcessId));
            Assert.Equal(6UL << 30, Limits(job).Limit); // a new value applies to existing jobs
        }
        finally
        {
            foreach (var p in new[] { ping, nested, console }) p.Kill();
        }
    }

    [Theory]
    [InlineData("code.exe")]
    [InlineData("cursor.exe")]
    [InlineData("windsurf.exe")]
    [InlineData("devenv.exe")]
    [InlineData("rider64.exe")]
    [InlineData("idea64.exe")]
    [InlineData("WindowsTerminal.exe")]
    [InlineData("OpenConsole.exe")]
    [InlineData("conhost.exe")]
    [InlineData("explorer.exe")]
    [InlineData("claude.exe")]
    [InlineData("codex.exe")]
    public void Agents_ides_terminals_and_consoles_are_never_capped(string name) =>
        Assert.False(SessionMemoryCaps.Eligible(Proc(50, 10, name, "x")));

    [Fact]
    public void A_node_exe_waits_until_its_command_line_says_it_is_not_an_agent()
    {
        using var ping = StartPing();
        try
        {
            var agent = FakeAgent(999_326);
            using var caps = new SessionMemoryCaps();
            var unreadable = Real(ping, "node.exe") with { CommandLine = null };
            caps.Apply(Poll(agent, unreadable), true, 4, Environment.ProcessId);
            Assert.False(InJob(ping, caps.JobOf(agent.Key))); // might be an npm-installed agent: wait

            caps.Apply(Poll(agent, unreadable with { CommandLine = "node vite" }), true, 4, Environment.ProcessId);
            Assert.True(InJob(ping, caps.JobOf(agent.Key))); // now known to be a dev server

            Assert.False(SessionMemoryCaps.Eligible(unreadable with { CommandLine = @"node C:\npm\node_modules\@anthropic-ai\claude-code\cli.js" }));
        }
        finally
        {
            ping.Kill();
        }
    }

    [Fact]
    public void A_process_that_cannot_join_the_cap_is_reported_once_and_retried()
    {
        // Two processes already in two different jobs (Codex round 20): once the cap's job nests under the first
        // one's job, the second can't join it.
        using var first = StartPing();
        using var second = StartPing();
        using var jobA = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        using var jobB = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        try
        {
            foreach (var (p, j) in new[] { (first, jobA), (second, jobB) })
                using (var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_TERMINATE, false, (uint)p.Id))
                    Assert.True(NativeMethods.AssignProcessToJobObject(j, h));

            var agent = FakeAgent(999_327);
            using var caps = new SessionMemoryCaps();
            var events = caps.Apply(Poll(agent, Real(first), Real(second)), true, 4, Environment.ProcessId);
            Assert.Equal(1, caps.Capped[agent.Key]);
            var report = Assert.Single(events).Message;
            Assert.Contains("doesn't cover 1 process(es)", report);
            Assert.Contains($"pid {second.Id}", report);

            Assert.Empty(caps.Apply(Poll(agent, Real(first), Real(second)), true, 4, Environment.ProcessId)); // told once
            Assert.Empty(caps.Apply(Poll(agent, Real(first), Real(second)) is var r ? r with { Snapshot = r.Snapshot with { TakenAtUtc = r.Snapshot.TakenAtUtc.AddMinutes(2) } } : null!,
                true, 4, Environment.ProcessId)); // retried a minute later, still not repeated
        }
        finally
        {
            first.Kill();
            second.Kill();
        }
    }

    [Fact]
    public void A_leftover_still_gets_what_it_starts_after_its_agent_exits_added_to_the_cap()
    {
        using var shell = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul & ping -n 30 127.0.0.1 >nul")
            { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var agent = FakeAgent(999_328);
            using var caps = new SessionMemoryCaps();
            var shellInfo = Real(shell, "cmd.exe");
            caps.Apply(Poll(agent, shellInfo), true, 4, Environment.ProcessId);

            // The agent exits; a moment later the leftover shell starts its second ping (Codex round 21).
            Thread.Sleep(2500);
            var child = Collection.ProcessCollector.ReadIdentities()
                .Where(x => x.ParentPid == shell.Id && x.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Key.StartTicks).First();
            using var started = Process.GetProcessById(child.Key.Pid);
            var childInfo = Real(started);
            var tree = new ProcessNode(shellInfo, TimeSpan.Zero, [new ProcessNode(childInfo, TimeSpan.Zero, [])]);
            var leftover = new OrphanTree(tree, AgentKind.ClaudeCode, Attribution.Observed, "Claude Code (pid 999328)", agent.Pid, agent.Key.StartTicks);
            Assert.Empty(caps.Apply(new AnalysisResult(Snapshot(0, shellInfo, childInfo), [], [leftover], []), true, 4, Environment.ProcessId));

            Assert.True(InJob(started, caps.JobOf(agent.Key)));
        }
        finally
        {
            shell.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void A_process_that_exits_before_it_can_be_added_is_not_reported()
    {
        using var ping = StartPing(); // this Process object keeps a handle, so the exited process object lingers
        var info = Real(ping);
        ping.Kill();
        ping.WaitForExit();

        var agent = FakeAgent(999_329);
        using var caps = new SessionMemoryCaps();
        Assert.Empty(caps.Apply(Poll(agent, info), true, 4, Environment.ProcessId));
        Assert.Equal(0, caps.Capped[agent.Key]);
    }

    private static AnalysisResult WithLeftover(ProcessInfo? liveAgent, ProcessKey owner, ProcessInfo leftover)
    {
        var sessions = liveAgent is null ? new List<AgentSession>() : [new AgentSession(AgentKind.ClaudeCode, new ProcessNode(liveAgent, TimeSpan.Zero, []))];
        var orphan = new OrphanTree(new ProcessNode(leftover, TimeSpan.Zero, []), AgentKind.ClaudeCode, Attribution.Observed, "Claude Code", owner.Pid, owner.StartTicks);
        var running = liveAgent is null ? new[] { leftover } : [liveAgent, leftover];
        return new AnalysisResult(Snapshot(0, running), sessions, [orphan], []);
    }

    [Fact]
    public void A_leftover_of_a_still_running_agent_is_capped_too()
    {
        // Its launcher exited, so the server is a leftover while the agent keeps running (Codex round 22).
        using var server = StartPing();
        try
        {
            var agent = FakeAgent(999_330);
            using var caps = new SessionMemoryCaps();
            caps.Apply(WithLeftover(agent, agent.Key, Real(server)), true, 4, Environment.ProcessId);
            Assert.True(InJob(server, caps.JobOf(agent.Key)));
        }
        finally
        {
            server.Kill();
        }
    }

    [Fact]
    public void The_cap_is_kept_for_a_leftover_still_waiting_to_join_after_the_agent_and_job_are_empty()
    {
        using var node = StartPing();
        try
        {
            var agent = FakeAgent(999_331);
            using var caps = new SessionMemoryCaps();
            caps.Apply(Poll(agent), true, 4, Environment.ProcessId); // the session is capped; nothing in its job yet

            // The agent exits; its leftover node.exe can't be read yet, so it waits outside the (empty) job.
            var unreadable = Real(node, "node.exe") with { CommandLine = null };
            caps.Apply(WithLeftover(null, agent.Key, unreadable), true, 4, Environment.ProcessId);
            Assert.False(InJob(node, caps.JobOf(agent.Key)));
            Assert.True(caps.Capped.ContainsKey(agent.Key)); // kept: a leftover still refers to this agent

            caps.Apply(WithLeftover(null, agent.Key, unreadable with { CommandLine = "node server.js" }), true, 4, Environment.ProcessId);
            Assert.True(InJob(node, caps.JobOf(agent.Key)));
        }
        finally
        {
            node.Kill();
        }
    }

    [Fact]
    public void Turning_caps_off_lifts_them_at_once_without_a_poll_and_a_late_poll_cant_put_them_back()
    {
        // Monitoring paused, then caps turned off (Codex round 23): no poll runs, so LiftAll must do it.
        using var ping = StartPing();
        try
        {
            var agent = FakeAgent(999_332);
            using var caps = new SessionMemoryCaps();
            var enabled = true;
            caps.Apply(Poll(agent, Real(ping)), () => (enabled, 4), Environment.ProcessId);
            Assert.Equal(1, caps.Capped[agent.Key]);

            enabled = false;
            Assert.Equal(1, caps.LiftAll());
            Assert.Empty(caps.Capped);
            Assert.Equal(0, caps.LiftAll());

            // A poll that started before the change reads the setting under the same lock, so it sees "off".
            caps.Apply(Poll(agent, Real(ping)), () => (enabled, 4), Environment.ProcessId);
            Assert.Empty(caps.Capped);
        }
        finally
        {
            ping.Kill();
        }
    }

    [Fact]
    public void Nothing_joins_the_job_by_inheritance_so_a_later_agent_child_is_never_capped()
    {
        // A shell that starts a second program a moment after it is capped (Codex round 18).
        using var shell = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul & ping -n 30 127.0.0.1 >nul")
            { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var agent = FakeAgent(999_324);
            using var caps = new SessionMemoryCaps();
            caps.Apply(Poll(agent, Real(shell, "cmd.exe")), true, 4, Environment.ProcessId);
            Assert.True(InJob(shell, caps.JobOf(agent.Key)));

            Thread.Sleep(2500); // the first ping (about 1 s) has finished; the second has started since capping
            var child = Collection.ProcessCollector.ReadIdentities()
                .Where(x => x.ParentPid == shell.Id && x.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Key.StartTicks).First();
            using var started = Process.GetProcessById(child.Key.Pid);
            Assert.False(InJob(started, caps.JobOf(agent.Key)));
        }
        finally
        {
            shell.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void A_job_is_kept_while_windows_says_something_is_still_in_it_even_if_untracked()
    {
        using var tracked = StartPing();
        using var untracked = StartPing();
        try
        {
            var agent = FakeAgent(999_325);
            using var caps = new SessionMemoryCaps();
            caps.Apply(Poll(agent, Real(tracked)), true, 4, Environment.ProcessId);
            var job = caps.JobOf(agent.Key)!;
            using (var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_TERMINATE, false, (uint)untracked.Id))
                Assert.True(NativeMethods.AssignProcessToJobObject(job, handle));

            tracked.Kill();
            tracked.WaitForExit();
            caps.Apply(Poll(null, Real(untracked)), true, 4, Environment.ProcessId); // agent and tracked member gone
            Assert.False(job.IsClosed);
            Assert.Contains("lifted", Assert.Single(caps.Apply(Poll(null, Real(untracked)), false, 4, Environment.ProcessId)).Message);
            Assert.Empty(caps.Capped);
        }
        finally
        {
            foreach (var p in new[] { tracked, untracked }) if (!p.HasExited) p.Kill();
        }
    }

    [Fact]
    public void A_leftover_keeps_its_job_after_the_agent_exits_so_turning_off_still_lifts_the_cap()
    {
        using var ping = StartPing();
        try
        {
            var agent = FakeAgent(999_322);
            using var caps = new SessionMemoryCaps();
            caps.Apply(Poll(agent, Real(ping)), true, 4, Environment.ProcessId);
            var job = caps.JobOf(agent.Key)!;

            // The agent exits; ping is now a leftover, still in the job.
            caps.Apply(Poll(null, Real(ping)), true, 4, Environment.ProcessId);
            Assert.False(job.IsClosed);
            Assert.Equal(1, caps.Capped[agent.Key]);

            var off = caps.Apply(Poll(null, Real(ping)), false, 4, Environment.ProcessId);
            Assert.Contains("lifted from 1", Assert.Single(off).Message);
            Assert.Empty(caps.Capped);

            // Once everything in it has exited (and the agent too), the job is let go.
            caps.Apply(Poll(agent, Real(ping)), true, 4, Environment.ProcessId);
            var second = caps.JobOf(agent.Key)!;
            caps.Apply(Poll(null, Real(ping)), true, 4, Environment.ProcessId);
            Assert.False(second.IsClosed); // ping is still in it
            ping.Kill();
            ping.WaitForExit();
            caps.Apply(Poll(null), true, 4, Environment.ProcessId);
            Assert.True(second.IsClosed);
            Assert.Empty(caps.Capped);
        }
        finally
        {
            ping.Kill();
        }
    }
}

public class MemoryHistoryTests
{
    private static AnalysisResult Result() => Scenario.Build(out _);

    [Fact]
    public void One_sample_a_minute_per_project_kept_for_a_day_and_saved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-history-{Guid.NewGuid():N}.json");
        try
        {
            var history = new MemoryHistory(path);
            var result = Result();
            var projects = Insights.Projects(result, Scenario.ProjectOf);
            var t0 = DateTime.UtcNow.AddHours(-30);

            Assert.True(history.Add(t0, result, projects));
            Assert.False(history.Add(t0.AddSeconds(30), result, projects)); // under a minute
            Assert.True(history.Add(t0.AddMinutes(1), result, projects));
            Assert.True(history.Add(DateTime.UtcNow, result, projects)); // 30 h later: the old ones fall out
            Assert.Single(history.Samples);

            var sample = history.Samples[0];
            Assert.Equal(result.AgentPrivateBytes, sample.AiBytes);
            Assert.Equal(860L << 20, sample.Projects[@"C:\src\app"]);
            Assert.Equal(200L << 20, sample.Projects[@"C:\src\old"]);
            Assert.Equal([@"C:\src\app", @"C:\src\old"], history.TopProjects(5));

            history.Save();
            Assert.Single(new MemoryHistory(path).Samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Freed_counts_successful_stops_by_who_asked()
    {
        var now = DateTime.UtcNow;
        ActionLogEntry E(string trigger, long bytes, bool ok = true, string phase = "done", string action = "stop-tree", int hoursAgo = 1) =>
            new(now.AddHours(-hoursAgo), action, phase, trigger, ok, "", 1, "x", null, null, null, 1, bytes, "", "");

        var freed = MemoryHistory.Freed([
            E("manual", 100), E("manual: free port 3000", 50), E("auto: idle leftover", 20), E("mcp: Claude Code (pid 1)", 5),
            E("manual: stop container", 7, action: "docker-stop"),
            E("manual", 1000, ok: false), E("manual", 1000, phase: "intent"), E("manual", 1000, hoursAgo: 30), E("manual", 1000, action: "wsl-shutdown"),
        ], now.AddHours(-24));

        Assert.Equal(new FreedSummary(182, 5, 157, 20, 5), freed);
    }
}
