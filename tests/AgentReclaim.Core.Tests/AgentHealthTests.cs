using System.Text.Json.Nodes;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Cleanup;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

public class AgentDoctorTests
{
    // Real `claude doctor` output (2.1.312), trimmed.
    private const string ClaudeOutput = """
        Claude Code doctor

        Running: native (2.1.312)
        Platform: win32-x64
        Search: OK (bundled)
        Auto-updates: enabled
        Last update attempt: failed (install_failed) — 2026-10-29
        Managed settings (remote): not fetched — requires an Enterprise or Team subscription

        No installation issues found.

        For a full setup checkup that can also fix issues, run /doctor in a Claude Code session.
        """;

    // Real `codex doctor --json` output (0.162.1), trimmed, followed by a line on stderr.
    private const string CodexOutput = """
        {
          "schemaVersion": 1,
          "overallStatus": "fail",
          "codexVersion": "0.162.1",
          "checks": {
            "auth.credentials": { "id": "auth.credentials", "status": "ok", "summary": "auth is configured", "remediation": null },
            "mcp.config": { "id": "mcp.config", "status": "warning", "summary": "MCP configuration has optional issues",
                            "remediation": "Set the missing MCP env vars or disable the affected server." },
            "sandbox.helpers": { "id": "sandbox.helpers", "status": "fail",
                                 "summary": "elevated Windows sandbox provisioning recorded a structured failure",
                                 "remediation": "repair or reinstall the Codex CLI from an approved distribution" },
            "system.disk": { "id": "system.disk", "status": "ok", "summary": "sufficient free disk space (413.9 GiB)" }
          }
        }
        WARN some log line on stderr
        """;

    private static AgentDoctor Doctor(Func<string, string, ToolResult> run, string? home = null, Func<string, string?>? find = null) =>
        new(find ?? (name => $@"C:\bin\{name}.exe"), run, home ?? Path.Combine(Path.GetTempPath(), "AgentReclaim.Tests", "no-home"));

    [Fact]
    public void Claude_doctor_text_turns_problem_lines_into_warnings()
    {
        var report = Doctor((_, args) => args == "doctor" ? new ToolResult(true, 0, ClaudeOutput) : throw new InvalidOperationException()).Claude();

        Assert.Equal(HealthStatus.Warning, report.Status);
        var problem = Assert.Single(report.Problems);
        Assert.Equal("Last update attempt", problem.Name);
        Assert.Equal("failed (install_failed) — 2026-10-29", problem.Summary);
        Assert.StartsWith("Claude Code native (2.1.312): 1 warning", report.Summary);
    }

    [Fact]
    public void Claude_doctor_clean_output_is_ok_and_a_failed_run_is_a_failure()
    {
        var clean = ClaudeOutput.Replace("Last update attempt: failed (install_failed) — 2026-10-29", "Last update attempt: success");
        var ok = Doctor((_, _) => new ToolResult(true, 0, clean)).Claude();
        Assert.Equal(HealthStatus.Ok, ok.Status);
        Assert.Empty(ok.Problems);
        Assert.Contains("no installation issues found", ok.Summary);

        var failed = Doctor((_, _) => new ToolResult(false, -1, @"C:\bin\claude.exe timed out after 60s")).Claude();
        Assert.Equal(HealthStatus.Failed, failed.Status);
        Assert.Contains("timed out", failed.Summary);
    }

    [Fact]
    public void Codex_doctor_json_lists_failures_first_with_their_fixes()
    {
        var report = Doctor((_, args) => args == "doctor --json" ? new ToolResult(false, 1, CodexOutput) : throw new InvalidOperationException()).Codex();

        Assert.Equal(HealthStatus.Failed, report.Status);
        Assert.Equal(["sandbox.helpers", "mcp.config"], report.Problems.Select(p => p.Name));
        Assert.Equal(HealthStatus.Failed, report.Problems[0].Status);
        Assert.Equal("repair or reinstall the Codex CLI from an approved distribution", report.Problems[0].Fix);
        Assert.Equal("Codex 0.162.1: 1 failed, 1 warning of 4 checks.", report.Summary);
    }

