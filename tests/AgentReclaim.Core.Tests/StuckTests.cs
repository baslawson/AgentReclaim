using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

public class StuckTests
{
    private static Analyzer NewAnalyzer() => new(new LineageStore(), new AnalyzerOptions
    {
        CoreCount = 4, // a whole core = 25% of the machine
        SpinningGap = TimeSpan.FromMinutes(2),
    });

    /// <summary>Polls once a minute from <paramref name="from"/> to <paramref name="to"/>; the processes for each minute come from <paramref name="at"/>.</summary>
    private static AnalysisResult Run(Analyzer analyzer, int from, int to, Func<int, ProcessInfo[]> at)
    {
        AnalysisResult? last = null;
        for (var minute = from; minute <= to; minute++) last = analyzer.Analyze(Snapshot(minute, at(minute)));
        return last!;
    }

    [Fact]
    public void A_command_with_no_activity_for_ten_minutes_may_be_stuck()
    {
        var analyzer = NewAnalyzer();
        ProcessInfo[] Tree(int _) =>
        [
            Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1),     // the agent itself keeps ticking over
            Proc(11, 10, "bash.exe", startMinute: 5),
            Proc(12, 11, "git.exe", "git push", startMinute: 5),  // waiting for a password prompt nobody sees
        ];

        Assert.Empty(Run(analyzer, 5, 14, Tree).Stuck); // 9 minutes: not yet
        var result = Run(analyzer, 15, 15, Tree);
        var stuck = Assert.Single(result.Stuck);
        Assert.Equal((11, StuckKind.Waiting), (stuck.Node.Process.Pid, stuck.Kind));
        Assert.Equal("Stuck? No activity for 10 min", stuck.Status);
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.Stuck && f.Title.Contains("bash.exe (pid 11)"));
    }

    [Fact]
    public void Activity_anywhere_in_the_command_counts()
    {
        var analyzer = NewAnalyzer();
        var result = Run(analyzer, 5, 20, minute =>
        [
            Proc(10, 1, "claude.exe", startMinute: 0),
            Proc(11, 10, "bash.exe", startMinute: 5),
            Proc(12, 11, "node.exe", "node build.js", startMinute: 5, cpu: minute % 3 == 0 ? 2 : 0), // working in bursts
        ]);
        Assert.Empty(result.Stuck);
    }

    [Fact]
    public void Standing_helpers_servers_the_agent_and_console_hosts_are_never_waiting()
    {
        var analyzer = NewAnalyzer();
        var result = Run(analyzer, 5, 40, _ =>
        [
            Proc(10, 1, "claude.exe", startMinute: 0),                                    // idle at its prompt: normal
            Proc(11, 10, "node.exe", "node mcp-server.js", startMinute: 1),               // started with the agent
            Proc(12, 10, "cmd.exe", startMinute: 5),
            Proc(13, 12, "node.exe", "node vite", startMinute: 5, ports: [5173]),          // a dev server, idle between requests
            Proc(14, 10, "conhost.exe", startMinute: 6),
        ]);
        Assert.Empty(result.Stuck);
    }

    [Fact]
    public void A_pile_of_idle_children_is_one_finding_not_one_stuck_each()
    {
        var analyzer = NewAnalyzer();
        var result = Run(analyzer, 40, 55, _ =>
            [Proc(10, 1, "codex.exe", startMinute: 0), .. Enumerable.Range(0, 6).Select(i => Proc(20 + i, 10, "node_repl.exe", startMinute: 5 + i))]);
        Assert.Empty(result.Stuck);
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.IdlePile);
    }

    [Fact]
    public void Nested_agents_tools_are_watched_too()
    {
        var analyzer = NewAnalyzer();
        var result = Run(analyzer, 5, 16, _ =>
        [
            Proc(10, 1, "claude.exe", startMinute: 0),
            Proc(20, 10, "codex.exe", startMinute: 1),
            Proc(21, 20, "python.exe", "python -c input()", startMinute: 4),
        ]);
        var stuck = Assert.Single(result.Stuck);
        Assert.Equal(21, stuck.Node.Process.Pid);
        Assert.Equal(10, stuck.Session.Root.Process.Pid);
    }

    [Fact]
    public void A_whole_core_busy_for_fifteen_minutes_may_be_a_loop_agents_included()
    {
        var analyzer = NewAnalyzer();
        ProcessInfo[] Tree(int _) =>
        [
            Proc(10, 1, "node.exe", @"node C:\npm\node_modules\@google\gemini-cli\dist\index.js", startMinute: 0, cpu: 24.9),
            Proc(11, 10, "python.exe", "python loop.py", startMinute: 1, cpu: 25),
        ];
        Assert.Empty(Run(analyzer, 1, 15, Tree).Stuck);
        var result = Run(analyzer, 16, 16, Tree);
        var stuck = result.Stuck.ToDictionary(s => s.Node.Process.Pid);
        Assert.Equal(StuckKind.Spinning, stuck[11].Kind);
        Assert.Equal(TimeSpan.FromMinutes(15), stuck[11].For);
        Assert.Equal(StuckKind.Spinning, stuck[10].Kind); // 24.9% of four cores is still 99.6% of one
        Assert.Equal("Stuck? Busy for 15 min", stuck[11].Status);
    }

    [Fact]
    public void A_short_dip_keeps_the_clock_and_a_long_one_restarts_it()
    {
        var analyzer = NewAnalyzer();
        ProcessInfo[] Busy(double cpu) => [Proc(10, 1, "claude.exe", startMinute: 0), Proc(11, 10, "cargo.exe", startMinute: 1, cpu: cpu)];

        Run(analyzer, 1, 8, _ => Busy(30));
        Run(analyzer, 9, 9, _ => Busy(5));                  // one quiet minute, within the 2-minute gap
        Assert.Single(Run(analyzer, 10, 16, _ => Busy(30)).Stuck);

        var restarted = NewAnalyzer();
        Run(restarted, 1, 8, _ => Busy(30));
        Run(restarted, 9, 12, _ => Busy(5));               // four quiet minutes: it let up
        Assert.Empty(Run(restarted, 13, 26, _ => Busy(30)).Stuck);
    }

    [Fact]
    public void Turned_off_means_nothing_is_reported()
    {
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions { StuckWaitingAfter = null, StuckSpinningAfter = null, CoreCount = 4 });
        var result = Run(analyzer, 5, 40, _ =>
            [Proc(10, 1, "claude.exe", startMinute: 0), Proc(11, 10, "bash.exe", startMinute: 5, cpu: 30)]);
        Assert.Empty(result.Stuck);
    }

    [Fact]
    public void Each_stuck_process_is_announced_once_and_only_when_asked()
    {
        var watch = new StuckWatch();
        var analyzer = NewAnalyzer();
        ProcessInfo[] Waiting(int _) => [Proc(10, 1, "claude.exe", startMinute: 0), Proc(11, 10, "bash.exe", startMinute: 5)];

        Assert.Null(watch.Evaluate(Run(analyzer, 5, 15, Waiting), enabled: false)); // seen while off: not announced later
        Assert.Null(watch.Evaluate(Run(analyzer, 16, 16, Waiting), enabled: true));

        var other = new StuckWatch();
        var notice = other.Evaluate(Run(NewAnalyzer(), 5, 15, Waiting), enabled: true);
        Assert.NotNull(notice);
        Assert.Contains("bash.exe under Claude Code has done nothing for 10 min", notice.Text);
        Assert.Null(other.Evaluate(Run(analyzer, 17, 17, Waiting), enabled: true));
    }

    [Fact]
    public void A_busy_child_that_exits_restarts_the_clock()
    {
        // The shell itself has been idle since minute 5, but its build worked until minute 30 and then exited:
        // the shell is waiting from minute 30, not minute 5.
        var analyzer = NewAnalyzer();
        ProcessInfo[] Tree(int minute) => minute < 30
            ?
            [
                Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1),
                Proc(11, 10, "bash.exe", startMinute: 5),
                Proc(12, 11, "cargo.exe", "cargo build", startMinute: 5, cpu: 20),
            ]
            : [Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1), Proc(11, 10, "bash.exe", startMinute: 5)];

        Assert.Empty(Run(analyzer, 5, 39, Tree).Stuck); // 9 min since the build ended: not yet
        var stuck = Assert.Single(Run(analyzer, 40, 40, Tree).Stuck);
        Assert.Equal((11, 10), (stuck.Node.Process.Pid, (int)stuck.For.TotalMinutes));
    }

    [Fact]
    public void A_server_child_exiting_restarts_the_clock_too()
    {
        // While its child listens the shell isn't reported, but its activity is still followed.
        var analyzer = NewAnalyzer();
        ProcessInfo[] Tree(int minute) => minute < 30
            ?
            [
                Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1),
                Proc(11, 10, "bash.exe", startMinute: 5),
                Proc(12, 11, "node.exe", "node server.js", startMinute: 5, cpu: 20, ports: [3000]),
            ]
            : [Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1), Proc(11, 10, "bash.exe", startMinute: 5)];

        Assert.Empty(Run(analyzer, 5, 39, Tree).Stuck);
        Assert.Single(Run(analyzer, 40, 40, Tree).Stuck);
    }

    [Fact]
    public void Activity_is_followed_while_the_setting_is_off()
    {
        ProcessInfo[] Tree(int minute) => minute < 30
            ?
            [
                Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1),
                Proc(11, 10, "bash.exe", startMinute: 5),
                Proc(12, 11, "cargo.exe", "cargo build", startMinute: 5, cpu: 20),
            ]
            : [Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1), Proc(11, 10, "bash.exe", startMinute: 5)];
        var options = new AnalyzerOptions { CoreCount = 4, SpinningGap = TimeSpan.FromMinutes(2), StuckWaitingAfter = null };
        var analyzer = new Analyzer(new LineageStore(), options);
        Run(analyzer, 5, 30, Tree);

        analyzer.Options = options with { StuckWaitingAfter = TimeSpan.FromMinutes(10) }; // turned on just after the build ended
        Assert.Empty(Run(analyzer, 31, 39, Tree).Stuck);
        Assert.Single(Run(analyzer, 40, 40, Tree).Stuck);
    }
}

