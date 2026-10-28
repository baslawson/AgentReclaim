using AgentReclaim.Core.Analysis;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

public class AnalyzerTests
{
    private static Analyzer NewAnalyzer() => new(new LineageStore());

    [Fact]
    public void Groups_children_under_the_live_agent()
    {
        var result = NewAnalyzer().Analyze(Snapshot(5,
            Proc(1, 0, "explorer.exe"),
            Proc(10, 1, "claude.exe", startMinute: 1),
            Proc(11, 10, "bash.exe", startMinute: 2),
            Proc(12, 11, "node.exe", "node vite", startMinute: 3, mb: 300)));

        var session = Assert.Single(result.Sessions);
        Assert.Equal(AgentKind.ClaudeCode, session.Kind);
        Assert.Equal(3, session.Root.TotalCount);
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public void Nested_agent_belongs_to_the_outer_session()
    {
        var result = NewAnalyzer().Analyze(Snapshot(5,
            Proc(10, 1, "claude.exe"),
            Proc(20, 10, "codex.exe", startMinute: 1),
            Proc(21, 20, "node.exe", startMinute: 2)));

        var session = Assert.Single(result.Sessions);
        Assert.Equal(10, session.Root.Process.Pid);
        Assert.Equal(3, session.Root.TotalCount);
    }

    [Fact]
    public void Children_left_behind_by_an_exited_agent_become_observed_orphans()
    {
        var analyzer = NewAnalyzer();
        var bash = Proc(11, 10, "bash.exe", startMinute: 2);
        var vite = Proc(12, 11, "node.exe", "node vite", startMinute: 3, mb: 300);
        analyzer.Analyze(Snapshot(5, Proc(10, 1, "claude.exe", startMinute: 1), bash, vite));

        // claude.exe exits; bash and vite keep running.
        var result = analyzer.Analyze(Snapshot(10, bash, vite));

        var orphan = Assert.Single(result.Orphans);
        Assert.Equal(11, orphan.Root.Process.Pid);
        Assert.Equal(2, orphan.Root.TotalCount);
        Assert.Equal(Attribution.Observed, orphan.Attribution);
        Assert.Contains("pid 10", orphan.FormerOwner);
    }

    [Fact]
    public void Reused_parent_pid_does_not_count_as_a_live_parent()
    {
        var analyzer = NewAnalyzer();
        var child = Proc(12, 10, "node.exe", startMinute: 3);
        analyzer.Analyze(Snapshot(5, Proc(10, 1, "claude.exe", startMinute: 1), child));

        // pid 10 now belongs to an unrelated process that started after the child.
        var result = analyzer.Analyze(Snapshot(10, Proc(10, 1, "notepad.exe", startMinute: 8), child));

        Assert.Equal(12, Assert.Single(result.Orphans).Root.Process.Pid);
    }

    [Fact]
    public void Unseen_orphan_is_attributed_from_its_path()
    {
        var result = NewAnalyzer().Analyze(Snapshot(5,
            Proc(30, 999, "node.exe", @"node C:\Users\me\.claude\plugins\cache\x\server.mjs")));

        var orphan = Assert.Single(result.Orphans);
        Assert.Equal(Attribution.Heuristic, orphan.Attribution);
        Assert.Equal(AgentKind.ClaudeCode, orphan.Kind);
    }

    [Fact]
    public void Unrelated_processes_are_never_orphans()
    {
        var result = NewAnalyzer().Analyze(Snapshot(5,
            Proc(1, 0, "explorer.exe"),
            Proc(40, 999, "node.exe", @"node C:\work\shiftready\node_modules\next\dist\bin\next dev"),
            Proc(41, 1, "node.exe", @"node C:\Users\me\.codex\thing.js")));

        // 40 has no agent markers; 41 has a live parent, so it isn't a leftover.
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public void Detached_agent_daemon_is_a_session_not_an_orphan()
    {
        var result = NewAnalyzer().Analyze(Snapshot(5,
            Proc(50, 999, "codex.exe", @"C:\Users\me\.codex\bin\codex.exe app-server")));

        Assert.Single(result.Sessions);
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public void Parentless_broker_hosting_a_live_agent_is_not_an_orphan()
    {
        var result = NewAnalyzer().Analyze(Snapshot(5,
            Proc(60, 999, "node.exe", @"node C:\Users\me\.claude\plugins\cache\openai-codex\app-server-broker.mjs"),
            Proc(61, 60, "codex.exe", startMinute: 1),
            Proc(62, 61, "node_repl.exe", startMinute: 2)));

        Assert.Empty(result.Orphans);
        Assert.Equal(61, Assert.Single(result.Sessions).Root.Process.Pid);
    }

    [Fact]
    public void Never_reports_itself_as_a_leftover()
    {
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions { SelfPid = 12 });
        var self = Proc(12, 11, "AgentReclaim.exe", startMinute: 3);
        analyzer.Analyze(Snapshot(5, Proc(10, 1, "claude.exe"), Proc(11, 10, "pwsh.exe", startMinute: 2), self));

        Assert.Empty(analyzer.Analyze(Snapshot(10, self)).Orphans);
    }

    [Fact]
    public void Pile_of_old_idle_children_raises_a_finding()
    {
        var processes = new List<Core.Model.ProcessInfo> { Proc(10, 1, "codex.exe") };
        for (var i = 0; i < 6; i++) processes.Add(Proc(100 + i, 10, "node_repl.exe", startMinute: 1, mb: 15));
        processes.Add(Proc(200, 10, "node_repl.exe", startMinute: 55)); // too young

        var result = NewAnalyzer().Analyze(Snapshot(60, processes.ToArray()));

        var finding = Assert.Single(result.Findings);
        Assert.StartsWith("6 idle node_repl.exe", finding.Title);
        Assert.Equal(6 * 15L * 1024 * 1024, finding.Bytes);
    }

    [Fact]
    public void Lineage_survives_a_restart_of_the_app()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentreclaim-test-{Guid.NewGuid():N}.json");
        try
        {
            var child = Proc(12, 10, "node.exe", startMinute: 3);
            new Analyzer(new LineageStore(path)).Analyze(Snapshot(5, Proc(10, 1, "claude.exe"), child));

            var result = new Analyzer(new LineageStore(path)).Analyze(Snapshot(10, child));

            Assert.Equal(Attribution.Observed, Assert.Single(result.Orphans).Attribution);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
