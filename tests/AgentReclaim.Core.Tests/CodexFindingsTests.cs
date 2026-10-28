using System.Diagnostics;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Collection;
using AgentReclaim.Core.Model;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

/// <summary>Regression tests for the issues raised in the Codex adversarial review.</summary>
public class CodexFindingsTests
{
    private static OrphanTree TreeOf(params ProcessInfo[] chain)
    {
        ProcessNode? node = null;
        foreach (var p in chain.Reverse()) node = new ProcessNode(p, TimeSpan.FromHours(1), node is null ? [] : [node]);
        return new OrphanTree(node!, AgentKind.ClaudeCode, Attribution.Observed, "claude.exe (pid 10)", 10);
    }

    private static string TempLog() => Path.Combine(Path.GetTempPath(), $"agentreclaim-{Guid.NewGuid():N}", "actions.jsonl");

    [Fact]
    public void Stop_is_refused_when_the_tree_gained_a_process_after_approval()
    {
        var root = Proc(999_001, 1, "node.exe");
        var lateChild = Proc(999_002, 999_001, "codex.exe", startMinute: 5);
        var terminator = new ProcessTerminator(new ActionLog(TempLog()), () => [
            (root.Key, root.ParentPid, root.Name),
            (lateChild.Key, lateChild.ParentPid, lateChild.Name),
        ]);

        var result = terminator.StopTree(TreeOf(root), "manual");

        Assert.False(result.Success);
        Assert.Contains("codex.exe", result.Message);
    }

    [Fact]
    public void Stop_is_refused_when_the_intent_cannot_be_logged()
    {
        var logPath = TempLog();
        Directory.CreateDirectory(logPath); // a directory where the log file should be: every write fails
        var root = Proc(999_003, 1, "node.exe");
        var terminator = new ProcessTerminator(new ActionLog(logPath), () => [(root.Key, root.ParentPid, root.Name)]);

        var result = terminator.StopTree(TreeOf(root), "manual");

        Assert.False(result.Success);
        Assert.Contains("action log", result.Message);
    }

    [Fact]
    public void Stops_a_real_process_and_logs_intent_then_done()
    {
        using var ping = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Thread.Sleep(300); // let Windows attach its conhost.exe child
            var snapshot = new ProcessCollector().Collect();
            var info = snapshot.Processes.Single(p => p.Pid == ping.Id);
            var children = snapshot.Processes
                .Where(p => p.ParentPid == ping.Id && p.StartTimeUtc >= info.StartTimeUtc)
                .Select(p => new ProcessNode(p, TimeSpan.FromHours(1), []))
                .ToList();
            var tree = new OrphanTree(new ProcessNode(info, TimeSpan.FromHours(1), children),
                AgentKind.ClaudeCode, Attribution.Observed, "test", null);
            var log = new ActionLog(TempLog());

            var result = new ProcessTerminator(log) { GracePeriod = TimeSpan.FromSeconds(1) }.StopTree(tree, "test");

            Assert.True(result.Success, result.Message);
            Assert.True(ping.WaitForExit(5000));
            var entries = log.ReadRecent(10);
            Assert.Equal(["done", "intent"], entries.Select(e => e.Phase));
            Assert.True(entries[0].CanRelaunch);
        }
        finally
        {
            if (!ping.HasExited) ping.Kill();
        }
    }

    [Fact]
    public void Inherited_ownership_survives_the_orphan_parent_exiting()
    {
        var analyzer = new Analyzer(new LineageStore());
        var shell = Proc(11, 10, "bash.exe", startMinute: 1);
        analyzer.Analyze(Snapshot(2, Proc(10, 1, "claude.exe"), shell));

        // Agent exits; the surviving shell starts a server we never saw under the agent.
        var server = Proc(12, 11, "node.exe", "node server.js", startMinute: 5);
        analyzer.Analyze(Snapshot(6, shell, server));

        // The shell exits too. The server is still a leftover of that Claude session.
        var orphan = Assert.Single(analyzer.Analyze(Snapshot(8, server)).Orphans);
        Assert.Equal(12, orphan.Root.Process.Pid);
        Assert.Equal(Attribution.Observed, orphan.Attribution);
        Assert.Equal(10, orphan.OwnerPid);
    }

    [Fact]
    public void Empty_snapshot_does_not_erase_lineage()
    {
        var analyzer = new Analyzer(new LineageStore());
        var child = Proc(12, 10, "node.exe", startMinute: 3);
        analyzer.Analyze(Snapshot(5, Proc(10, 1, "claude.exe"), child));

        analyzer.Analyze(Snapshot(6)); // collection glitch
        var result = analyzer.Analyze(Snapshot(7, child));

        Assert.Equal(Attribution.Observed, Assert.Single(result.Orphans).Attribution);
    }
}