public class AutoStopStuckTests
{
    private static AnalysisResult Stuck(params ProcessInfo[] tools)
    {
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions { CoreCount = 4, SpinningGap = TimeSpan.FromMinutes(2) });
        AnalysisResult? last = null;
        for (var minute = 5; minute <= 20; minute++)
            last = analyzer.Analyze(Snapshot(minute, [Proc(10, 1, "claude.exe", startMinute: 0), .. tools]));
        return last!;
    }

    private static readonly ProcessInfo Waiting = Proc(11, 10, "bash.exe", "bash -c 'git push'", startMinute: 5);
    private static readonly ProcessInfo Looping = Proc(12, 10, "python.exe", "python loop.py", startMinute: 4, cpu: 30);

    [Fact]
    public void Nothing_is_stopped_unless_turned_on_and_each_kind_has_its_own_switch()
    {
        var result = Stuck(Waiting, Looping);
        Assert.Equal(2, result.Stuck.Count);
        var engine = new AutoRuleEngine();
        Assert.Empty(engine.StuckToStop(result, new AppSettings(), selfPid: 1));

        var waitingOnly = engine.StuckToStop(result, new AppSettings { AutoStopStuckWaiting = true }, selfPid: 1);
        Assert.Equal(11, Assert.Single(waitingOnly).Node.Process.Pid);
        var spinningOnly = engine.StuckToStop(result, new AppSettings { AutoStopStuckSpinning = true }, selfPid: 1);
        Assert.Equal(12, Assert.Single(spinningOnly).Node.Process.Pid);
    }

    [Fact]
    public void Each_is_tried_only_once()
    {
        var result = Stuck(Waiting);
        var engine = new AutoRuleEngine();
        var settings = new AppSettings { AutoStopStuckWaiting = true };
        engine.RecordStopAttempt(Assert.Single(engine.StuckToStop(result, settings, 1)));
        Assert.Empty(engine.StuckToStop(result, settings, 1));
    }

    [Fact]
    public void Never_an_agent_a_possible_agent_a_connection_or_without_connection_data()
    {
        var settings = new AppSettings { AutoStopStuckWaiting = true, AutoStopStuckSpinning = true };
        var engine = new AutoRuleEngine();

        // The agent itself spinning: reported, never stopped.
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions { CoreCount = 4 });
        AnalysisResult? spinningAgent = null;
        for (var second = 0; second <= 16 * 60; second += 20)
            spinningAgent = analyzer.Analyze(new Model.ProcessSnapshot(T0.AddSeconds(second), [Proc(10, 1, "claude.exe", startMinute: 0, cpu: 30)],
                new Model.SystemStats(16L << 30, 8L << 30, 10)));
        Assert.Single(spinningAgent!.Stuck);
        Assert.Empty(engine.StuckToStop(spinningAgent, settings, 1));

        // A node.exe whose command line couldn't be read might be an agent.
        Assert.Empty(engine.StuckToStop(Stuck(Proc(11, 10, "node.exe", null, startMinute: 5)), settings, 1));
        // Something with an open connection (waiting on the network, or in use).
        Assert.Empty(engine.StuckToStop(Stuck(Proc(11, 10, "bash.exe", startMinute: 5), Proc(13, 11, "curl.exe", startMinute: 5, connections: 1)), settings, 1));
        // AgentReclaim itself.
        Assert.Empty(engine.StuckToStop(Stuck(Waiting), settings, selfPid: 11));
        // A nested agent below the tool whose command line couldn't be read, or one launched with an agent path.
        Assert.Empty(engine.StuckToStop(Stuck(Proc(11, 10, "bash.exe", startMinute: 5), Proc(13, 11, "node.exe", null, startMinute: 5)), settings, 1));
        Assert.Empty(engine.StuckToStop(Stuck(Proc(11, 10, "bash.exe", startMinute: 5),
            Proc(13, 11, "node.exe", @"node C:\x\run.js C:\npm\node_modules\@google\gemini-cli\dist\index.js", startMinute: 5)), settings, 1));

        var blind = Stuck(Waiting);
        blind = blind with { Snapshot = blind.Snapshot with { NetworkComplete = false } };
        Assert.Empty(engine.StuckToStop(blind, settings, 1));
    }
}

