using System.Text.Json.Nodes;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Watchdog;

namespace AgentReclaim.Core.Tests;

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
