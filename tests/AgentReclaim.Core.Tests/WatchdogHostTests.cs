using System.Text.Json.Nodes;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Watchdog;

namespace AgentReclaim.Core.Tests;

/// <summary>The Debug lines that say why a session did or didn't get an alert.</summary>
public class WatchdogTraceTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly WatchdogSettings Settings = new() { ActivityPaths = ["/work/{session}"] };

    private static (WatchdogEngine Engine, List<string> Lines) Engine(FakeProbes probes)
    {
        var lines = new List<string>();
        return (new WatchdogEngine(probes) { TraceTo = lines.Add }, lines);
    }

    private static List<WatchdogAlert> Minutes(WatchdogEngine engine, int from, int to)
    {
        var alerts = new List<WatchdogAlert>();
        for (var minute = from; minute <= to; minute++) alerts.AddRange(engine.Check(Settings, ["s1"], T0.AddMinutes(minute)));
        return alerts;
    }

    [Fact]
    public void Each_round_says_how_long_a_session_has_been_quiet_and_what_was_decided()
    {
        var (engine, lines) = Engine(new FakeProbes { ProgressNow = new ProgressMark("50|Testing", T0), Activity = new ActivityMark(T0, "/work/a.log") });
        Minutes(engine, 0, 14);
        Assert.Equal("session s1: fine; quiet 14 of 20 min, progress unchanged 14 of 60 min, newest file /work/a.log (progress: 50|Testing)", lines[^1]);

        Assert.Single(Minutes(engine, 15, 20));
        Assert.StartsWith("session s1: STUCK alert; quiet 20 of 20 min", lines[^1]);
        Assert.Empty(Minutes(engine, 21, 21));
        Assert.StartsWith("session s1: stuck, already told; quiet 21 of 20 min", lines[^1]);
    }

    [Fact]
    public void Files_moving_with_progress_still_says_stale()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|Testing", T0) };
        var (engine, lines) = Engine(probes);
        for (var minute = 0; minute <= 60; minute++)
        {
            probes.Activity = new ActivityMark(T0.AddMinutes(minute), "/work/a.log");
            engine.Check(Settings, ["s1"], T0.AddMinutes(minute));
        }
        Assert.StartsWith("session s1: STALE alert; quiet 0 of 20 min, progress unchanged 60 of 60 min", lines[^1]);
    }

    [Theory]
    [InlineData(null, "session s1: not judged, no progress file")]
    [InlineData("100|Done: all of it", "session s1: not judged, done (progress: 100|Done: all of it)")]
    [InlineData("40|Waiting for OK: the plan", "session s1: not judged, waiting (progress: 40|Waiting for OK: the plan)")]
    public void An_idle_session_says_why_it_was_not_judged(string? progress, string expected)
    {
        var (engine, lines) = Engine(new FakeProbes { ProgressNow = progress is null ? null : new ProgressMark(progress, T0) });
        engine.Check(Settings, ["s1"], T0);
        Assert.Equal(expected, Assert.Single(lines));
    }
}