/// <summary>UDP has no "listening" state, so a UDP server looked like an idle command with no ports.</summary>
public class UdpServerTests
{
    [Fact]
    public void A_stuck_udp_server_is_neither_waiting_nor_auto_stopped()
    {
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions { CoreCount = 4, SpinningGap = TimeSpan.FromMinutes(2) });
        AnalysisResult? last = null;
        for (var minute = 5; minute <= 25; minute++)
            last = analyzer.Analyze(Snapshot(minute,
                Proc(10, 1, "claude.exe", startMinute: 0),
                Proc(11, 10, "node.exe", "node dns-server.js", startMinute: 5) with { UdpPorts = [5353] },  // idle between queries
                Proc(12, 10, "python.exe", "python syslog.py", startMinute: 4, cpu: 30) with { UdpPorts = [514] })); // busy
        Assert.DoesNotContain(last!.Stuck, s => s.Kind == StuckKind.Waiting);

        var settings = new AppSettings { AutoStopStuckWaiting = true, AutoStopStuckSpinning = true };
        Assert.Empty(new AutoRuleEngine().StuckToStop(last, settings, 1));
    }

    [Fact]
    public void The_last_check_before_a_stuck_stop_refuses_a_server_or_unreadable_ports()
    {
        var root = Proc(999_101, 1, "python.exe");
        var node = new ProcessNode(root, TimeSpan.FromHours(1), []);
        var log = new ActionLog(Path.Combine(Path.GetTempPath(), $"agentreclaim-{Guid.NewGuid():N}", "actions.jsonl"));
        ProcessTerminator With(int? listeners) =>
            new(log, () => [(root.Key, root.ParentPid, root.Name)], _ => 0, _ => listeners);

        var server = With(1).StopSubtree(node, "Claude Code", "live session", "test", requireNoConnections: true, requireNoListeners: true);
        Assert.False(server.Success);
        Assert.Contains("may be a server", server.Message);
        var unknown = With(null).StopSubtree(node, "Claude Code", "live session", "test", requireNoConnections: true, requireNoListeners: true);
        Assert.False(unknown.Success);
        Assert.Contains("couldn't read the ports", unknown.Message);
    }

    [Fact]
    public void A_bound_udp_socket_is_read_from_the_real_table()
    {
        using var v4 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)v4.Client.LocalEndPoint!).Port;
        using var v6 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, 0));
        var port6 = ((System.Net.IPEndPoint)v6.Client.LocalEndPoint!).Port;
        var table = Collection.TcpTable.Read();
        Assert.True(table.Complete);
        Assert.Contains(port, table.UdpPorts[Environment.ProcessId]);
        Assert.Contains(port6, table.UdpPorts[Environment.ProcessId]); // the IPv6 table's row layout
        Assert.True(Collection.TcpTable.ListenersFor(new HashSet<int> { Environment.ProcessId }) >= 1);
    }
}

