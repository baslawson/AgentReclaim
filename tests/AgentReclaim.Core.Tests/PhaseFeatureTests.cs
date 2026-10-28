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

public class AutoRuleTests
{
    private static AppSettings AutoOn => new() { AutoStopLeftovers = true, AutoStopIdleMinutes = 15, HookGraceSeconds = 60 };

    /// <summary>Claude session with a server, then Claude exits; returns the analysis <paramref name="idleMinutes"/> later.</summary>
    private static (Analyzer Analyzer, AnalysisResult Result) LeftoverAfter(int idleMinutes, int connections = 0)
    {
        var analyzer = new Analyzer(new LineageStore());
        var server = Proc(12, 10, "node.exe", "node vite", startMinute: 1, connections: connections);
        analyzer.Analyze(Snapshot(2, Proc(10, 1, "claude.exe"), server));
        analyzer.Analyze(Snapshot(3, server));
        return (analyzer, analyzer.Analyze(Snapshot(3 + idleMinutes, server)));
    }

    [Fact]
    public void Nothing_happens_while_the_rule_is_off()
    {
        var (_, result) = LeftoverAfter(60);
        Assert.Empty(new AutoRuleEngine().LeftoversToStop(result, new AppSettings(), selfPid: 1));
    }

    [Fact]
    public void Idle_observed_leftover_is_stopped_once_past_the_threshold()
    {
        var engine = new AutoRuleEngine();
        Assert.Empty(engine.LeftoversToStop(LeftoverAfter(5).Result, AutoOn, 1));
        Assert.Single(engine.LeftoversToStop(LeftoverAfter(20).Result, AutoOn, 1));
    }

    [Fact]
    public void Hook_reported_session_end_shortens_the_wait()
    {
        var engine = new AutoRuleEngine();
        engine.MarkSessionEnded(Proc(10, 1, "claude.exe").Key, T0);
        Assert.Single(engine.LeftoversToStop(LeftoverAfter(2).Result, AutoOn, 1));
    }

    [Fact]
    public void Leftover_with_live_connections_is_left_alone()
    {
        Assert.Empty(new AutoRuleEngine().LeftoversToStop(LeftoverAfter(60, connections: 1).Result, AutoOn, 1));
    }

    [Fact]
    public void Guessed_leftovers_are_never_auto_stopped()
    {
        var analyzer = new Analyzer(new LineageStore());
        var orphan = Proc(30, 999, "node.exe", @"node C:\Users\me\.claude\x.mjs");
        analyzer.Analyze(Snapshot(0, orphan));
        var result = analyzer.Analyze(Snapshot(120, orphan));

        Assert.Single(result.Orphans);
        Assert.Empty(new AutoRuleEngine().LeftoversToStop(result, AutoOn, 1));
    }

    [Fact]
    public void A_stop_that_was_tried_is_not_retried()
    {
        var engine = new AutoRuleEngine();
        var result = LeftoverAfter(20).Result;
        engine.RecordStopAttempt(Assert.Single(engine.LeftoversToStop(result, AutoOn, 1)));
        Assert.Empty(engine.LeftoversToStop(result, AutoOn, 1));
    }

    [Fact]
    public void Build_hogging_cpu_is_throttled_after_the_delay_but_never_the_agent()
    {
        var engine = new AutoRuleEngine();
        var settings = new AppSettings { AutoThrottleBuilds = true, ThrottleCpuPercent = 50, ThrottleAfterSeconds = 60 };
        var analyzer = new Analyzer(new LineageStore());
        AnalysisResult At(int minute) => analyzer.Analyze(Snapshot(minute,
            Proc(10, 1, "claude.exe", cpu: 90),
            Proc(11, 10, "tsc.exe", startMinute: 0, cpu: 95)));

        Assert.Empty(engine.ProcessesToThrottle(At(1), settings));
        var plan = Assert.Single(engine.ProcessesToThrottle(At(3), settings));
        Assert.Equal("tsc.exe", plan.Process.Name);

        engine.RecordThrottled(plan.Process.Key);
        Assert.Empty(engine.ProcessesToThrottle(At(4), settings));
    }
}

public class LeakDetectionTests
{
    [Fact]
    public void Steady_growth_of_an_agent_raises_a_finding()
    {
        var analyzer = new Analyzer(new LineageStore());
        AnalysisResult? result = null;
        for (var minute = 0; minute <= 30; minute += 2)
            result = analyzer.Analyze(Snapshot(minute, Proc(10, 1, "claude.exe", mb: 600 + minute * 30)));

        var finding = Assert.Single(result!.Findings);
        Assert.Contains("keeps growing", finding.Title);
    }