    [Fact]
    public void Codex_overall_status_counts_even_without_a_failing_check_and_garbage_is_a_failure()
    {
        var odd = Doctor((_, _) => new ToolResult(true, 0, """{"overallStatus":"warning","checks":{"a":{"status":"ok"}}}""")).Codex();
        Assert.Equal(HealthStatus.Warning, odd.Status);
        Assert.Contains("\"warning\"", odd.Summary);

        var allOk = Doctor((_, _) => new ToolResult(true, 0, """{"overallStatus":"ok","checks":{"a":{"status":"ok"},"b":{"status":"skipped"}}}""")).Codex();
        Assert.Equal(HealthStatus.Ok, allOk.Status);
        Assert.Contains("all 2 checks passed", allOk.Summary);

        var garbage = Doctor((_, _) => new ToolResult(false, 2, "error: unrecognized subcommand 'doctor'")).Codex();
        Assert.Equal(HealthStatus.Failed, garbage.Status);
        Assert.Contains("unrecognized subcommand", garbage.Summary);
    }

    [Fact]
    public void Gemini_checks_version_mcp_servers_and_settings()
    {
        var home = Path.Combine(Path.GetTempPath(), "AgentReclaim.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, ".gemini"));
        var settings = Path.Combine(home, ".gemini", "settings.json");
        File.WriteAllText(settings, "{\n  // comments are fine\n  \"theme\": \"Default\",\n}");
        var mcp = "Configured MCP servers:\n\n✓ files: npx files (stdio) - Connected";
        ToolResult Run(string _, string args) => args switch
        {
            "--version" => new ToolResult(true, 0, "0.63.0"),
            "mcp list" => new ToolResult(true, 0, mcp),
            _ => throw new InvalidOperationException(args),
        };

        var ok = Doctor(Run, home).Gemini();
        Assert.Equal(HealthStatus.Ok, ok.Status);
        Assert.Equal("Gemini CLI 0.63.0: version, MCP servers and settings look fine.", ok.Summary);

        // A server's command line can hold an opaque credential: only its name is kept.
        mcp += "\n✗ broken: node missing.js --api-key fixture-opaque-secret-123456 (stdio) - Disconnected";
        File.WriteAllText(settings, "{ \"theme\": ");
        var bad = Doctor(Run, home).Gemini();
        Assert.Equal(HealthStatus.Failed, bad.Status);
        Assert.Equal(["mcp", "settings"], bad.Problems.Select(p => p.Name));
        Assert.Equal("MCP server \"broken\" is disconnected.", bad.Problems[0].Summary);
        Assert.DoesNotContain("fixture-opaque-secret", System.Text.Json.JsonSerializer.Serialize(bad));
    }

    [Fact]
    public void Codex_reports_of_an_unknown_shape_or_a_failed_run_never_read_as_healthy()
    {
        var empty = Doctor((_, _) => new ToolResult(false, 1, "{}")).Codex();
        Assert.Equal(HealthStatus.Failed, empty.Status);
        Assert.Contains("isn't in a format", empty.Summary);

        var noStatus = Doctor((_, _) => new ToolResult(true, 0, """{"overallStatus":"ok","checks":{"a":{"summary":"?"}}}""")).Codex();
        Assert.Equal(HealthStatus.Warning, noStatus.Status);

        var exitOnly = Doctor((_, _) => new ToolResult(false, 3, """{"overallStatus":"ok","checks":{"a":{"status":"ok"}}}""")).Codex();
        Assert.Equal(HealthStatus.Failed, exitOnly.Status);
        Assert.Contains("exited with code 3", exitOnly.Problems[0].Summary);
    }

    [Fact]
    public void Named_secrets_are_hidden_but_their_names_kept()
    {
        Assert.Equal("node s.js --api-key [hidden] --port 3",
            AgentDoctor.Redact("node s.js --api-key fixture-opaque-secret-123456 --port 3"));
        Assert.Equal("url https://[hidden]@host/x and TOKEN=[hidden]",
            AgentDoctor.Redact("url https://user:p4ss@host/x and TOKEN=abc123"));
        Assert.Equal("Last update attempt: failed (install_failed)", AgentDoctor.Redact("Last update attempt: failed (install_failed)"));
    }

    [Fact]
    public void A_tool_whose_helper_keeps_its_output_open_fails_instead_of_hanging_or_passing_partial_output()
    {
        // cmd exits at once; the ping it started in the background keeps the stderr pipe open for ~7 s.
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = ToolRunner.Run(cmd, "/c \"start /b ping -n 8 127.0.0.1 >nul & echo !! .env\"", TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Contains("still open", result.Output);
        Assert.DoesNotContain(".env", result.Output);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void A_missing_cli_is_not_installed_and_does_not_count_as_a_problem()
    {
        var doctor = Doctor((_, _) => throw new InvalidOperationException("must not run"), find: _ => null);

        var reports = doctor.RunAll();

        Assert.All(reports, r => Assert.Equal(HealthStatus.NotInstalled, r.Status));
        Assert.Equal(HealthStatus.Ok, AgentDoctor.Worst(reports.Select(r => r.Status)));
        Assert.Equal("Claude Code not installed · Codex not installed · Gemini CLI not installed", AgentDoctor.OneLine(reports));
    }

    [Fact]
    public void One_line_names_the_problems()
    {
        var reports = new[]
        {
            Doctor((_, _) => new ToolResult(true, 0, ClaudeOutput.Replace("failed (install_failed)", "success"))).Claude(),
            Doctor((_, _) => new ToolResult(false, 1, CodexOutput)).Codex(),
        };

        Assert.Equal("Claude Code OK · Codex 1 failed, 1 warning (sandbox.helpers, mcp.config)", AgentDoctor.OneLine(reports));
    }

    [Fact]
    public void Secrets_in_output_are_hidden()
    {
        var output = ClaudeOutput.Replace("Search: OK (bundled)",
            "Auth error: token sk-ant-api03-abcdefABCDEF123456 and ghp_abcdefghijklmnopqrstuvwxyz0123 and Bearer abc.def-ghi");

        var report = Doctor((_, _) => new ToolResult(true, 0, output)).Claude();

        var text = string.Join("\n", report.Problems.Select(p => p.Summary));
        Assert.Contains("[hidden]", text);
        Assert.DoesNotContain("sk-ant-", text);
        Assert.DoesNotContain("ghp_", text);
        Assert.DoesNotContain("abc.def-ghi", text);
    }

    [Fact]
    public void Reports_survive_a_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "AgentReclaim.Tests", Guid.NewGuid().ToString("N"), "health.json");
        var reports = new[] { Doctor((_, _) => new ToolResult(false, 1, CodexOutput)).Codex() };

        AgentDoctor.Save(path, reports);
        var loaded = AgentDoctor.Load(path);

        var report = Assert.Single(loaded);
        Assert.Equal(AgentKind.Codex, report.Agent);
        Assert.Equal(HealthStatus.Failed, report.Status);
        Assert.Equal(AgentDoctor.ForSaving(reports[0]).Problems, report.Problems);
        Assert.Equal("Codex: 1 failed, 1 warning.", report.Summary);

        foreach (var broken in new[] { "not json", "[null]", "[{}]", """[{"Agent":"Codex","Status":"Ok","Summary":"x","Problems":[null]}]""",
                     """[{"Agent":"Codex","Status":"Ok","Summary":"x"}]""", """[{"Agent":7,"Status":"Ok","Summary":"x","Problems":[]}]""" })
        {
            File.WriteAllText(path, broken);
            Assert.Empty(AgentDoctor.Load(path));
        }
    }

    [Fact]
    public void Quoted_secrets_never_reach_the_saved_report()
    {
        var output = ClaudeOutput.Replace("Search: OK (bundled)",
            "MCP error: config {\"token\":\"opaque one\",\"apiKey\": \"two\"} run with --password \"first second\" and --auth-token 'three four'");
        var path = Path.Combine(Path.GetTempPath(), "AgentReclaim.Tests", Guid.NewGuid().ToString("N"), "health.json");

        AgentDoctor.Save(path, [Doctor((_, _) => new ToolResult(true, 0, output)).Claude()]);

        var saved = File.ReadAllText(path);
        foreach (var secret in new[] { "opaque", "one", "two", "first", "second", "three", "four" })
            Assert.DoesNotContain(secret, saved);
    }

    [Fact]
    public void The_saved_form_keeps_no_cli_text_only_labels_statuses_and_fixes()
    {
        var output = ClaudeOutput.Replace("Search: OK (bundled)", "x9Zq-opaque: failed to load plugin from C:/secret/place");
        var live = Doctor((_, _) => new ToolResult(true, 0, output)).Claude();
        Assert.Contains(live.Problems, p => p.Summary.Contains("secret/place")); // shown live on the Health tab

        var saved = AgentDoctor.ForSaving(live);

        Assert.Equal("Claude Code: 2 warnings.", saved.Summary); // no version string from the output either
        Assert.Equal(["doctor", "Last update attempt"], saved.Problems.Select(p => p.Name)); // "x9Zq-opaque" isn't a plain label
        Assert.All(saved.Problems, p => Assert.Equal("", p.Summary));
        Assert.All(saved.Problems, p => Assert.Equal(HealthStatus.Warning, p.Status));
        Assert.DoesNotContain("opaque", System.Text.Json.JsonSerializer.Serialize(saved));
        Assert.DoesNotContain("secret", AgentDoctor.OneLine([live]));
    }

    [Fact]
    public void A_secret_whose_name_comes_before_the_first_colon_stays_hidden()
    {
        // The line is split at its first colon into name and summary; redacting first keeps the pair together.
        var output = ClaudeOutput.Replace("Search: OK (bundled)", """{"token":"opaque secret","error":"invalid"}""");
        var path = Path.Combine(Path.GetTempPath(), "AgentReclaim.Tests", Guid.NewGuid().ToString("N"), "health.json");

        AgentDoctor.Save(path, [Doctor((_, _) => new ToolResult(true, 0, output)).Claude()]);

        Assert.DoesNotContain("opaque", File.ReadAllText(path));
    }

    [Fact]
    public void Authorization_headers_lose_scheme_and_credential()
    {
        Assert.Equal("Authorization: [hidden] next", AgentDoctor.Redact("Authorization: Basic dXNlcjpwYXNz next"));
        Assert.Equal("\"Proxy-Authorization\":[hidden],", AgentDoctor.Redact("\"Proxy-Authorization\":\"Bearer abc.def\","));

        var codex = """{"overallStatus":"fail","checks":{"net":{"status":"fail","summary":"proxy said 401 for Authorization: Basic dXNlcjpwYXNz"}}}""";
        var path = Path.Combine(Path.GetTempPath(), "AgentReclaim.Tests", Guid.NewGuid().ToString("N"), "health.json");
        AgentDoctor.Save(path, [Doctor((_, _) => new ToolResult(false, 1, codex)).Codex()]);
        Assert.DoesNotContain("dXNlcjpwYXNz", File.ReadAllText(path));
    }

    [Fact]
    public void A_cli_started_by_a_health_check_is_not_an_agent_session()
    {
        // 50 is AgentReclaim running a check: 51 is `claude doctor`, 53 is node running Gemini CLI under cmd.exe (52).
        var snapshot = Snapshot(5,
            Proc(10, 1, "claude.exe"),
            Proc(51, 50, "claude.exe", "claude.exe doctor", startMinute: 4),
            Proc(52, 50, "cmd.exe", startMinute: 4),
            Proc(53, 52, "node.exe", @"node C:\npm\node_modules\@google\gemini-cli\dist\index.js mcp list", startMinute: 4));
        var options = new AnalyzerOptions { IsHealthCheck = pid => pid is 51 or 52 };

        var result = new Analyzer(new LineageStore(), options).Analyze(snapshot);

        Assert.Equal([10], result.Sessions.Select(s => s.Root.Process.Pid));
        Assert.Empty(result.Orphans);

        var normal = new Analyzer(new LineageStore(), new AnalyzerOptions { IsHealthCheck = _ => false }).Analyze(snapshot);
        Assert.Equal(3, normal.Sessions.Count);
    }

    [Fact]
    public void Mcp_check_agent_health_returns_the_reports()
    {
        var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"check_agent_health","arguments":{}}}""");
        var output = new StringWriter();
        var reports = new[] { Doctor((_, _) => new ToolResult(false, 1, CodexOutput)).Codex() };

        new McpServer(input, output, () => throw new InvalidOperationException("no analysis needed"), healthCheck: () => reports).Run();

        var text = JsonNode.Parse(output.ToString())!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        var agents = JsonNode.Parse(text)!.AsArray();
        Assert.Equal("Codex", agents[0]!["agent"]!.GetValue<string>());
        Assert.Equal("Failed", agents[0]!["status"]!.GetValue<string>());
        Assert.Equal("sandbox.helpers", agents[0]!["problems"]![0]!["check"]!.GetValue<string>());
    }
}

public class CpuAlertTests
{
    private static AnalysisResult At(double cpu, params ProcessInfo[] processes) =>
        new Analyzer(new LineageStore()).Analyze(new ProcessSnapshot(T0, processes, new SystemStats(16L << 30, 8L << 30, cpu)));

    private static readonly AppSettings On = new() { CpuAlerts = true, CpuAlertPercent = 90, CpuAlertMinutes = 3 };

    [Fact]
    public void Fires_once_after_the_cpu_stays_busy_and_rearms_after_it_calms_down()
    {
        var rule = new CpuAlertRule { Cooldown = TimeSpan.Zero };
        var busy = At(95, Proc(10, 1, "claude.exe", cpu: 40), Proc(30, 1, "codex.exe", cpu: 2));

        Assert.Null(rule.Evaluate(busy, On, T0));
        Assert.Null(rule.Evaluate(busy, On, T0.AddMinutes(2)));
        Assert.Null(rule.Evaluate(At(50), On, T0.AddMinutes(2.5))); // a dip restarts the clock
        Assert.Null(rule.Evaluate(busy, On, T0.AddMinutes(3)));
        Assert.Null(rule.Evaluate(busy, On, T0.AddMinutes(5)));
        var alert = rule.Evaluate(busy, On, T0.AddMinutes(6));
        Assert.NotNull(alert);
        Assert.Contains("above 90% for 3 min (now 95%)", alert.Text);
        Assert.Contains("Claude Code (pid 10) 40%", alert.Text);
        Assert.DoesNotContain("Codex", alert.Text); // under 5%

        Assert.Null(rule.Evaluate(busy, On, T0.AddMinutes(10))); // same episode
        Assert.Null(rule.Evaluate(At(85), On, T0.AddMinutes(11))); // not clearly below
        Assert.Null(rule.Evaluate(busy, On, T0.AddMinutes(12)));
        Assert.Null(rule.Evaluate(busy, On, T0.AddMinutes(16)));
        Assert.Null(rule.Evaluate(At(40), On, T0.AddMinutes(17))); // re-armed
        Assert.Null(rule.Evaluate(busy, On, T0.AddMinutes(18)));
        Assert.NotNull(rule.Evaluate(busy, On, T0.AddMinutes(21)));
    }

    [Fact]
    public void Off_by_default_and_the_cooldown_limits_repeats()
    {
        var busy = At(99);
        var off = new CpuAlertRule();
        Assert.False(new AppSettings().CpuAlerts);
        Assert.Null(off.Evaluate(busy, new AppSettings(), T0));
        Assert.Null(off.Evaluate(busy, new AppSettings(), T0.AddMinutes(10)));

        var rule = new CpuAlertRule { Cooldown = TimeSpan.FromMinutes(30) };
        rule.Evaluate(busy, On, T0);
        Assert.NotNull(rule.Evaluate(busy, On, T0.AddMinutes(3)));
        rule.Evaluate(At(10), On, T0.AddMinutes(4));
        rule.Evaluate(busy, On, T0.AddMinutes(5));
        Assert.Null(rule.Evaluate(busy, On, T0.AddMinutes(9))); // re-armed but within the cooldown
    }
}