/// <summary>CPU is a share of the whole machine: with hundreds of cores, a whole busy core is under 0.5% of it.</summary>
public class ManyCoreTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(192)]
    [InlineData(256)]
    public void A_process_keeping_part_of_one_core_busy_is_never_waiting(int cores)
    {
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions { CoreCount = cores });
        AnalysisResult? last = null;
        for (var minute = 5; minute <= 30; minute++)
            last = analyzer.Analyze(Snapshot(minute,
                Proc(10, 1, "claude.exe", startMinute: 0),
                Proc(11, 10, "bash.exe", startMinute: 5),
                Proc(12, 11, "python.exe", "python train.py", startMinute: 5, cpu: 95.0 / cores),   // 95% of one core
                Proc(13, 10, "cmd.exe", startMinute: 5),
                Proc(14, 13, "node.exe", "node build.js", startMinute: 5, cpu: 10.0 / cores)));     // 10% of one core

        Assert.DoesNotContain(last!.Stuck, s => s.Kind == StuckKind.Waiting);
        Assert.Empty(new AutoRuleEngine().StuckToStop(last, new AppSettings { AutoStopStuckWaiting = true }, 1));
    }
}

/// <summary>Turning a rule off while a poll is under way must stop what that poll planned.</summary>
public class AutoStopOptOutTests
{
    [Fact]
    public void A_stop_is_refused_when_the_rule_was_turned_off_after_it_was_planned()
    {
        using var ping = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping", "-n 30 127.0.0.1")
            { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var info = new ProcessInfo(new ProcessKey(ping.Id, Native.NativeProcess.GetStartTicks(ping.Id)!.Value), 1, "PING.EXE", null,
                "ping -n 30 127.0.0.1", DateTime.UtcNow, 1, 1, 0, [], 0);
            var log = new ActionLog(Path.Combine(Path.GetTempPath(), $"agentreclaim-{Guid.NewGuid():N}", "actions.jsonl"));
            var asked = 0;
            var result = new ProcessTerminator(log, () => [(info.Key, info.ParentPid, info.Name)], _ => 0, _ => 0).StopSubtree(
                new ProcessNode(info, TimeSpan.FromHours(1), []), "Claude Code", "live session", "auto: stuck process",
                requireNoConnections: true, requireNoListeners: true, stillWanted: () => { asked++; return false; });

            Assert.False(result.Success);
            Assert.Contains("turned off", result.Message);
            Assert.Equal(1, asked);
            Assert.False(ping.HasExited);
        }
        finally
        {
            ping.Kill();
        }
    }

