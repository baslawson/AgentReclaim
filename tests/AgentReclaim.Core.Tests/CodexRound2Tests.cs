using System.Text.Json.Nodes;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Cleanup;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

/// <summary>Regression tests for the second Codex adversarial review.</summary>
public class CodexRound2Tests
{
    private static readonly AppSettings AutoOn = new() { AutoStopLeftovers = true, AutoStopIdleMinutes = 15, HookGraceSeconds = 60 };

    private static AnalysisResult IdleLeftover(int idleMinutes, bool networkComplete = true)
    {
        var analyzer = new Analyzer(new LineageStore());
        var server = Proc(12, 10, "node.exe", startMinute: 1);
        analyzer.Analyze(Snapshot(2, Proc(10, 1, "claude.exe"), server));
        analyzer.Analyze(Snapshot(3, server));
        var snapshot = Snapshot(3 + idleMinutes, server) with { NetworkComplete = networkComplete };
        return analyzer.Analyze(snapshot);
    }

    [Fact]
    public void No_auto_stop_when_connection_data_is_missing()
    {
        Assert.Empty(new AutoRuleEngine().LeftoversToStop(IdleLeftover(60, networkComplete: false), AutoOn, 1));
    }

    [Fact]
    public void Hook_grace_counts_from_the_session_end_not_from_idleness()
    {
        var result = IdleLeftover(5); // idle 5 min: past the 60 s grace, but under the 15 min threshold
        var now = result.Snapshot.TakenAtUtc;

        var justEnded = new AutoRuleEngine();
        justEnded.MarkSessionEnded(Proc(10, 1, "claude.exe").Key, now);
        Assert.Empty(justEnded.LeftoversToStop(result, AutoOn, 1));

        var endedEarlier = new AutoRuleEngine();
        endedEarlier.MarkSessionEnded(Proc(10, 1, "claude.exe").Key, now.AddSeconds(-90));
        Assert.Single(endedEarlier.LeftoversToStop(result, AutoOn, 1));
    }

    [Fact]
    public void Auto_stop_rechecks_connections_right_before_stopping()
    {
        var root = Proc(999_010, 1, "node.exe");
        var tree = new OrphanTree(new ProcessNode(root, TimeSpan.FromHours(1), []), AgentKind.ClaudeCode, Attribution.Observed, "x", 10);
        var log = new ActionLog(Path.Combine(Path.GetTempPath(), $"ar-{Guid.NewGuid():N}", "a.jsonl"));
        ProcessTerminator Terminator(int? connections) =>
            new(log, () => [(root.Key, root.ParentPid, root.Name)], _ => connections);

        Assert.Contains("open connection", Terminator(2).StopTree(tree, "auto", requireNoConnections: true).Message);
        Assert.Contains("couldn't read network", Terminator(null).StopTree(tree, "auto", requireNoConnections: true).Message);
        Assert.Empty(log.ReadRecent(10)); // refused before anything was logged or touched
    }

    [Fact]
    public void Relaunch_refuses_when_the_original_folder_is_gone_or_unknown()
    {
        ActionLogEntry Entry(string? dir) => new(DateTime.UtcNow, "stop-tree", "done", "manual", true, "", 1, "node.exe",
            @"C:\Windows\System32\where.exe", "where.exe node", dir, 1, 0, "Claude Code", "Observed");

        var gone = Relauncher.Relaunch(Entry(@"C:\definitely\not\here\" + Guid.NewGuid()));
        Assert.False(gone.Success);
        Assert.Contains("no longer exists", gone.Message);
        Assert.False(Entry(null).CanRelaunch);
    }

    [Theory]
    [InlineData("node_modules/", true)]
    [InlineData("web/node_modules/", true)]
    [InlineData("bin/", true)]
    [InlineData("src/__pycache__/", true)]
    [InlineData("app.pyc", true)]
    [InlineData(".env", false)]
    [InlineData("local.db", false)]
    [InlineData("build/local.db", false)] // listed individually: a file, not a build directory
    [InlineData("secrets/", false)]
    public void Only_build_output_counts_as_regenerable(string entry, bool expected)
    {
        Assert.Equal(expected, DiskCleanup.IsRegenerable(entry));
    }

    [Fact]
    public void Worktree_with_an_ignored_local_file_is_not_removable()
    {
        if (ToolRunner.Find("git") is not { } git) return;
        var repo = Path.Combine(Path.GetTempPath(), $"ar-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        try
        {
            ToolRunner.Run(git, "init -q", TimeSpan.FromSeconds(30), repo);
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "*.db\nnode_modules/\n");
            ToolRunner.Run(git, "add .gitignore", TimeSpan.FromSeconds(30), repo);
            ToolRunner.Run(git, "-c user.name=t -c user.email=t@example.com commit -q -m init", TimeSpan.FromSeconds(30), repo);

            Directory.CreateDirectory(Path.Combine(repo, "node_modules"));
            File.WriteAllText(Path.Combine(repo, "node_modules", "x.js"), "");
            Assert.Null(DiskCleanup.WorktreeRemovalBlocker(git, repo)); // only build output: fine

            File.WriteAllText(Path.Combine(repo, "local.db"), "important");
            Assert.Contains("local.db", DiskCleanup.WorktreeRemovalBlocker(git, repo));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(repo, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Malformed_mcp_requests_get_errors_and_the_server_keeps_going()
    {
        var result = new Analyzer(new LineageStore()).Analyze(Snapshot(1, Proc(10, 1, "claude.exe")));
        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":123}""",
            """[1,2,3]""",
            """{"jsonrpc":"2.0","id":{"x":1},"method":"ping"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_session_processes","arguments":{"agent_pid":"abc"}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":"nope"}""",
            """{"jsonrpc":"2.0","id":4,"method":"ping"}"""));
        var output = new StringWriter();

        new McpServer(input, output, () => result).Run();

        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(6, replies.Count);
        Assert.All(replies.Take(5), r => Assert.NotNull(r["error"]));
        Assert.NotNull(replies[5]["result"]); // still serving
    }
}

[Collection("Global path overrides")]
public class HookInstallerSiblingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ar-hook-{Guid.NewGuid():N}");

    public HookInstallerSiblingTests()
    {
        Directory.CreateDirectory(_dir);
        ClaudeHookInstaller.SettingsPathOverride = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        ClaudeHookInstaller.SettingsPathOverride = null;
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Uninstall_keeps_unrelated_hooks_in_the_same_group()
    {
        File.WriteAllText(ClaudeHookInstaller.SettingsPath, """
            { "hooks": { "SessionEnd": [ { "matcher": "x", "hooks": [
                { "type": "command", "command": "\"C:\\Apps\\AgentReclaim.exe\" hook session-end" },
                { "type": "command", "command": "echo AgentReclaim-was-here" }
            ] } ] } }
            """);

        ClaudeHookInstaller.Uninstall();

        var group = JsonNode.Parse(File.ReadAllText(ClaudeHookInstaller.SettingsPath))!["hooks"]!["SessionEnd"]![0]!;
        Assert.Equal("x", group["matcher"]!.GetValue<string>());
        var remaining = Assert.Single(group["hooks"]!.AsArray());
        Assert.Equal("echo AgentReclaim-was-here", remaining!["command"]!.GetValue<string>());
    }
}