/// <summary>The watchdog run on its own, as on Linux (agentreclaim-watchdog).</summary>
public sealed class WatchdogHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ar-host-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(_dir, "settings.json");
    private string Status => Path.Combine(_dir, "watchdog.json");

    public WatchdogHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteSettings(bool enabled, int checkSeconds) => File.WriteAllText(Settings,
        $$"""{ "Watchdog": { "Enabled": {{(enabled ? "true" : "false")}}, "Port": 0, "CheckSeconds": {{checkSeconds}}, "ProgressDirectory": "{{_dir.Replace("\\", "\\\\")}}" } }""");

    private static bool WaitFor(Func<bool> condition, TimeSpan limit)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < limit)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private (Task<int> Run, CancellationTokenSource Stop, StringWriter Output) Start()
    {
        var stop = new CancellationTokenSource();
        var output = new StringWriter();
        return (Task.Run(() => WatchdogHost.Run(Settings, Status, output, stop.Token)), stop, output);
    }

    [Fact]
    public void It_watches_until_stopped_and_then_says_it_has_stopped()
    {
        WriteSettings(enabled: true, checkSeconds: 1);
        var (run, stop, _) = Start();
        Assert.True(WaitFor(() => WatchdogStatus.Load(Status) is { Running: true, Port: > 0 } s && s.IsCurrent(DateTime.UtcNow), TimeSpan.FromSeconds(15)));

        stop.Cancel();
        Assert.True(run.Wait(TimeSpan.FromSeconds(15)));
        Assert.Equal(0, run.Result);
        // watchdog_status then gives no address: nothing of ours is listening there.
        Assert.False(WatchdogStatus.Load(Status)!.IsCurrent(DateTime.UtcNow));
    }

    [Fact]
    public void A_second_one_with_the_same_status_file_refuses_to_run()
    {
        WriteSettings(enabled: true, checkSeconds: 1);
        var (run, stop, _) = Start();
        try
        {
            Assert.True(WaitFor(() => File.Exists(Status + ".lock"), TimeSpan.FromSeconds(15)));
            Assert.True(WaitFor(() => WatchdogStatus.Load(Status) is { Running: true }, TimeSpan.FromSeconds(15)));
            var output = new StringWriter();
            Assert.Equal(1, WatchdogHost.Run(Settings, Status, output, CancellationToken.None));
            Assert.Contains("already running", output.ToString());
        }
        finally
        {
            stop.Cancel();
            run.Wait(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public void A_settings_change_applies_without_waiting_for_the_next_round()
    {
        // An hour between rounds: once the first has saved, only the settings watcher can bring another within the wait.
        WriteSettings(enabled: true, checkSeconds: 3600);
        var (run, stop, _) = Start();
        try
        {
            Assert.True(WaitFor(() => WatchdogStatus.Load(Status) is { Running: true, Port: > 0 }, TimeSpan.FromSeconds(15)));
            WriteSettings(enabled: false, checkSeconds: 3600);
            Assert.True(WaitFor(() => WatchdogStatus.Load(Status) is { Enabled: false, Running: false }, TimeSpan.FromSeconds(15)));
        }
        finally
        {
            stop.Cancel();
            run.Wait(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public void The_log_level_in_settings_is_handed_on_each_time_they_are_read()
    {
        File.WriteAllText(Settings, """{ "LogLevel": "Debug", "Watchdog": { "Enabled": true, "Port": 0, "CheckSeconds": 3600 } }""");
        var levels = new System.Collections.Concurrent.ConcurrentQueue<Diagnostics.LogLevel>();
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => WatchdogHost.Run(Settings, Status, new StringWriter(), stop.Token, levels.Enqueue));
        try
        {
            Assert.True(WaitFor(() => levels.Contains(Diagnostics.LogLevel.Debug), TimeSpan.FromSeconds(15)));
            File.WriteAllText(Settings, """{ "LogLevel": "Info", "Watchdog": { "Enabled": true, "Port": 0, "CheckSeconds": 3600 } }""");
            Assert.True(WaitFor(() => levels.LastOrDefault() == Diagnostics.LogLevel.Info, TimeSpan.FromSeconds(15)));
        }
        finally
        {
            stop.Cancel();
            run.Wait(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public void Deleting_the_settings_applies_at_once_too()
    {
        WriteSettings(enabled: true, checkSeconds: 3600);
        var (run, stop, _) = Start();
        try
        {
            Assert.True(WaitFor(() => WatchdogStatus.Load(Status) is { Running: true, Port: > 0 }, TimeSpan.FromSeconds(15)));
            File.Delete(Settings); // no settings: the defaults, which have it off
            Assert.True(WaitFor(() => WatchdogStatus.Load(Status) is { Enabled: false, Running: false }, TimeSpan.FromSeconds(15)));
        }
        finally
        {
            stop.Cancel();
            run.Wait(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public void Its_mcp_server_offers_only_watchdog_status()
    {
        var requests = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"resource_overview","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"watchdog_status","arguments":{}}}""");
        var output = new StringWriter();
        new McpServer(new StringReader(requests), output, () => throw new InvalidOperationException("no analysis here"),
            watchdogStatus: () => null, onlyTools: WatchdogHost.McpTools).Run();
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();

        var listed = replies[0]["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["watchdog_status"], listed);
        Assert.Contains("Unknown tool", replies[1]["error"]!["message"]!.GetValue<string>());
        Assert.Contains("never run", replies[2]["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }
}