    [Fact]
    public void A_single_spike_is_not_a_leak()
    {
        var analyzer = new Analyzer(new LineageStore());
        AnalysisResult? result = null;
        for (var minute = 0; minute <= 30; minute += 2)
        {
            var mb = minute == 30 ? 2000 : (minute % 4 == 0 ? 600 : 550); // flat and noisy, one jump at the end
            result = analyzer.Analyze(Snapshot(minute, Proc(10, 1, "claude.exe", mb: mb)));
        }

        Assert.Empty(result!.Findings);
    }
}

public class RelaunchTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" server.js --port 5173", @"C:\Program Files\nodejs\node.exe", "server.js --port 5173")]
    [InlineData(@"node.exe vite", "node.exe", "vite")]
    [InlineData(@"""\\?\C:\x\codex.exe"" app-server", @"C:\x\codex.exe", "app-server")]
    [InlineData(@"ping.exe", "ping.exe", "")]
    public void Splits_program_from_arguments(string commandLine, string file, string arguments)
    {
        Assert.Equal((file, arguments), Relauncher.Split(commandLine));
    }
}

[Collection("Global path overrides")]
public class ConfigEditingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"agentreclaim-cfg-{Guid.NewGuid():N}");

    public ConfigEditingTests()
    {
        Directory.CreateDirectory(_dir);
        WslManager.ConfigPathOverride = Path.Combine(_dir, ".wslconfig");
        ClaudeHookInstaller.SettingsPathOverride = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        WslManager.ConfigPathOverride = null;
        ClaudeHookInstaller.SettingsPathOverride = null;
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Wslconfig_gets_the_setting_without_losing_anything_else()
    {
        File.WriteAllLines(WslManager.ConfigPath, ["[wsl2]", "memory=8GB", "", "[experimental]", "sparseVhd=true"]);

        WslManager.EnableAutoMemoryReclaim();

        var lines = File.ReadAllLines(WslManager.ConfigPath);
        Assert.Contains("memory=8GB", lines);
        Assert.Contains("sparseVhd=true", lines);
        Assert.Equal("dropCache", WslManager.ReadAutoMemoryReclaim());
        Assert.Single(Directory.GetFiles(_dir, ".wslconfig.bak-*"));
    }

    [Fact]
    public void Existing_reclaim_value_is_replaced_not_duplicated()
    {
        File.WriteAllLines(WslManager.ConfigPath, ["[experimental]", "autoMemoryReclaim=gradual"]);
        WslManager.EnableAutoMemoryReclaim();
        Assert.Single(File.ReadAllLines(WslManager.ConfigPath), l => l.StartsWith("autoMemoryReclaim"));
    }

    [Fact]
    public void Missing_or_unknown_reclaim_setting_counts_as_on_like_wsl()
    {
        Assert.True(WslManager.IsAutoMemoryReclaimOn()); // no .wslconfig: WSL's default is dropCache
        Assert.True(WslManager.IsAutoMemoryReclaimOn("gradual"));
        Assert.True(WslManager.IsAutoMemoryReclaimOn("typo")); // WSL treats unknown values as dropCache
        Assert.False(WslManager.IsAutoMemoryReclaimOn("Disabled"));
        Assert.False(WslManager.IsAutoMemoryReclaimOn(" disabled "));
    }

    [Fact]
    public void Turning_reclaim_off_writes_disabled_and_keeps_everything_else()
    {
        File.WriteAllLines(WslManager.ConfigPath,
            ["[wsl2]", "memory=8GB", "", "[experimental]", "autoMemoryReclaim=dropcache  # added by AgentReclaim", "sparseVhd=true"]);

        Assert.True(WslManager.DisableAutoMemoryReclaim());

        Assert.Equal(["[wsl2]", "memory=8GB", "", "[experimental]", "autoMemoryReclaim=disabled", "sparseVhd=true"],
            File.ReadAllLines(WslManager.ConfigPath));
        Assert.False(WslManager.IsAutoMemoryReclaimOn());
        Assert.Single(Directory.GetFiles(_dir, ".wslconfig.bak-*"));
    }

    [Fact]
    public void Turning_reclaim_off_without_a_config_creates_one()
    {
        // Deleting the line or the file would leave WSL's default (on); only an explicit value turns it off.
        Assert.True(WslManager.DisableAutoMemoryReclaim());
        Assert.Equal(["[experimental]", "autoMemoryReclaim=disabled"], File.ReadAllLines(WslManager.ConfigPath));

        Assert.False(WslManager.DisableAutoMemoryReclaim()); // already off: nothing written
        Assert.Empty(Directory.GetFiles(_dir, ".wslconfig.bak-*")); // there was no file to back up, and nothing was rewritten
    }

    [Fact]
    public void Turning_reclaim_back_on_replaces_disabled()
    {
        File.WriteAllLines(WslManager.ConfigPath, ["[experimental]", "autoMemoryReclaim=disabled"]);
        WslManager.EnableAutoMemoryReclaim();
        Assert.Equal("dropCache", WslManager.ReadAutoMemoryReclaim());
        Assert.True(WslManager.IsAutoMemoryReclaimOn());
    }

    [Fact]
    public void Equivalent_data_directory_spellings_share_one_instance()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AgentReclaim-review-data");
        Assert.Equal(AppPaths.InstanceSuffixFor(dir), AppPaths.InstanceSuffixFor(dir + Path.DirectorySeparatorChar));
        Assert.Equal(AppPaths.InstanceSuffixFor(dir), AppPaths.InstanceSuffixFor(dir.Replace('\\', '/') + "/"));
        Assert.Equal(AppPaths.InstanceSuffixFor(dir), AppPaths.InstanceSuffixFor(dir.ToUpperInvariant()));
        Assert.NotEqual(AppPaths.InstanceSuffixFor(dir), AppPaths.InstanceSuffixFor(dir + "-other"));
    }

    [Fact]
    public void Uninstaller_removes_only_the_hook_of_its_own_copy()
    {
        var installed = Path.Combine(_dir, "Programs", "AgentReclaim", "AgentReclaim.exe");
        var portable = Path.Combine(_dir, "portable", "AgentReclaim.exe");
        ClaudeHookInstaller.Install(portable);

        Assert.Null(ClaudeHookInstaller.Uninstall(onlyForExe: installed)); // not ours: untouched, no backup written
        Assert.True(ClaudeHookInstaller.IsInstalled());
        var backups = Directory.GetFiles(_dir, "settings.json.bak-*").Length;

        Assert.NotNull(ClaudeHookInstaller.Uninstall(onlyForExe: portable));
        Assert.False(ClaudeHookInstaller.IsInstalled());
        Assert.Equal(backups + 1, Directory.GetFiles(_dir, "settings.json.bak-*").Length);
    }

    [Fact]
    public void Claude_hook_install_keeps_other_hooks_and_uninstall_removes_only_ours()
    {
        File.WriteAllText(ClaudeHookInstaller.SettingsPath, """
            {
              // user comment
              "model": "opus",
              "hooks": { "SessionEnd": [ { "hooks": [ { "type": "command", "command": "echo bye" } ] } ] },
            }
            """);

        ClaudeHookInstaller.Install(@"C:\Apps\AgentReclaim.exe");
        ClaudeHookInstaller.Install(@"C:\Apps\AgentReclaim.exe"); // idempotent
        Assert.True(ClaudeHookInstaller.IsInstalled());
        var installed = JsonNode.Parse(File.ReadAllText(ClaudeHookInstaller.SettingsPath))!;
        Assert.Equal("opus", installed["model"]!.GetValue<string>());
        Assert.Equal(2, installed["hooks"]!["SessionEnd"]!.AsArray().Count);

        ClaudeHookInstaller.Uninstall();
        Assert.False(ClaudeHookInstaller.IsInstalled());
        var after = JsonNode.Parse(File.ReadAllText(ClaudeHookInstaller.SettingsPath))!;
        Assert.Equal("echo bye", after["hooks"]!["SessionEnd"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }
}

public class McpServerTests
{
    [Fact]
    public void Speaks_initialize_list_and_call()
    {
        var analyzer = new Analyzer(new LineageStore());
        var result = analyzer.Analyze(Snapshot(5, Proc(10, 1, "claude.exe", mb: 700), Proc(11, 10, "node.exe", mb: 300)));
        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"resource_overview","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"stop_everything","arguments":{}}}"""));
        var output = new StringWriter();

        new McpServer(input, output, () => result).Run();

        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(4, replies.Count); // the notification gets no reply
        Assert.Equal("agentreclaim", replies[0]["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(3, replies[1]["result"]!["tools"]!.AsArray().Count);
        Assert.Contains("\"ai_tools_memory_mb\": 1000", replies[2]["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.NotNull(replies[3]["error"]); // there is no tool that changes anything
    }
}

public class DiskCleanupTests
{
    [Fact]
    public void Measure_counts_files_and_finds_the_newest()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agentreclaim-disk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(dir, "sub", "b.bin"), new byte[500]);

            var (bytes, newest) = DiskCleanup.Measure(dir, CancellationToken.None);

            Assert.Equal(1500, bytes);
            Assert.True(DateTime.UtcNow - newest < TimeSpan.FromMinutes(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