    [Fact]
    public void Still_wanted_follows_the_current_switch_and_time_for_its_kind()
    {
        var session = new AgentSession(AgentKind.ClaudeCode, new ProcessNode(Proc(10, 1, "claude.exe"), TimeSpan.Zero, []));
        var node = new ProcessNode(Proc(11, 10, "bash.exe"), TimeSpan.Zero, []);
        var waiting = new StuckProcess(session, node, StuckKind.Waiting, TimeSpan.FromMinutes(12));
        var spinning = new StuckProcess(session, node, StuckKind.Spinning, TimeSpan.FromMinutes(16));

        Assert.True(AutoRuleEngine.StillWanted(waiting, new AppSettings { AutoStopStuckWaiting = true }));
        Assert.False(AutoRuleEngine.StillWanted(waiting, new AppSettings { AutoStopStuckWaiting = false, AutoStopStuckSpinning = true }));
        Assert.False(AutoRuleEngine.StillWanted(waiting, new AppSettings { AutoStopStuckWaiting = true, StuckWaitingMinutes = 30 }));
        Assert.True(AutoRuleEngine.StillWanted(spinning, new AppSettings { AutoStopStuckSpinning = true }));
        Assert.False(AutoRuleEngine.StillWanted(spinning, new AppSettings { AutoStopStuckWaiting = true }));
    }
}
