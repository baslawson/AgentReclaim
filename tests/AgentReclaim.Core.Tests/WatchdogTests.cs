using AgentReclaim.Core.Cleanup;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Settings;
using AgentReclaim.Core.Watchdog;

namespace AgentReclaim.Core.Tests;

internal sealed class FakeProbes : IWatchdogProbes
{
    public IReadOnlyList<string>? Repo = ["commit a one"];
    public string? Server = "boot=1";
    public ActivityMark? Activity;
    public ProgressMark? ProgressNow;
    public List<string> AskedPaths = [];
    public int ServerReads;
    public List<string> Setup = [];

    public IReadOnlyList<string> SetupProblems(WatchdogSettings settings) => Setup;

    public IReadOnlyList<string>? RepoState(WatchedRepo repo) => Repo;

    public string? ServerState(WatchedServer server)
    {
        ServerReads++;
        return Server;
    }

    public ActivityMark? NewestActivity(IReadOnlyList<string> paths)
    {
        AskedPaths = paths.ToList();
        return Activity;
    }

    public ProgressMark? Progress(string session) => ProgressNow;
}

public class WatchdogEngineTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] OneSession = ["s1"];

    private static WatchdogSettings Settings(bool repo = false, bool server = false) => new()
    {
        ServerEveryChecks = 1,
        ActivityPaths = [@"C:\work\{session}\files"],
        Repos = repo ? [new WatchedRepo { Path = "repo" }] : [],
        Servers = server ? [new WatchedServer { Name = "home", SshTarget = "root@h", Notify = true }] : [],
    };

    [Fact]
    public void A_repo_is_silent_until_it_changes_then_reports_once()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        Assert.Empty(engine.Check(Settings(repo: true), [], T0));
        Assert.Empty(engine.Check(Settings(repo: true), [], T0));

        probes.Repo = ["commit b two"];
        var alert = Assert.Single(engine.Check(Settings(repo: true), [], T0));
        Assert.Equal(WatchdogAlertKind.Change, alert.Kind);
        Assert.Contains("< commit a one", alert.Text);
        Assert.Contains("> commit b two", alert.Text);
        Assert.False(alert.Notify);
        Assert.Null(alert.Session);

        Assert.Empty(engine.Check(Settings(repo: true), [], T0));
    }

    [Fact]
    public void A_failed_repo_read_is_not_a_change()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        engine.Check(Settings(repo: true), [], T0);
        probes.Repo = null;
        Assert.Empty(engine.Check(Settings(repo: true), [], T0));
        probes.Repo = ["commit a one"];
        Assert.Empty(engine.Check(Settings(repo: true), [], T0));
    }

    [Fact]
    public void A_server_change_must_be_read_twice_in_a_row()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        Assert.Empty(engine.Check(Settings(server: true), [], T0));

        probes.Server = "boot=2";
        Assert.Empty(engine.Check(Settings(server: true), [], T0));
        var alert = Assert.Single(engine.Check(Settings(server: true), [], T0));
        Assert.Equal(WatchdogAlertKind.Server, alert.Kind);
        Assert.Contains("was: boot=1", alert.Text);
        Assert.Contains("now: boot=2", alert.Text);
        Assert.True(alert.Notify);

        Assert.Empty(engine.Check(Settings(server: true), [], T0));
    }

    [Fact]
    public void One_odd_server_reading_is_not_an_alert()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        engine.Check(Settings(server: true), [], T0);
        probes.Server = null;
        Assert.Empty(engine.Check(Settings(server: true), [], T0));
        probes.Server = "boot=1";
        Assert.Empty(engine.Check(Settings(server: true), [], T0));
        probes.Server = null;
        Assert.Empty(engine.Check(Settings(server: true), [], T0)); // once again, so still not two in a row
    }

    [Fact]
    public void A_server_that_stops_answering_is_named_and_the_note_says_it_may_be_this_PC()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        engine.Check(Settings(server: true), [], T0);
        probes.Server = null;
        engine.Check(Settings(server: true), [], T0);
        var alert = Assert.Single(engine.Check(Settings(server: true), [], T0));
        Assert.Contains("now: unreachable", alert.Text);
        Assert.Contains("this PC", alert.Text);
    }

    [Fact]
    public void Servers_are_only_read_every_so_many_checks()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        var settings = Settings(server: true);
        settings.ServerEveryChecks = 3;
        for (var i = 0; i < 6; i++) engine.Check(settings, [], T0);
        Assert.Equal(2, probes.ServerReads);
    }

    [Fact]
    public void A_restart_of_the_server_while_the_app_was_closed_is_still_noticed()
    {
        var first = new FakeProbes();
        var before = new WatchdogEngine(first);
        before.Check(Settings(server: true), [], T0);

        var second = new FakeProbes { Server = "boot=2" };
        var after = new WatchdogEngine(second, before.Memory);
        Assert.Empty(after.Check(Settings(server: true), [], T0));
        Assert.Single(after.Check(Settings(server: true), [], T0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("100|Done: all of it")]
    public void An_idle_session_is_never_an_alert(string? progress)
    {
        var probes = new FakeProbes { ProgressNow = progress is null ? null : new ProgressMark(progress, T0) };
        var engine = new WatchdogEngine(probes);
        Assert.Empty(engine.Check(Settings(), OneSession, T0.AddHours(9)));
    }

    /// <summary>Checks once a minute, as a running app does: a jump between checks counts as the PC having slept.</summary>
    private static List<WatchdogAlert> Minutes(WatchdogEngine engine, int from, int to)
    {
        var alerts = new List<WatchdogAlert>();
        for (var minute = from; minute <= to; minute++)
            alerts.AddRange(engine.Check(Settings(), OneSession, T0.AddMinutes(minute)));
        return alerts;
    }

    [Fact]
    public void Work_gone_quiet_is_stuck_once_and_rearming_does_not_repeat_it()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|Testing", T0), Activity = new ActivityMark(T0, @"C:\a.log") };
        var engine = new WatchdogEngine(probes);
        Assert.Empty(Minutes(engine, 0, 19));

        var alert = Assert.Single(Minutes(engine, 20, 20));
        Assert.Equal(WatchdogAlertKind.Stuck, alert.Kind);
        Assert.Equal("s1", alert.Session);
        Assert.Contains("50|Testing", alert.Text);
        Assert.Contains(@"C:\a.log", alert.Text);

        Assert.Empty(Minutes(engine, 21, 139));
        Assert.Single(Minutes(engine, 140, 140)); // the gap has passed
    }

    [Fact]
    public void Work_that_moved_and_went_quiet_again_is_a_new_alert()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|Testing", T0), Activity = new ActivityMark(T0, "a") };
        var engine = new WatchdogEngine(probes);
        Assert.Single(Minutes(engine, 0, 20));

        probes.Activity = new ActivityMark(T0.AddMinutes(25), "b");
        probes.ProgressNow = new ProgressMark("60|Testing more", T0.AddMinutes(25));
        Assert.Empty(Minutes(engine, 21, 44));
        Assert.Single(Minutes(engine, 45, 45));
    }

    [Fact]
    public void The_progress_file_changing_counts_as_movement()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|Testing", T0.AddMinutes(10)), Activity = new ActivityMark(T0, "a") };
        var engine = new WatchdogEngine(probes);
        Assert.Empty(Minutes(engine, 0, 25));
    }

    [Fact]
    public void Files_moving_with_progress_unchanged_is_stale_once()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|Testing", T0), Activity = new ActivityMark(T0.AddMinutes(59), "a") };
        var engine = new WatchdogEngine(probes);
        Assert.Empty(Minutes(engine, 0, 59));

        var alert = Assert.Single(Minutes(engine, 60, 60));
        Assert.Equal(WatchdogAlertKind.Stale, alert.Kind);
        Assert.Empty(Minutes(engine, 61, 61));
    }

    [Fact]
    public void Quiet_work_is_stuck_rather_than_also_stale()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|Testing", T0), Activity = new ActivityMark(T0, "a") };
        var alerts = Minutes(new WatchdogEngine(probes), 0, 181);
        Assert.Equal(2, alerts.Count); // at 20 minutes, and again when the repeat gap has passed
        Assert.All(alerts, a => Assert.Equal(WatchdogAlertKind.Stuck, a.Kind));
    }

    [Fact]
    public void A_session_id_fills_in_its_activity_paths()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|x", T0) };
        new WatchdogEngine(probes).Check(Settings(), OneSession, T0);
        Assert.Equal([@"C:\work\s1\files"], probes.AskedPaths);
    }

    [Theory]
    [InlineData("root@192.0.2.10", true)]
    [InlineData("host.example", true)]
    [InlineData("-oProxyCommand=calc", false)]
    [InlineData("root@-oProxyCommand=calc", false)]
    [InlineData("root@host name", false)]
    [InlineData("", false)]
    public void Only_a_plain_ssh_target_is_accepted(string target, bool ok) => Assert.Equal(ok, WatchdogEngine.ValidSshTarget(target));

    [Theory]
    [InlineData("c0e0212d-4e69-48d1-bb89-60618e768c2b", true)]
    [InlineData("..", false)]
    [InlineData(@"..\..\settings", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void A_session_id_can_not_walk_out_of_a_folder(string session, bool ok) => Assert.Equal(ok, WatchdogEngine.ValidSession(session));

    [Fact]
    public void A_stuck_process_is_reported_once_until_it_has_been_gone_a_while()
    {
        var engine = new WatchdogEngine(new FakeProbes());
        var stuck = Stuck(7);
        var alert = Assert.Single(engine.NoteStuck([stuck], T0));
        Assert.Equal(WatchdogAlertKind.Hung, alert.Kind);
        Assert.Contains("pid 7", alert.Text);
        Assert.Empty(engine.NoteStuck([stuck], T0));
        Assert.Empty(engine.NoteStuck([], T0));
        Assert.Empty(engine.NoteStuck([stuck], T0.AddMinutes(1)));
        var later = T0 + WatchdogEngine.LongestStuckWait + WatchdogEngine.HungMemory;
        Assert.Empty(engine.NoteStuck([stuck], later));
        Assert.Empty(engine.NoteStuck([], later.AddMinutes(31)));
        Assert.Single(engine.NoteStuck([stuck], later.AddMinutes(31)));
    }

    [Fact]
    public void A_hung_alert_shows_what_a_long_shell_command_is_actually_running()
    {
        var wrapper = new string('w', 500);
        var shell = Proc(7, "bash.exe", $"bash -c \"source {wrapper} && eval 'python my_real_work.py'\"");
        var agent = new ProcessNode(Proc(1, "claude.exe", "claude"), TimeSpan.Zero, []);
        var stuck = new StuckProcess(new AgentSession(AgentKind.ClaudeCode, agent), new ProcessNode(shell, TimeSpan.Zero, []), StuckKind.Waiting, TimeSpan.FromMinutes(11));
        var text = Assert.Single(new WatchdogEngine(new FakeProbes()).NoteStuck([stuck], T0)).Text;
        Assert.Contains("my_real_work.py", text);
        Assert.True(text.Length < 450);
    }

    private static StuckProcess Stuck(int pid)
    {
        var agent = new ProcessNode(Proc(1, "claude.exe", "claude"), TimeSpan.Zero, []);
        var tool = new ProcessNode(Proc(pid, "bash.exe", "bash -c 'git commit --help'"), TimeSpan.Zero, []);
        return new StuckProcess(new AgentSession(AgentKind.ClaudeCode, agent), tool, StuckKind.Waiting, TimeSpan.FromMinutes(11));
    }

    private static ProcessInfo Proc(int pid, string name, string command) =>
        new(new ProcessKey(pid, 100 + pid), 0, name, null, command, DateTime.UtcNow, 0, 0, 0, [], 0);
}

public class WatchdogHubTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

    private static WatchdogAlert Alert(string text, string? session = null) => new(WatchdogAlertKind.Change, text, session, T0, false);

    private static List<string> Drain(System.Threading.Channels.ChannelReader<WatchdogAlert> reader)
    {
        var all = new List<string>();
        while (reader.TryRead(out var alert)) all.Add(alert.Text);
        return all;
    }

    [Fact]
    public void An_alert_during_the_gap_between_connections_arrives_on_the_next()
    {
        var hub = new WatchdogHub();
        var (first, _) = hub.Connect("s", T0);
        hub.Disconnect("s", first, [], T0);
        hub.Publish(Alert("while away"));
        var (_, reader) = hub.Connect("s", T0);
        Assert.Equal(["while away"], Drain(reader));
    }

    [Fact]
    public void An_alert_published_after_a_connections_last_read_but_before_it_ends_goes_to_the_next()
    {
        var hub = new WatchdogHub();
        var (first, _) = hub.Connect("s", T0);
        hub.Publish(Alert("just in time")); // into the channel, which nothing reads any more
        hub.Disconnect("s", first, [], T0);
        Assert.Equal(["just in time"], Drain(hub.Connect("s", T0).Reader));
    }

    [Fact]
    public void What_a_connection_did_not_send_goes_to_the_next()
    {
        var hub = new WatchdogHub();
        var (first, _) = hub.Connect("s", T0);
        hub.Disconnect("s", first, [Alert("unsent")], T0);
        Assert.Equal(["unsent"], Drain(hub.Connect("s", T0).Reader));
    }

    [Fact]
    public void A_replaced_connection_ends_and_its_unsent_alerts_reach_the_new_one()
    {
        var hub = new WatchdogHub();
        var (first, oldReader) = hub.Connect("s", T0);
        var (_, newReader) = hub.Connect("s", T0);
        Assert.True(oldReader.Completion.IsCompleted);

        hub.Disconnect("s", first, [Alert("unsent")], T0);
        Assert.Equal(["unsent"], Drain(newReader));
        Assert.True(hub.Sessions().Single().Connected); // the old one leaving didn't disconnect the new one
    }

    [Fact]
    public void An_alert_for_one_session_goes_only_to_it()
    {
        var hub = new WatchdogHub();
        var a = hub.Connect("a", T0).Reader;
        var b = hub.Connect("b", T0).Reader;
        hub.Publish(Alert("for a", "a"));
        hub.Publish(Alert("for all"));
        Assert.Equal(["for a", "for all"], Drain(a));
        Assert.Equal(["for all"], Drain(b));
    }

    [Fact]
    public void A_long_gone_session_stops_collecting_alerts()
    {
        var hub = new WatchdogHub();
        var (connection, _) = hub.Connect("s", T0);
        hub.Disconnect("s", connection, [], T0);
        for (var minute = 0; minute <= 64; minute += 2) hub.Prune(TimeSpan.FromMinutes(65), T0.AddMinutes(minute), TimeSpan.FromMinutes(5));
        Assert.Single(hub.Sessions());
        hub.Prune(TimeSpan.FromMinutes(65), T0.AddMinutes(66), TimeSpan.FromMinutes(5));
        Assert.Empty(hub.Sessions());
    }

    [Fact]
    public void A_connected_session_is_never_pruned()
    {
        var hub = new WatchdogHub();
        hub.Connect("s", T0);
        hub.Prune(TimeSpan.FromMinutes(65), T0, TimeSpan.FromMinutes(5));
        hub.Prune(TimeSpan.FromMinutes(65), T0.AddDays(1), TimeSpan.FromDays(2));
        Assert.Single(hub.Sessions());
    }

    [Fact]
    public void Waiting_alerts_are_limited_keeping_the_newest()
    {
        var hub = new WatchdogHub();
        var (connection, _) = hub.Connect("s", T0);
        hub.Disconnect("s", connection, [], T0);
        for (var i = 0; i < WatchdogHub.MaxWaiting + 5; i++) hub.Publish(Alert($"{i}"));
        var got = Drain(hub.Connect("s", T0).Reader);
        Assert.Equal(WatchdogHub.MaxWaiting, got.Count);
        Assert.Equal($"{WatchdogHub.MaxWaiting + 4}", got[^1]);
    }
}

public class WatchdogSocketTests
{
    private const string Token = "secret-token-secret-token-secret-1234";

    private static string Head(string target = $"/watchdog?session=s1&token={Token}", string host = "127.0.0.1:99", string extra = "") =>
        $"GET {target} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: abc=\r\n{extra}\r\n";

    [Fact]
    public void A_good_handshake_is_accepted()
    {
        var (status, session, key) = WatchdogSocket.Judge(Head(), Token, 99);
        Assert.Equal((101, "s1", "abc="), (status, session, key));
    }

    [Fact]
    public void A_web_page_is_refused_by_its_origin() =>
        Assert.Equal(403, WatchdogSocket.Judge(Head(extra: "Origin: https://evil.example\r\n"), Token, 99).Status);

    [Fact]
    public void A_name_pointing_here_from_elsewhere_is_refused() =>
        Assert.Equal(403, WatchdogSocket.Judge(Head(host: "evil.example:99"), Token, 99).Status);

    [Theory]
    [InlineData("/watchdog?session=s1&token=wrong")]
    [InlineData("/watchdog?session=s1")]
    public void A_wrong_or_missing_token_is_refused(string target) => Assert.Equal(401, WatchdogSocket.Judge(Head(target), Token, 99).Status);

    [Theory]
    [InlineData($"/watchdog?token={Token}")]
    [InlineData($"/watchdog?session=..&token={Token}")]
    public void A_bad_session_is_refused(string target) => Assert.Equal(400, WatchdogSocket.Judge(Head(target), Token, 99).Status);

    [Fact]
    public void A_command_the_server_cannot_run_is_no_state()
    {
        Assert.Null(WatchdogProbes.StateFrom(new ToolResult(false, 127, "bash: uptme: command not found")));
        Assert.Null(WatchdogProbes.StateFrom(new ToolResult(false, 126, "bash: ./check: Permission denied")));
        Assert.Null(WatchdogProbes.StateFrom(new ToolResult(false, 255, "ssh: connect to host h port 22: timed out")));
        Assert.Equal("(exit 3) inactive", WatchdogProbes.StateFrom(new ToolResult(false, 3, "inactive")));
        Assert.Equal("up 5 days", WatchdogProbes.StateFrom(new ToolResult(true, 0, "up 5 days")));
    }

    [Fact]
    public void Another_path_is_refused() => Assert.Equal(404, WatchdogSocket.Judge(Head($"/other?session=s1&token={Token}"), Token, 99).Status);

    private static async Task<(ClientWebSocket Client, WatchdogSocket Server, WatchdogHub Hub)> Connected(string session = "s1")
    {
        var hub = new WatchdogHub();
        var server = new WatchdogSocket(hub, Token);
        server.Start(0);
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/watchdog?session={session}&token={Token}"), Timeout());
        return (client, server, hub);
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static async Task<string> Receive(ClientWebSocket client)
    {
        var buffer = new byte[4096];
        var got = await client.ReceiveAsync(buffer, Timeout());
        Assert.Equal(WebSocketMessageType.Text, got.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, got.Count);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task An_alert_reaches_a_connected_client_as_one_text_message()
    {
        var (client, server, hub) = await Connected();
        using (client)
        using (server)
        {
            await WaitUntil(() => hub.Sessions().Count == 1);
            hub.Publish(new WatchdogAlert(WatchdogAlertKind.Server, "WATCHDOG: line one\nline two", null, DateTime.UtcNow, true));
            Assert.Equal("WATCHDOG: line one\nline two", await Receive(client));
        }
    }

    [Fact]
    public async Task Nothing_is_sent_on_connecting_so_arming_wakes_nobody()
    {
        var (client, server, hub) = await Connected();
        using (client)
        using (server)
        {
            await WaitUntil(() => hub.Sessions().Count == 1);
            var buffer = new byte[64];
            var receive = client.ReceiveAsync(buffer, CancellationToken.None);
            Assert.NotEqual(receive, await Task.WhenAny(receive, Task.Delay(500)));
        }
    }

    [Fact]
    public async Task A_client_that_leaves_is_marked_and_its_session_kept()
    {
        var (client, server, hub) = await Connected();
        using (client)
        using (server)
        {
            await WaitUntil(() => hub.Sessions().Count == 1 && hub.Sessions()[0].Connected);
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", Timeout());
            await WaitUntil(() => !hub.Sessions()[0].Connected);
        }
    }

    [Fact]
    public async Task A_second_connection_replaces_the_first_whose_listener_ends()
    {
        var (first, server, hub) = await Connected();
        using (first)
        using (server)
        {
            await WaitUntil(() => hub.Sessions().Count == 1);
            using var second = new ClientWebSocket();
            await second.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/watchdog?session=s1&token={Token}"), Timeout());

            var buffer = new byte[64];
            Assert.Equal(WebSocketMessageType.Close, (await first.ReceiveAsync(buffer, Timeout())).MessageType);
            Assert.Equal("replaced", first.CloseStatusDescription); // not "the app stopped"
            await WaitUntil(() => hub.Sessions().Single().Connected);
            hub.Publish(new WatchdogAlert(WatchdogAlertKind.Change, "to the new one", null, DateTime.UtcNow, false));
            Assert.Equal("to the new one", await Receive(second));
        }
    }

    [Fact]
    public async Task Stopping_the_server_ends_a_clients_listener()
    {
        var (client, server, hub) = await Connected();
        using (client)
        {
            await WaitUntil(() => hub.Sessions().Count == 1);
            server.Dispose();
            var buffer = new byte[64];
            try
            {
                Assert.Equal(WebSocketMessageType.Close, (await client.ReceiveAsync(buffer, Timeout())).MessageType);
            }
            catch (WebSocketException)
            {
                // An abrupt end is an end too.
            }
        }
    }

    [Fact]
    public async Task It_listens_on_loopback_only_and_refuses_a_browser_over_the_wire()
    {
        var hub = new WatchdogHub();
        using var server = new WatchdogSocket(hub, Token);
        server.Start(0);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = tcp.GetStream();
        var request = Head(host: $"127.0.0.1:{server.Port}", extra: "Origin: https://evil.example\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, Timeout());
        Assert.StartsWith("HTTP/1.1 403", Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Empty(hub.Sessions());

        var other = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (other is not null)
        {
            using var outside = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () => await outside.ConnectAsync(other, server.Port));
        }
    }
}

public class WatchdogProbesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ar-wd-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string relative, string text, DateTime whenUtc)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, whenUtc);
        return path;
    }

    private WatchdogProbes Probes() => new(new WatchdogSettings { ProgressDirectory = _dir });

    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_newest_file_is_found_skipping_build_folders_and_links_to_nothing()
    {
        var want = Write(@"a\b\c.txt", "x", Old.AddHours(1));
        Write("older.txt", "x", Old);
        Write(@"node_modules\p\new.js", "x", Old.AddHours(5));
        Write(@".git\index", "x", Old.AddHours(5));
        Write(@"bin\x.dll", "x", Old.AddHours(5));

        var mark = Probes().NewestActivity([_dir, Path.Combine(_dir, "not-there")]);
        Assert.Equal((Old.AddHours(1), want), (mark!.AtUtc, mark.Path));
    }

    [Fact]
    public void A_listeners_own_output_is_not_activity()
    {
        var want = Write("work.log", "x", Old);
        Write("watchdog.log", "x", Old.AddHours(1));
        Write("a.output", "WATCHDOG: STUCK old", Old.AddHours(2));
        Write("empty.output", "", Old.AddHours(3));
        Assert.Equal(want, Probes().NewestActivity([_dir])!.Path);

        var real = Write("test.output", "PASS 12", Old.AddHours(4));
        Assert.Equal(real, Probes().NewestActivity([_dir])!.Path);
    }

    [Fact]
    public void No_paths_or_no_files_means_no_activity()
    {
        Assert.Null(Probes().NewestActivity([]));
        Assert.Null(Probes().NewestActivity([_dir]));
    }

    [Theory]
    [InlineData("100|Done: x", true)]
    [InlineData("100", true)]
    [InlineData(" 100 |Done", true)]
    [InlineData("99|Nearly", false)]
    [InlineData("1000|odd", false)]
    [InlineData("", false)]
    public void Only_exactly_100_is_done(string text, bool done) => Assert.Equal(done, new ProgressMark(text, Old).Done);

    [Fact]
    public void Progress_is_read_for_a_session_and_a_bad_id_reads_nothing()
    {
        Write("s1.txt", "50|Testing\n", Old);
        Write("outside.txt", "100|secret", Old);
        var progress = Probes().Progress("s1");
        Assert.Equal(("50|Testing", Old), (progress!.Text, progress.AtUtc));
        Assert.Null(Probes().Progress("missing"));
        Assert.Null(Probes().Progress(@"..\outside"));
    }

    [Fact]
    public void A_repo_read_notices_a_commit_and_a_file_and_refuses_files_outside_it()
    {
        Git("init", "-q");
        Write("docs.md", "one", Old);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");

        var repo = new WatchedRepo { Path = _dir, Files = ["docs.md", "gone.md", @"..\elsewhere.md"] };
        var before = Probes().RepoState(repo)!;
        Assert.Contains("first", before[0]);
        Assert.Contains("gone.md missing", before);
        Assert.Contains(@"..\elsewhere.md outside the repo", before); // not read, and told as a problem

        Write("docs.md", "two longer", Old);
        var changed = Probes().RepoState(repo)!;
        Assert.NotEqual(before.First(l => l.StartsWith("docs.md")), changed.First(l => l.StartsWith("docs.md")));

        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "second");
        Assert.Contains("second", Probes().RepoState(repo)![0]);
    }

    [Fact]
    public void A_folder_that_is_no_repo_reads_as_unknown_not_as_a_change() =>
        Assert.Null(Probes().RepoState(new WatchedRepo { Path = _dir }));

    [Theory]
    [InlineData("-oProxyCommand=calc", "uptime")]
    [InlineData("root@-oProxyCommand=calc", "uptime")]
    [InlineData("host", "-oProxyCommand=calc")]
    [InlineData("host", "  -v")]
    [InlineData("host", "  ")]
    public void What_ssh_would_read_as_an_option_is_never_passed_to_it(string target, string command) =>
        Assert.Null(WatchdogProbes.SshArguments(new WatchedServer { SshTarget = target, Command = command }));

    [Fact]
    public void Ssh_never_prompts_and_gets_the_target_and_command_as_they_are() =>
        Assert.Equal(["-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "root@h", "echo \"a b\""],
            WatchdogProbes.SshArguments(new WatchedServer { SshTarget = "root@h", Command = "echo \"a b\"" }));

    [Theory]
    [InlineData("")]
    [InlineData("\\")]
    [InlineData("/")]
    public void A_repo_path_ending_in_a_slash_still_watches_its_files(string ending)
    {
        Git("init", "-q");
        Write("docs.md", "one", Old);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");
        var state = Probes().RepoState(new WatchedRepo { Path = _dir + ending, Files = ["docs.md"] })!;
        Assert.Contains(state, l => l.StartsWith("docs.md ") && !l.EndsWith("outside the repo") && !l.EndsWith("missing"));
    }

    [Fact]
    public void A_file_written_a_moment_ago_is_read_next_round_not_now()
    {
        Git("init", "-q");
        Write("docs.md", "one", Old);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(_dir, "docs.md"), "half"); // being written now
        Assert.Null(Probes().RepoState(new WatchedRepo { Path = _dir, Files = ["docs.md"] }));
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "docs.md"), DateTime.UtcNow.AddSeconds(-5));
        Assert.NotNull(Probes().RepoState(new WatchedRepo { Path = _dir, Files = ["docs.md"] }));
    }

    [Fact]
    public void A_folder_or_wildcard_listed_as_a_file_leaves_the_repo_watched()
    {
        Git("init", "-q");
        Write("docs.md", "one", Old);
        Write(Path.Combine("docs", "a.md"), "one", Old);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");
        var state = Probes().RepoState(new WatchedRepo { Path = _dir, Files = ["docs.md", "docs", "*.md"] });
        Assert.NotNull(state); // the commit is still watched
        Assert.Contains("docs is a folder or wildcard, not a file", state);
        Assert.Contains("*.md is a folder or wildcard, not a file", state);

        var engine = new WatchdogEngine(new FakeProbes { Repo = state });
        var problems = engine.Check(new WatchdogSettings { Repos = [new WatchedRepo { Path = _dir }] }, [], DateTime.UtcNow);
        Assert.Equal(2, problems.Count(a => a.Kind == WatchdogAlertKind.Problem));
    }

    [Fact]
    public void A_missing_folder_that_holds_session_folders_is_told()
    {
        var probes = new WatchdogProbes(new WatchdogSettings { ProgressDirectory = _dir });
        Directory.CreateDirectory(Path.Combine(_dir, "sessions"));
        Assert.Empty(probes.SetupProblems(new WatchdogSettings { ActivityPaths = [Path.Combine(_dir, "sessions", "{session}")] }));
        Assert.Single(probes.SetupProblems(new WatchdogSettings { ActivityPaths = [Path.Combine(_dir, "sesions", "{session}")] }));
        Assert.Single(probes.SetupProblems(new WatchdogSettings { ActivityPaths = [Path.Combine(_dir, "sesions", "run-{session}")] }));
    }

    [Fact]
    public void A_bad_activity_path_does_not_hide_the_others()
    {
        var want = Write("work.log", "x", Old);
        Assert.Equal(want, Probes().NewestActivity([_dir, "", "bad\0path"])!.Path);
    }

    [Fact]
    public void A_work_file_that_merely_starts_with_watchdog_counts()
    {
        var want = Write("WatchdogTests.cs", "x", Old.AddHours(1));
        Write("watchdog.log", "x", Old.AddHours(2));
        Assert.Equal(want, Probes().NewestActivity([_dir])!.Path);
    }

    private void Git(params string[] args)
    {
        var result = Cleanup.ToolRunner.Run("git", ["-C", _dir, .. args], TimeSpan.FromSeconds(30));
        Assert.True(result.Success, result.Output);
    }
}

public class WatchdogFindingsTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] OneSession = ["s1"];

    private static WatchdogSettings Settings(bool repo = false, bool server = false) => new()
    {
        ServerEveryChecks = 1,
        Repos = repo ? [new WatchedRepo { Path = "repo" }] : [],
        Servers = server ? [new WatchedServer { Name = "home", SshTarget = "root@h", Notify = true }] : [],
    };

    [Fact]
    public void A_server_that_never_answered_is_reported_once_after_two_reads()
    {
        var probes = new FakeProbes { Server = null };
        var engine = new WatchdogEngine(probes);
        Assert.Empty(engine.Check(Settings(server: true), [], T0));
        var alert = Assert.Single(engine.Check(Settings(server: true), [], T0));
        Assert.Equal(WatchdogAlertKind.Problem, alert.Kind);
        Assert.True(alert.Notify);
        Assert.Contains("never answered", alert.Text);
        Assert.Empty(engine.Check(Settings(server: true), [], T0));
    }

    [Fact]
    public void A_first_failed_read_then_an_answer_is_not_reported()
    {
        var probes = new FakeProbes { Server = null };
        var engine = new WatchdogEngine(probes);
        Assert.Empty(engine.Check(Settings(server: true), [], T0));
        probes.Server = "boot=1";
        Assert.Empty(engine.Check(Settings(server: true), [], T0));
        Assert.Equal("boot=1", engine.Memory.Servers["home"]);
    }

    [Fact]
    public void Unusable_server_settings_are_named()
    {
        var probes = new FakeProbes { Server = null };
        var engine = new WatchdogEngine(probes);
        var settings = new WatchdogSettings { ServerEveryChecks = 1, Servers = [new WatchedServer { Name = "x", SshTarget = "-oBad", Notify = false }] };
        var alert = Assert.Single(engine.Check(settings, [], T0));
        Assert.Contains("-oBad", alert.Text);
        Assert.True(alert.Notify); // it's the user's setting, whatever the server's Notify says
        Assert.Empty(engine.Check(settings, [], T0));
        Assert.Equal(0, probes.ServerReads);
    }

    [Fact]
    public void A_server_command_that_fails_is_told_once_and_still_watched()
    {
        var probes = new FakeProbes { Server = "(exit 1) uptime: illegal option -- s" };
        var engine = new WatchdogEngine(probes);
        var settings = new WatchdogSettings { ServerEveryChecks = 4, Servers = [new WatchedServer { Name = "mac", SshTarget = "me@mac" }] };
        var told = 0;
        for (var round = 0; round < 12; round++)
            told += engine.Check(settings, [], T0.AddMinutes(round), scheduled: round % 3 != 2).Count(a => a.Kind == WatchdogAlertKind.Problem);
        Assert.Equal(1, told);
        Assert.Equal("(exit 1) uptime: illegal option -- s", engine.Memory.Servers["mac"]);
    }

    [Fact]
    public void A_server_made_unusable_after_it_answered_is_told_once()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        var settings = new WatchdogSettings { ServerEveryChecks = 4, Servers = [new WatchedServer { Name = "home", SshTarget = "root@h" }] };
        Assert.Empty(engine.Check(settings, [], T0));
        Assert.True(engine.Memory.Servers.ContainsKey("home"));

        settings.Servers[0].SshTarget = "-oBad"; // edited, same name
        var told = 0;
        for (var round = 0; round < 16; round++)
            told += engine.Check(settings, [], T0.AddMinutes(round), scheduled: round % 2 == 0).Count(a => a.Kind == WatchdogAlertKind.Problem);
        Assert.Equal(1, told);
        Assert.False(engine.Memory.Servers.ContainsKey("home")); // its old reading isn't shown as current
    }

    [Fact]
    public void A_repo_that_stays_unreadable_is_reported_once_and_again_after_it_recovered()
    {
        var probes = new FakeProbes { Repo = null };
        var engine = new WatchdogEngine(probes);
        for (var i = 1; i < WatchdogEngine.RepoFailuresBeforeAlert; i++) Assert.Empty(engine.Check(Settings(repo: true), [], T0));
        var alert = Assert.Single(engine.Check(Settings(repo: true), [], T0));
        Assert.Equal(WatchdogAlertKind.Problem, alert.Kind);
        Assert.True(alert.Notify);
        Assert.Empty(engine.Check(Settings(repo: true), [], T0));

        probes.Repo = ["commit a"];
        Assert.Empty(engine.Check(Settings(repo: true), [], T0));
        probes.Repo = null;
        for (var i = 1; i < WatchdogEngine.RepoFailuresBeforeAlert; i++) Assert.Empty(engine.Check(Settings(repo: true), [], T0));
        Assert.Single(engine.Check(Settings(repo: true), [], T0));
    }

    private sealed class Throwing(IWatchdogProbes inner, string which) : IWatchdogProbes
    {
        public IReadOnlyList<string>? RepoState(WatchedRepo repo) => which == "repo" ? throw new InvalidOperationException("boom") : inner.RepoState(repo);
        public string? ServerState(WatchedServer server) => which == "server" ? throw new InvalidOperationException("boom") : inner.ServerState(server);
        public ActivityMark? NewestActivity(IReadOnlyList<string> paths) => which == "activity" ? throw new ArgumentException("boom") : inner.NewestActivity(paths);
        public ProgressMark? Progress(string session) => inner.Progress(session);
    }

    [Fact]
    public void A_failure_later_in_a_round_keeps_the_alerts_already_decided()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|x", T0) };
        var engine = new WatchdogEngine(new Throwing(probes, "activity"));
        engine.Check(Settings(repo: true), OneSession, T0);
        probes.Repo = ["commit b"];
        var alert = Assert.Single(engine.Check(Settings(repo: true), OneSession, T0));
        Assert.Equal(WatchdogAlertKind.Change, alert.Kind);
    }

    [Fact]
    public void A_failure_on_one_server_does_not_stop_the_next_check()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(new Throwing(probes, "server"));
        engine.Check(Settings(repo: true, server: true), [], T0);
        probes.Repo = ["commit b"];
        Assert.Single(engine.Check(Settings(repo: true, server: true), [], T0));
    }

    [Fact]
    public void Work_is_not_stuck_just_because_the_PC_slept()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|x", T0), Activity = new ActivityMark(T0, "a") };
        var engine = new WatchdogEngine(probes);
        Assert.Empty(engine.Check(Settings(), OneSession, T0.AddMinutes(2)));

        var woke = T0.AddHours(8);
        Assert.Empty(engine.Check(Settings(), OneSession, woke));
        Assert.Empty(engine.Check(Settings(), OneSession, woke.AddMinutes(2)));
        // Still quiet long after waking: that's real. Checks keep coming, so this isn't another gap.
        for (var minutes = 4; minutes < 20; minutes += 2) Assert.Empty(engine.Check(Settings(), OneSession, woke.AddMinutes(minutes)));
        Assert.Equal(WatchdogAlertKind.Stuck, Assert.Single(engine.Check(Settings(), OneSession, woke.AddMinutes(20))).Kind);
    }

    [Fact]
    public void Work_is_not_judged_when_there_were_too_many_files_to_look_through()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|x", T0), Activity = new ActivityMark(T0, "a", Complete: false) };
        var engine = new WatchdogEngine(probes);
        var settings = Settings();
        settings.CheckSeconds = 3600;
        var told = Assert.Single(engine.Check(settings, OneSession, T0));
        Assert.Equal(WatchdogAlertKind.Problem, told.Kind); // said once, so it isn't silently unwatched
        Assert.Empty(engine.Check(settings, OneSession, T0.AddMinutes(40)));
    }

    [Fact]
    public void Editing_a_repos_file_list_is_not_a_change_in_the_repo()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        engine.Check(Settings(repo: true), [], T0);
        var edited = Settings(repo: true);
        edited.Repos[0].Files = ["docs.md"];
        probes.Repo = ["commit a one", "docs.md ABC"];
        Assert.Empty(engine.Check(edited, [], T0));
        probes.Repo = ["commit a one", "docs.md DEF"];
        Assert.Single(engine.Check(edited, [], T0));
    }

    [Fact]
    public void What_is_no_longer_watched_is_forgotten()
    {
        var engine = new WatchdogEngine(new FakeProbes());
        engine.Check(Settings(repo: true, server: true), [], T0);
        engine.Check(Settings(), [], T0);
        Assert.Empty(engine.Memory.Repos);
        Assert.Empty(engine.Memory.Servers);
    }

    [Theory]
    [InlineData("abc\n")]
    public void A_trailing_newline_is_not_a_valid_id(string text)
    {
        Assert.False(WatchdogEngine.ValidSession(text));
        Assert.False(WatchdogEngine.ValidSshTarget(text));
    }

    private static StuckProcess StuckCommand(int pid, string commandLine)
    {
        ProcessInfo Proc(int id, string name, string line) => new(new ProcessKey(id, 100 + id), 0, name, null, line, T0, 0, 0, 0, [], 0);
        var agent = new ProcessNode(Proc(1, "claude.exe", "claude"), TimeSpan.Zero, []);
        return new StuckProcess(new AgentSession(AgentKind.ClaudeCode, agent), new ProcessNode(Proc(pid, "python.exe", commandLine), TimeSpan.Zero, []),
            StuckKind.Spinning, TimeSpan.FromMinutes(16));
    }

    [Fact]
    public void A_stuck_command_whose_cpu_dips_for_a_poll_is_not_reported_again()
    {
        var engine = new WatchdogEngine(new FakeProbes());
        var busy = StuckCommand(9, "python train.py");
        Assert.Single(engine.NoteStuck([busy], T0));
        Assert.Empty(engine.NoteStuck([], T0.AddSeconds(3)));
        Assert.Empty(engine.NoteStuck([busy], T0.AddSeconds(6)));
        Assert.Empty(engine.NoteStuck([busy], T0.AddMinutes(5)));
        var later = T0 + WatchdogEngine.LongestStuckWait + WatchdogEngine.HungMemory;
        Assert.Empty(engine.NoteStuck([busy], later));
        Assert.Empty(engine.NoteStuck([], later.AddSeconds(3))); // a dip once forgetting applies
        Assert.Empty(engine.NoteStuck([busy], later.AddSeconds(6)));
        Assert.Empty(engine.NoteStuck([], later.AddMinutes(31)));
        Assert.Single(engine.NoteStuck([busy], later.AddMinutes(32))); // gone long enough to be news again
    }

    [Fact]
    public void A_stuck_command_already_told_is_not_told_again_after_a_restart()
    {
        var busy = StuckCommand(9, "python train.py");
        var memory = new WatchdogMemory();
        var first = new WatchdogEngine(new FakeProbes(), memory);
        Assert.Single(first.NoteStuck([busy], T0));
        memory.HungSeen = first.HungSnapshot();
        Assert.Empty(new WatchdogEngine(new FakeProbes(), SavedAndLoaded(memory)).NoteStuck([busy], T0.AddMinutes(1)));
    }

    [Fact]
    public void A_stuck_command_is_not_told_again_after_a_long_close()
    {
        var busy = StuckCommand(9, "python train.py");
        var memory = new WatchdogMemory();
        var first = new WatchdogEngine(new FakeProbes(), memory);
        Assert.Single(first.NoteStuck([busy], T0));
        memory.HungSeen = first.HungSnapshot();

        // Closed for an hour. After starting, the process check judges it afresh, which takes its stuck time.
        var restarted = new WatchdogEngine(new FakeProbes(), SavedAndLoaded(memory));
        var start = T0.AddHours(1);
        for (var minute = 0; minute < 30; minute++) Assert.Empty(restarted.NoteStuck([], start.AddMinutes(minute)));
        Assert.Empty(restarted.NoteStuck([busy], start.AddMinutes(30)));
    }

    [Fact]
    public void A_hung_alert_hides_secrets_in_the_command_line()
    {
        var text = Assert.Single(new WatchdogEngine(new FakeProbes()).NoteStuck([StuckCommand(9, "python up.py --api-key=sk-live-123456")], T0)).Text;
        Assert.DoesNotContain("sk-live-123456", text);
    }

    [Fact]
    public void Stuck_and_stale_taking_turns_stay_within_the_repeat_limit()
    {
        // A job that writes a file every 25 minutes while its progress never moves.
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|x", T0) };
        var engine = new WatchdogEngine(probes);
        var alerts = new List<WatchdogAlert>();
        for (var minute = 0; minute <= 240; minute++)
        {
            probes.Activity = new ActivityMark(T0.AddMinutes(minute / 25 * 25), "a");
            alerts.AddRange(engine.Check(Settings(), OneSession, T0.AddMinutes(minute)));
        }
        Assert.InRange(alerts.Count(a => a.Kind == WatchdogAlertKind.Stuck), 1, 2);
        Assert.InRange(alerts.Count(a => a.Kind == WatchdogAlertKind.Stale), 1, 2);
    }

    [Fact]
    public void Stale_soon_after_stuck_is_still_told_since_each_kind_has_its_own_repeat()
    {
        // Quiet long enough to be stuck, then files move while the progress still doesn't.
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|x", T0) };
        var engine = new WatchdogEngine(probes);
        var alerts = new List<WatchdogAlert>();
        for (var minute = 0; minute <= 100; minute++)
        {
            probes.Activity = new ActivityMark(minute < 30 ? T0 : T0.AddMinutes(minute), "a");
            alerts.AddRange(engine.Check(Settings(), OneSession, T0.AddMinutes(minute)));
        }
        Assert.Contains(alerts, a => a.Kind == WatchdogAlertKind.Stuck);
        Assert.Contains(alerts, a => a.Kind == WatchdogAlertKind.Stale); // within RepeatMinutes of the stuck one
    }

    [Fact]
    public void A_session_that_just_connected_is_given_time_before_it_is_called_stuck()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|asked the user", T0.AddHours(-5)), Activity = new ActivityMark(T0.AddHours(-5), "a") };
        var engine = new WatchdogEngine(probes);
        for (var minute = 0; minute < 30; minute++) Assert.Empty(engine.Check(Settings(), [], T0.AddMinutes(minute)));
        for (var minute = 30; minute < 50; minute++) Assert.Empty(engine.Check(Settings(), OneSession, T0.AddMinutes(minute)));
        Assert.Single(engine.Check(Settings(), OneSession, T0.AddMinutes(50)));
    }

    [Fact]
    public void A_round_run_early_for_a_settings_change_does_not_read_servers_again()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        engine.Check(Settings(server: true), [], T0);
        engine.Check(Settings(server: true), [], T0.AddSeconds(1), scheduled: false);
        Assert.Equal(1, probes.ServerReads);
    }

    [Fact]
    public void An_early_round_does_not_confirm_that_a_server_never_answered()
    {
        var probes = new FakeProbes { Server = null };
        var engine = new WatchdogEngine(probes);
        Assert.Empty(engine.Check(Settings(server: true), [], T0));
        Assert.Empty(engine.Check(Settings(server: true), [], T0.AddSeconds(5), scheduled: false));
        Assert.Equal(1, probes.ServerReads);
    }

    [Fact]
    public void A_lasting_problem_is_not_told_again_after_a_restart()
    {
        var probes = new FakeProbes { Setup = ["the progress folder X doesn't exist"] };
        var memory = new WatchdogMemory();
        Assert.Single(new WatchdogEngine(probes, memory).Check(Settings(), [], T0));
        Assert.Empty(new WatchdogEngine(probes, SavedAndLoaded(memory)).Check(Settings(), [], T0));
    }

    [Fact]
    public void One_failed_read_does_not_make_a_lasting_problem_be_told_again()
    {
        var probes = new FakeProbes { Repo = ["commit a one", "docs/STATUS.md missing"] };
        var engine = new WatchdogEngine(probes);
        Assert.Single(engine.Check(Settings(repo: true), [], T0));
        probes.Repo = null; // git timed out once
        Assert.Empty(engine.Check(Settings(repo: true), [], T0));
        probes.Repo = ["commit a one", "docs/STATUS.md missing"];
        Assert.Empty(engine.Check(Settings(repo: true), [], T0));
    }

    [Fact]
    public void An_unreadable_repo_is_not_told_again_after_a_restart()
    {
        var probes = new FakeProbes { Repo = null };
        var memory = new WatchdogMemory();
        var first = new WatchdogEngine(probes, memory);
        for (var i = 1; i < WatchdogEngine.RepoFailuresBeforeAlert; i++) first.Check(Settings(repo: true), [], T0);
        Assert.Single(first.Check(Settings(repo: true), [], T0));
        var restarted = new WatchdogEngine(probes, SavedAndLoaded(memory));
        for (var i = 0; i < 6; i++) Assert.Empty(restarted.Check(Settings(repo: true), [], T0));
    }

    [Fact]
    public void Stuck_is_not_told_again_after_a_restart_within_the_repeat_time()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|x", T0), Activity = new ActivityMark(T0, "a") };
        var memory = new WatchdogMemory();
        var first = new WatchdogEngine(probes, memory);
        Assert.Single(Enumerable.Range(0, 23).SelectMany(m => first.Check(Settings(), OneSession, T0.AddMinutes(m))).ToList());
        var restarted = new WatchdogEngine(probes, SavedAndLoaded(memory));
        var alerts = new List<WatchdogAlert>();
        for (var minute = 23; minute < 139; minute++) alerts.AddRange(restarted.Check(Settings(), OneSession, T0.AddMinutes(minute)));
        Assert.Empty(alerts);
    }

    [Fact]
    public void A_misspelt_setting_is_told_rather_than_ignored()
    {
        var json = """{ "Watchdog": { "Enabled": true, "repos": [{ "Path": "C:\\x" }], "Servers": [{ "Name": "h", "SshTarget": "h", "Comand": "uptime" }] } }""";
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        var alerts = new WatchdogEngine(new FakeProbes()).Check(loaded.Watchdog, [], T0);
        Assert.Contains(alerts, a => a.Kind == WatchdogAlertKind.Problem && a.Text.Contains("\"repos\""));
        Assert.Contains(alerts, a => a.Kind == WatchdogAlertKind.Problem && a.Text.Contains("\"Comand\""));
    }

    [Fact]
    public void A_server_read_before_the_PC_slept_is_not_confirmed_by_one_after()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        engine.Check(Settings(server: true), [], T0);
        probes.Server = null;
        Assert.Empty(engine.Check(Settings(server: true), [], T0.AddMinutes(2))); // just before sleeping
        Assert.Empty(engine.Check(Settings(server: true), [], T0.AddHours(8))); // waking, network not up yet
        probes.Server = "boot=1";
        Assert.Empty(engine.Check(Settings(server: true), [], T0.AddHours(8).AddMinutes(2)));
    }

    [Fact]
    public void Editing_a_servers_command_is_not_a_change_on_the_server()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        engine.Check(Settings(server: true), [], T0);
        var edited = Settings(server: true);
        edited.Servers[0].Command = "docker ps";
        probes.Server = "web up";
        Assert.Empty(engine.Check(edited, [], T0));
        Assert.Empty(engine.Check(edited, [], T0));
        Assert.Equal("web up", engine.Memory.Servers["home"]);
    }

    [Fact]
    public void Settings_that_point_at_nothing_are_told_once_and_again_if_they_come_back()
    {
        var probes = new FakeProbes { Setup = ["the progress folder X doesn't exist"] };
        var engine = new WatchdogEngine(probes);
        var alert = Assert.Single(engine.Check(Settings(), [], T0));
        Assert.Equal(WatchdogAlertKind.Problem, alert.Kind);
        Assert.True(alert.Notify);
        Assert.Empty(engine.Check(Settings(), [], T0));
        probes.Setup = [];
        for (var i = 0; i < WatchdogEngine.RoundsBeforeProblemGone; i++) Assert.Empty(engine.Check(Settings(), [], T0)); // fixed for good
        probes.Setup = ["the progress folder X doesn't exist"];
        Assert.Single(engine.Check(Settings(), [], T0));
    }

    [Fact]
    public void A_watched_file_that_is_missing_is_told()
    {
        var probes = new FakeProbes { Repo = ["commit a one", "docs/STATU.md missing"] };
        Assert.Contains("STATU.md", Assert.Single(new WatchdogEngine(probes).Check(Settings(repo: true), [], T0)).Text);
    }

    [Fact]
    public void Two_servers_with_one_name_are_told()
    {
        var settings = Settings(server: true);
        settings.Servers.Add(new WatchedServer { Name = "home", SshTarget = "other" });
        var alerts = new WatchdogEngine(new FakeProbes()).Check(settings, [], T0);
        Assert.Contains(alerts, a => a.Kind == WatchdogAlertKind.Problem && a.Text.Contains("home"));
    }

    [Fact]
    public void Missing_pieces_in_settings_do_not_stop_the_rest_being_watched()
    {
        var probes = new FakeProbes();
        var engine = new WatchdogEngine(probes);
        var settings = new WatchdogSettings
        {
            ServerEveryChecks = 1,
            ActivityPaths = null!,
            Repos = [new WatchedRepo { Path = "broken", Files = null! }, null!, new WatchedRepo { Path = "repo" }],
            Servers = [new WatchedServer { Name = "s", SshTarget = null!, Command = null! }, null!],
        };
        engine.Check(settings, OneSession, T0);
        probes.Repo = ["commit b"];
        Assert.Equal(2, engine.Check(settings, OneSession, T0).Count(a => a.Kind == WatchdogAlertKind.Change));
    }

    private static WatchdogAlert Global(string text) => new(WatchdogAlertKind.Change, text, null, T0, false);

    /// <summary>Through the status file, the way a restart does it: only what's really saved survives.</summary>
    private static WatchdogMemory SavedAndLoaded(WatchdogMemory memory)
    {
        var file = Path.Combine(Path.GetTempPath(), $"wd-{Guid.NewGuid():N}.json");
        try
        {
            new WatchdogStatus { Memory = WatchdogService.CopyOf(memory) }.Save(file);
            return WatchdogStatus.Load(file)!.Memory;
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Too_many_files_is_one_problem_however_many_sessions_and_stays_told_while_they_rest()
    {
        var probes = new FakeProbes { ProgressNow = new ProgressMark("50|x", T0), Activity = new ActivityMark(T0, "a", Complete: false) };
        var engine = new WatchdogEngine(probes);
        string[] two = ["s1", "s2"];
        Assert.Single(engine.Check(Settings(), two, T0));
        probes.ProgressNow = new ProgressMark("100|done", T0);
        for (var i = 0; i < 5; i++) Assert.Empty(engine.Check(Settings(), two, T0));
        probes.ProgressNow = new ProgressMark("0|next task", T0);
        Assert.Empty(engine.Check(Settings(), two, T0));
    }

    private static List<string> Drain(System.Threading.Channels.ChannelReader<WatchdogAlert> reader)
    {
        var all = new List<string>();
        while (reader.TryRead(out var alert)) all.Add(alert.Text);
        return all;
    }

    [Fact]
    public void An_alert_with_nobody_to_tell_goes_to_whoever_connects_next_once()
    {
        var hub = new WatchdogHub();
        hub.Publish(Global("nobody here"));
        Assert.Equal(["nobody here"], Drain(hub.Connect("a", T0).Reader));
        Assert.Empty(Drain(hub.Connect("b", T0).Reader));
    }

    [Fact]
    public void A_session_alert_with_no_such_session_is_dropped()
    {
        var hub = new WatchdogHub();
        hub.Publish(new WatchdogAlert(WatchdogAlertKind.Stuck, "for s", "s", T0, false));
        Assert.Empty(Drain(hub.Connect("a", T0).Reader));
    }

    private static void Awake(WatchdogHub hub, int fromMinute, int toMinute)
    {
        for (var minute = fromMinute; minute <= toMinute; minute += 2)
            hub.Prune(TimeSpan.FromMinutes(65), T0.AddMinutes(minute), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Sleeping_does_not_end_a_session_or_lose_what_waits_for_it()
    {
        var hub = new WatchdogHub();
        var (connection, _) = hub.Connect("s", T0);
        hub.Disconnect("s", connection, [], T0);
        hub.Publish(Global("while away"));
        hub.Publish(new WatchdogAlert(WatchdogAlertKind.Stuck, "for s", "s", T0, false));
        Awake(hub, 0, 10);
        hub.Prune(TimeSpan.FromMinutes(65), T0.AddHours(8), TimeSpan.FromMinutes(5)); // woke up
        Awake(hub, 482, 490);
        Assert.Single(hub.Sessions());
        Assert.Equal(["while away", "for s"], Drain(hub.Connect("s", T0.AddHours(8)).Reader));
    }

    [Fact]
    public void Two_ended_sessions_holding_one_alert_pass_it_on_once()
    {
        var hub = new WatchdogHub();
        foreach (var s in new[] { "a", "b" })
        {
            var (connection, _) = hub.Connect(s, T0);
            hub.Disconnect(s, connection, [], T0);
        }
        hub.Publish(Global("one"));
        Awake(hub, 0, 70);
        Assert.Empty(hub.Sessions());
        Assert.Equal(["one"], Drain(hub.Connect("c", T0.AddHours(8)).Reader));
    }

    [Fact]
    public void An_ended_session_does_not_hand_on_what_a_live_one_already_had()
    {
        var hub = new WatchdogHub();
        var (old, _) = hub.Connect("ended", T0);
        hub.Disconnect("ended", old, [], T0);
        var live = hub.Connect("live", T0).Reader;
        hub.Publish(Global("seen by live"));
        Assert.Equal(["seen by live"], Drain(live));
        Awake(hub, 0, 70);
        Assert.Empty(Drain(hub.Connect("next", T0.AddMinutes(70)).Reader));
    }

    [Fact]
    public void A_new_session_gets_what_only_an_ended_one_was_holding()
    {
        var hub = new WatchdogHub();
        var (old, _) = hub.Connect("ended", T0);
        hub.Disconnect("ended", old, [], T0);
        hub.Publish(Global("nobody live"));
        Assert.Equal(["nobody live"], Drain(hub.Connect("new", T0).Reader));
        Assert.Empty(Drain(hub.Connect("newer", T0).Reader)); // once is enough
    }

    [Fact]
    public async Task What_was_being_sent_when_a_connection_failed_is_kept_with_what_came_after()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<WatchdogAlert>();
        foreach (var text in new[] { "sent", "in flight", "after" }) channel.Writer.TryWrite(Global(text));
        channel.Writer.TryComplete();
        var sent = new List<string>();
        var unsent = new List<WatchdogAlert>();
        await WatchdogSocket.SendAll(channel.Reader, (alert, _) =>
        {
            if (alert.Text == "in flight") throw new IOException("connection reset");
            sent.Add(alert.Text);
            return Task.CompletedTask;
        }, unsent, CancellationToken.None);
        Assert.Equal(["sent"], sent);
        Assert.Equal(["in flight", "after"], unsent.Select(a => a.Text));
    }

    [Fact]
    public void Waiting_alerts_survive_a_restart_of_the_app()
    {
        var hub = new WatchdogHub();
        hub.Publish(Global("unclaimed"));
        var other = new WatchdogHub();
        var (connection, _) = other.Connect("s", T0);
        other.Disconnect("s", connection, [], T0);
        other.Publish(Global("waiting"));

        var restarted = new WatchdogHub();
        restarted.Restore(hub.Mail());
        restarted.Restore(other.Mail());
        Assert.Equal(["unclaimed", "waiting"], Drain(restarted.Connect("s", T0).Reader));
        Assert.Empty(restarted.Mail().Unclaimed);
        Assert.Empty(restarted.Mail().Waiting);
    }

    [Fact]
    public void After_a_restart_an_alert_one_session_had_is_not_given_to_it_again()
    {
        var hub = new WatchdogHub();
        var (b, _) = hub.Connect("b", T0);
        hub.Disconnect("b", b, [], T0);
        var a = hub.Connect("a", T0).Reader;
        hub.Publish(Global("x"));
        Assert.Equal(["x"], Drain(a));

        var restarted = new WatchdogHub();
        restarted.Restore(hub.Mail());
        Assert.Empty(Drain(restarted.Connect("a", T0).Reader));
        Assert.Equal(["x"], Drain(restarted.Connect("b", T0).Reader)); // b never had it
    }

    [Fact]
    public void A_session_never_gets_one_alert_twice_when_it_is_both_unclaimed_and_queued()
    {
        var hub = new WatchdogHub();
        var (a, _) = hub.Connect("a", T0);
        hub.Disconnect("a", a, [], T0);
        var (b, _) = hub.Connect("b", T0);
        Awake(hub, 0, 20);
        hub.Disconnect("b", b, [], T0.AddMinutes(20));
        hub.Publish(Global("x")); // waits for both
        Awake(hub, 22, 66); // a has been away 66 minutes and is pruned, handing x on; b only 46
        Assert.Equal([("b", false)], hub.Sessions());
        Assert.Equal(["x"], Drain(hub.Connect("b", T0.AddMinutes(66)).Reader));
    }

    [Fact]
    public void An_alert_a_failed_connection_never_sent_still_reaches_someone_if_that_session_ends()
    {
        var hub = new WatchdogHub();
        var (s, _) = hub.Connect("s", T0);
        hub.Publish(Global("never got there"));
        hub.Disconnect("s", s, [Global("never got there")], T0); // the send failed
        Awake(hub, 0, 70); // s doesn't come back
        Assert.Empty(hub.Sessions());
        Assert.Equal(["never got there"], Drain(hub.Connect("next", T0.AddMinutes(70)).Reader));
    }

    [Fact]
    public void One_listener_lapsing_with_an_alert_unsent_does_not_undo_another_having_sent_it()
    {
        var hub = new WatchdogHub();
        var (a, _) = hub.Connect("a", T0);
        var b = hub.Connect("b", T0);
        hub.Publish(Global("x"));
        Assert.Equal(["x"], Drain(b.Reader)); // b sent it
        hub.Disconnect("a", a, [Global("x")], T0); // a lapsed with it unsent, and never comes back
        hub.Disconnect("b", b.Connection, [], T0.AddMinutes(30));
        Awake(hub, 0, 70);
        Assert.Empty(Drain(hub.Connect("b", T0.AddMinutes(70)).Reader));
    }

    [Fact]
    public void An_alert_a_failed_connection_never_sent_goes_to_a_new_session_too()
    {
        var hub = new WatchdogHub();
        var (s, _) = hub.Connect("s", T0);
        hub.Publish(Global("never got there"));
        hub.Disconnect("s", s, [Global("never got there")], T0);
        Assert.Equal(["never got there"], Drain(hub.Connect("other", T0).Reader));
    }

    [Fact]
    public void Closing_puts_what_a_connection_had_not_sent_back_in_its_queue_at_once()
    {
        var hub = new WatchdogHub();
        hub.Connect("s", T0);
        hub.Publish(Global("not sent yet"));
        hub.CloseAll();
        var restarted = new WatchdogHub();
        restarted.Restore(hub.Mail());
        Assert.Equal(["not sent yet"], Drain(restarted.Connect("s", T0).Reader));
    }

    [Fact]
    public void A_bad_session_name_in_the_saved_file_is_ignored()
    {
        var hub = new WatchdogHub();
        hub.Restore(new WatchdogMail { Waiting = new() { [@"..\x"] = [Global("a")] } });
        Assert.Empty(hub.Sessions());
    }
}

public class WatchdogServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ar-wds-").FullName;
    private string File_ => Path.Combine(_dir, "watchdog.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AppSettings On(params WatchedServer[] servers) => new()
    {
        Watchdog = new WatchdogSettings { Enabled = true, Port = 0, CheckSeconds = 1, ServerEveryChecks = 1, Servers = servers.ToList() },
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_server_change_reaches_a_connected_agent_and_the_user_and_is_saved()
    {
        var probes = new FakeProbes();
        var notified = new List<WatchdogAlert>();
        var settings = On(new WatchedServer { Name = "home", SshTarget = "h", Notify = true });
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, probes);
        service.Start();

        await WaitUntil(() => WatchdogStatus.Load(File_) is { Port: > 0, LastCheckUtc: not null });
        var status = WatchdogStatus.Load(File_)!;
        Assert.True(status.IsCurrent(DateTime.UtcNow));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(status.Url.Replace("{session}", "s1")), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        probes.Server = "boot=2";
        var buffer = new byte[1024];
        var got = await client.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        Assert.Contains("now: boot=2", Encoding.UTF8.GetString(buffer, 0, got.Count));

        await WaitUntil(() => { lock (notified) return notified.Count == 1; });
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.Memory.Servers.GetValueOrDefault("home") == "boot=2");
        Assert.Contains(WatchdogStatus.Load(File_)!.RecentAlerts, a => a.Kind == WatchdogAlertKind.Server && a.Notify);
    }

    [Fact]
    public async Task A_server_set_not_to_notify_stays_out_of_the_users_way()
    {
        var probes = new FakeProbes();
        var notified = 0;
        var settings = On(new WatchedServer { Name = "home", SshTarget = "h", Notify = false });
        using var service = new WatchdogService(() => settings, File_, _ => Interlocked.Increment(ref notified), probes);
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_)?.LastCheckUtc is not null);
        probes.Server = "boot=2";
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.Memory.Servers.GetValueOrDefault("home") == "boot=2");
        Assert.Equal(0, notified);
    }

    [Fact]
    public async Task The_token_and_what_was_known_survive_a_restart()
    {
        var settings = On(new WatchedServer { Name = "home", SshTarget = "h", Notify = false });
        string token;
        using (var service = new WatchdogService(() => settings, File_, null, new FakeProbes()))
        {
            service.Start();
            await WaitUntil(() => WatchdogStatus.Load(File_)?.Memory.Servers.Count == 1);
            token = WatchdogStatus.Load(File_)!.Token;
        }
        Assert.True(token.Length >= 32);

        using var again = new WatchdogService(() => settings, File_, null, new FakeProbes { Server = "boot=2" });
        again.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.RecentAlerts.Count == 1);
        Assert.Equal(token, WatchdogStatus.Load(File_)!.Token);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Off_by_default_it_probes_nothing_and_opens_no_port()
    {
        Assert.False(new AppSettings().Watchdog.Enabled);
        var probes = new FakeProbes();
        var port = FreePort();
        var settings = new AppSettings { Watchdog = new WatchdogSettings { Port = port, CheckSeconds = 1, Servers = [new WatchedServer { Name = "h", SshTarget = "h" }] } };
        using var service = new WatchdogService(() => settings, File_, null, probes);
        service.Start();
        await Task.Delay(2500);
        Assert.Equal(0, probes.ServerReads);
        service.Dispose(); // closing too
        Assert.False(File.Exists(File_)); // no file, and no token made
        using var refused = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await refused.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Changing_a_setting_takes_effect_at_once_even_after_rounds_have_timed_out()
    {
        var settings = On();
        settings.Watchdog.CheckSeconds = 1;
        using var service = new WatchdogService(() => settings, File_, null, new FakeProbes());
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Port: > 0 });
        await Task.Delay(2500); // some rounds time out, which used to leave waits behind that took the wake-up
        settings.Watchdog.CheckSeconds = 3600;
        await Task.Delay(1500); // the round in progress picks up the long wait
        settings.Watchdog.Enabled = false;
        service.SettingsChanged();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.Enabled == false);
    }

    [Fact]
    public async Task A_commit_while_the_app_was_closed_reaches_the_agent_that_connects_after()
    {
        var settings = On();
        settings.Watchdog.Repos = [new WatchedRepo { Path = "repo" }];
        using (var before = new WatchdogService(() => settings, File_, null, new FakeProbes()))
        {
            before.Start();
            await WaitUntil(() => WatchdogStatus.Load(File_)?.Memory.Repos.Count == 1);
        }

        using var after = new WatchdogService(() => settings, File_, null, new FakeProbes { Repo = ["commit b while closed"] });
        after.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.RecentAlerts.Count == 1);
        var status = WatchdogStatus.Load(File_)!;
        Assert.Single(status.Mail.Unclaimed); // and saved, in case the app stops again first

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(status.Url.Replace("{session}", "s1")), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var buffer = new byte[1024];
        var got = await client.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Contains("while closed", Encoding.UTF8.GetString(buffer, 0, got.Count));
    }

    [Fact]
    public async Task An_alert_saved_before_a_restart_reaches_the_agent_that_connects_after_it()
    {
        var settings = On();
        settings.Watchdog.Repos = [new WatchedRepo { Path = "repo" }];
        var probes = new FakeProbes();
        using (var first = new WatchdogService(() => settings, File_, null, probes))
        {
            first.Start();
            await WaitUntil(() => WatchdogStatus.Load(File_)?.Memory.Repos.Count == 1);
            probes.Repo = ["commit b before the restart"];
            await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.Mail.Unclaimed.Count == 1);
        }

        var oldPort = WatchdogStatus.Load(File_)!.Port;
        using var second = new WatchdogService(() => settings, File_, null, new FakeProbes { Repo = ["commit b before the restart"] });
        second.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.Port != oldPort);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(WatchdogStatus.Load(File_)!.Url.Replace("{session}", "s1")), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var buffer = new byte[1024];
        var got = await client.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Contains("before the restart", Encoding.UTF8.GetString(buffer, 0, got.Count));
    }

    private sealed class SlowRepo(FakeProbes inner, ManualResetEventSlim slow) : IWatchdogProbes
    {
        public IReadOnlyList<string>? RepoState(WatchedRepo repo)
        {
            slow.Wait(TimeSpan.FromSeconds(10));
            return inner.RepoState(repo);
        }

        public string? ServerState(WatchedServer server) => inner.ServerState(server);
        public ActivityMark? NewestActivity(IReadOnlyList<string> paths) => inner.NewestActivity(paths);
        public ProgressMark? Progress(string session) => inner.Progress(session);
    }

    [Fact]
    public async Task Closing_the_app_during_a_slow_check_does_not_lose_the_change_it_found()
    {
        var settings = On();
        settings.Watchdog.Repos = [new WatchedRepo { Path = "repo" }];
        var inner = new FakeProbes();
        using var go = new ManualResetEventSlim(true);
        var service = new WatchdogService(() => settings, File_, null, new SlowRepo(inner, go));
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_)?.Memory.Repos.Count == 1);
        go.Reset();
        inner.Repo = ["commit b found while closing"];
        await Task.Delay(1500); // a round is now waiting on the slow read
        var closing = Task.Run(service.Dispose);
        await Task.Delay(4500);
        go.Set(); // the read finishes after the app has given up waiting for it
        await closing;
        await Task.Delay(1000);

        // Saved as not yet known, so the next start finds it again and tells it.
        Assert.Equal(["commit a one"], WatchdogStatus.Load(File_)!.Memory.Repos["repo"]);
        Assert.False(WatchdogStatus.Load(File_)!.Running);
    }

    private sealed class SlowServer(FakeProbes inner, ManualResetEventSlim slow) : IWatchdogProbes
    {
        public IReadOnlyList<string>? RepoState(WatchedRepo repo) => inner.RepoState(repo);

        public string? ServerState(WatchedServer server)
        {
            slow.Wait(TimeSpan.FromSeconds(10));
            return inner.ServerState(server);
        }

        public ActivityMark? NewestActivity(IReadOnlyList<string> paths) => inner.NewestActivity(paths);
        public ProgressMark? Progress(string session) => inner.Progress(session);
    }

    [Fact]
    public async Task Closing_while_a_slow_server_read_holds_up_the_round_does_not_save_a_repo_change_as_known()
    {
        // The repo is read first and its new commit noted; then ssh takes its time, and the app closes before the round
        // ends and tells it.
        var settings = On();
        settings.Watchdog.ServerEveryChecks = 1;
        settings.Watchdog.Repos = [new WatchedRepo { Path = "repo" }];
        settings.Watchdog.Servers = [new WatchedServer { Name = "home", SshTarget = "root@h" }];
        var inner = new FakeProbes();
        using var go = new ManualResetEventSlim(true);
        var service = new WatchdogService(() => settings, File_, null, new SlowServer(inner, go));
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.Memory.Repos.Count == 1 && s.Memory.Servers.Count == 1);
        go.Reset();
        inner.Repo = ["commit b noted, never told"];
        await Task.Delay(2500); // the next round has read the repo and waits on ssh
        await Task.Run(service.Dispose);
        go.Set();
        await Task.Delay(500);

        Assert.Equal(["commit a one"], WatchdogStatus.Load(File_)!.Memory.Repos["repo"]);
    }

    [Fact]
    public async Task Closing_the_app_as_a_check_finishes_keeps_the_alert_it_found()
    {
        var settings = On();
        settings.Watchdog.Repos = [new WatchedRepo { Path = "repo" }];
        var inner = new FakeProbes();
        using var go = new ManualResetEventSlim(true);
        var service = new WatchdogService(() => settings, File_, null, new SlowRepo(inner, go));
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_)?.Memory.Repos.Count == 1);
        go.Reset();
        inner.Repo = ["commit b found while closing"];
        await Task.Delay(1500);
        var closing = Task.Run(service.Dispose);
        await Task.Delay(1000);
        go.Set(); // well inside the time closing waits for a round
        await closing;

        var status = WatchdogStatus.Load(File_)!;
        Assert.Equal(["commit b found while closing"], status.Memory.Repos["repo"]);
        Assert.Contains(status.Mail.Unclaimed, a => a.Text.Contains("found while closing"));
    }

    [Fact]
    public async Task A_wrongly_capitalised_watchdog_section_is_told()
    {
        var notified = new List<WatchdogAlert>();
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "watchdog": { "Enabled": true } }""")!;
        Assert.False(settings.Watchdog.Enabled);
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await WaitUntil(() => { lock (notified) return notified.Count == 1; });
        lock (notified) Assert.Contains("\"watchdog\"", notified[0].Text);
    }

    [Fact]
    public async Task A_misspelt_key_is_told_even_while_it_leaves_the_watchdog_off()
    {
        var notified = new List<WatchdogAlert>();
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "Watchdog": { "enabled": true } }""")!;
        Assert.False(settings.Watchdog.Enabled);
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await WaitUntil(() => { lock (notified) return notified.Count == 1; });
        lock (notified) Assert.Contains("\"enabled\"", notified[0].Text);
    }

    [Fact]
    public async Task Closing_during_the_first_check_keeps_what_was_remembered()
    {
        var settings = On();
        settings.Watchdog.Repos = [new WatchedRepo { Path = "repo" }];
        var inner = new FakeProbes();
        using (var before = new WatchdogService(() => settings, File_, null, inner))
        {
            before.Start();
            await WaitUntil(() => WatchdogStatus.Load(File_)?.Memory.Repos.Count == 1);
        }

        using var go = new ManualResetEventSlim(false); // the first read hangs, like ssh to a server that's down
        var after = new WatchdogService(() => settings, File_, null, new SlowRepo(inner, go));
        after.Start();
        await Task.Delay(800);
        await Task.Run(after.Dispose);
        go.Set();
        Assert.Equal(["commit a one"], WatchdogStatus.Load(File_)!.Memory.Repos["repo"]);
    }

    [Fact]
    public async Task A_misspelt_key_while_off_is_told_once_even_with_an_old_status_file()
    {
        File.WriteAllText(File_, """{ "Enabled": true, "Token": "0123456789abcdef0123456789abcdef" }""");
        var notified = new List<WatchdogAlert>();
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "Watchdog": { "Enable": true, "CheckSeconds": 1 } }""")!;
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await Task.Delay(3500);
        lock (notified) Assert.Single(notified);
    }

    [Fact]
    public async Task Closing_straight_after_starting_leaves_the_saved_state_alone()
    {
        var settings = On();
        settings.Watchdog.Repos = [new WatchedRepo { Path = "repo" }];
        using (var before = new WatchdogService(() => settings, File_, null, new FakeProbes()))
        {
            before.Start();
            await WaitUntil(() => WatchdogStatus.Load(File_)?.Memory.Repos.Count == 1);
        }
        var token = WatchdogStatus.Load(File_)!.Token;

        for (var i = 0; i < 20; i++)
        {
            var after = new WatchdogService(() => settings, File_, null, new FakeProbes());
            after.Start();
            after.Dispose();
            var status = WatchdogStatus.Load(File_)!;
            Assert.Equal(token, status.Token);
            Assert.Single(status.Memory.Repos);
        }
    }

    [Fact]
    public async Task Two_lasting_problems_are_each_told_once()
    {
        var notified = new List<WatchdogAlert>();
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "Watchdog": { "Enabled": true, "CheckSeconds": 1, "Port": 70000 }, "watchdog": {} }""")!;
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await Task.Delay(4000);
        lock (notified)
        {
            Assert.Equal(2, notified.Count);
            Assert.Contains(notified, a => a.Text.Contains("as well as") && !a.Text.Contains("is off"));
        }
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_read_is_told_to_someone_who_had_it_on()
    {
        var notified = new List<WatchdogAlert>();
        File.WriteAllText(File_, """{ "Enabled": true, "Token": "0123456789abcdef0123456789abcdef" }""");
        var settingsFile = Path.Combine(Path.GetDirectoryName(File_)!, "settings.json");
        File.WriteAllText(settingsFile, """{ "Watchdog": { "Enabled": true, "Port": "47615" } }""");
        var settings = AppSettings.Load(settingsFile).Clone(); // the app hands copies around, never the loaded one
        Assert.NotNull(settings.LoadError);
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await WaitUntil(() => { lock (notified) return notified.Count == 1; });
        lock (notified) Assert.Contains("couldn't be read", notified[0].Text);
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_read_says_nothing_to_someone_who_never_used_the_watchdog()
    {
        var notified = new List<WatchdogAlert>();
        var settingsFile = Path.Combine(Path.GetDirectoryName(File_)!, "settings.json");
        File.WriteAllText(settingsFile, "{ broken");
        var settings = AppSettings.Load(settingsFile);
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await Task.Delay(1500);
        lock (notified) Assert.Empty(notified);
    }

    [Fact]
    public void An_unreadable_settings_file_is_kept_before_the_app_saves_over_it()
    {
        var settingsFile = Path.Combine(Path.GetDirectoryName(File_)!, "settings.json");
        File.WriteAllText(settingsFile, "{ \"Watchdog\": { \"Enabled\": true, } broken");
        Assert.NotNull(AppSettings.Load(settingsFile).LoadError);
        Assert.Equal(File.ReadAllText(settingsFile), File.ReadAllText(settingsFile + ".unreadable"));
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_read_while_turning_the_watchdog_on_is_told()
    {
        var notified = new List<WatchdogAlert>();
        var settingsFile = Path.Combine(Path.GetDirectoryName(File_)!, "settings.json");
        File.WriteAllText(settingsFile, """{ "Watchdog": { "Enabled": true, "Repos": [{ "Path": "C:\Users\me\src" }] } }""");
        var settings = AppSettings.Load(settingsFile).Clone();
        Assert.NotNull(settings.LoadError);
        Assert.False(File.Exists(File_)); // never on before
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await WaitUntil(() => { lock (notified) return notified.Count == 1; });
        lock (notified) Assert.Contains("couldn't be read", notified[0].Text);
    }

    [Fact]
    public async Task A_problem_the_service_told_is_not_told_again_after_a_restart()
    {
        var notified = new List<WatchdogAlert>();
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "Watchdog": { "Enabled": true, "CheckSeconds": 3600, "Port": 0 }, "watchdog": {} }""")!;
        for (var start = 0; start < 3; start++)
        {
            using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
            service.Start();
            await WaitUntil(() => WatchdogStatus.Load(File_) is { Running: true });
        }
        lock (notified) Assert.Single(notified);
    }

    [Fact]
    public async Task A_setting_with_the_wrong_capitals_is_told()
    {
        var notified = new List<WatchdogAlert>();
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "Watchdog": { "Enabled": true, "Port": 0 }, "stuckExemptCommands": ["my_soak"] }""")!;
        Assert.Empty(settings.StuckExemptCommands ?? []);
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await WaitUntil(() => { lock (notified) return notified.Count == 1; });
        lock (notified) Assert.Contains("\"StuckExemptCommands\"", notified[0].Text);
    }

    [Fact]
    public async Task A_status_file_held_open_at_start_is_waited_for_not_wiped()
    {
        var notified = new List<WatchdogAlert>();
        File.WriteAllText(File_, """{ "Enabled": true, "Token": "0123456789abcdef0123456789abcdef" }""");
        var holder = new FileStream(File_, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // a backup tool, say
        _ = Task.Delay(1500).ContinueWith(_ => holder.Dispose());
        var settings = On();
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await Task.Delay(2000);
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Running: true });
        Assert.Equal("0123456789abcdef0123456789abcdef", WatchdogStatus.Load(File_)!.Token);
        lock (notified) Assert.Empty(notified);
    }

    [Fact]
    public async Task Watched_things_left_outside_the_watchdog_section_are_told()
    {
        var notified = new List<WatchdogAlert>();
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "Watchdog": { "Enabled": true, "Port": 0 }, "Servers": [{ "Name": "home", "SshTarget": "root@h" }] }""")!;
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await WaitUntil(() => { lock (notified) return notified.Count == 1; });
        lock (notified) Assert.Contains("\"Servers\" outside", notified[0].Text);
    }

    [Fact]
    public async Task A_damaged_status_file_is_told_and_kept()
    {
        var notified = new List<WatchdogAlert>();
        File.WriteAllBytes(File_, new byte[64]); // zeroed, as after a power cut
        var settings = On();
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await WaitUntil(() => { lock (notified) return notified.Count == 1; });
        lock (notified) Assert.Contains("couldn't be read", notified[0].Text);
        Assert.True(File.Exists(File_ + ".unreadable"));
    }

    [Fact]
    public void A_null_watchdog_section_loads_as_off_rather_than_missing()
    {
        var settingsFile = Path.Combine(Path.GetTempPath(), $"s-{Guid.NewGuid():N}.json");
        File.WriteAllText(settingsFile, """{ "Watchdog": null }""");
        try
        {
            Assert.False(AppSettings.Load(settingsFile).Watchdog.Enabled);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public void A_slow_round_does_not_make_the_watchdog_look_stopped()
    {
        var status = new WatchdogStatus { Enabled = true, Running = true, CheckSeconds = 10, LastRoundSeconds = 60, LastCheckUtc = DateTime.UtcNow.AddSeconds(-70) };
        Assert.True(status.IsCurrent(DateTime.UtcNow));
    }

    [Fact]
    public async Task Closing_saves_even_if_the_file_is_held_open_for_a_moment()
    {
        var settings = On();
        var service = new WatchdogService(() => settings, File_, null, new FakeProbes());
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Running: true });
        var holder = new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _ = Task.Delay(200).ContinueWith(_ => holder.Dispose()); // a reader, such as watchdog_status, lets go shortly
        service.Dispose();
        await Task.Delay(300);
        Assert.False(WatchdogStatus.Load(File_)!.Running);
    }

    [Fact]
    public async Task Turning_off_and_on_does_not_tell_a_stuck_command_again()
    {
        ProcessInfo Proc(int pid, string name) => new(new ProcessKey(pid, 100 + pid), 0, name, null, name, DateTime.UtcNow, 0, 0, 0, [], 0);
        var session = new AgentSession(AgentKind.ClaudeCode, new ProcessNode(Proc(1, "claude.exe"), TimeSpan.Zero, []));
        var stuck = new StuckProcess(session, new ProcessNode(Proc(9, "bash.exe"), TimeSpan.Zero, []), StuckKind.Waiting, TimeSpan.FromMinutes(11));
        var settings = On();
        settings.Watchdog.CheckSeconds = 3600; // nothing saves in between but turning off
        using var service = new WatchdogService(() => settings, File_, null, new FakeProbes());
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Running: true });
        service.NoteStuck([stuck]);
        settings.Watchdog.Enabled = false;
        service.SettingsChanged();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Enabled: false });
        Assert.NotEmpty(WatchdogStatus.Load(File_)!.Memory.HungSeen);

        settings.Watchdog.Enabled = true; // and back on: the new engine starts from what was saved
        service.SettingsChanged();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Running: true });
        service.NoteStuck([stuck]);
        var before = WatchdogStatus.Load(File_)!.LastCheckUtc;
        service.SettingsChanged(); // a round, which saves what was told
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.LastCheckUtc > before);
        Assert.Single(WatchdogStatus.Load(File_)!.RecentAlerts, a => a.Kind == WatchdogAlertKind.Hung);
    }

    private sealed class SlowServerAfterFirst(FakeProbes inner, ManualResetEventSlim slow) : IWatchdogProbes
    {
        private int _reads;
        public IReadOnlyList<string>? RepoState(WatchedRepo repo) => inner.RepoState(repo);

        public string? ServerState(WatchedServer server)
        {
            if (Interlocked.Increment(ref _reads) > 1) slow.Wait(TimeSpan.FromSeconds(10));
            return inner.ServerState(server);
        }

        public ActivityMark? NewestActivity(IReadOnlyList<string> paths) => inner.NewestActivity(paths);
        public ProgressMark? Progress(string session) => inner.Progress(session);
    }

    [Fact]
    public async Task A_stuck_command_told_just_before_closing_mid_check_is_saved_as_told()
    {
        ProcessInfo Proc(int pid, string name) => new(new ProcessKey(pid, 100 + pid), 0, name, null, name, DateTime.UtcNow, 0, 0, 0, [], 0);
        var session = new AgentSession(AgentKind.ClaudeCode, new ProcessNode(Proc(1, "claude.exe"), TimeSpan.Zero, []));
        var stuck = new StuckProcess(session, new ProcessNode(Proc(9, "bash.exe"), TimeSpan.Zero, []), StuckKind.Waiting, TimeSpan.FromMinutes(11));
        var settings = On();
        settings.Watchdog.ServerEveryChecks = 1;
        settings.Watchdog.Servers = [new WatchedServer { Name = "home", SshTarget = "root@h" }];
        using var go = new ManualResetEventSlim(false);
        var service = new WatchdogService(() => settings, File_, null, new SlowServerAfterFirst(new FakeProbes(), go));
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Running: true });
        service.NoteStuck([stuck]); // told between rounds
        await Task.Delay(1500); // the next round is stuck on ssh
        await Task.Run(service.Dispose);
        go.Set();
        var saved = WatchdogStatus.Load(File_)!;
        Assert.NotEmpty(saved.Memory.HungSeen);
        Assert.Contains(saved.Mail.Unclaimed, a => a.Kind == WatchdogAlertKind.Hung);
    }

    [Fact]
    public async Task Closing_during_the_first_check_still_says_it_was_on()
    {
        var settings = On();
        settings.Watchdog.Repos = [new WatchedRepo { Path = "repo" }];
        File.WriteAllText(File_, """{ "Enabled": true, "Token": "0123456789abcdef0123456789abcdef" }""");
        using var go = new ManualResetEventSlim(false);
        var service = new WatchdogService(() => settings, File_, null, new SlowRepo(new FakeProbes(), go));
        service.Start();
        await Task.Delay(800);
        await Task.Run(service.Dispose);
        go.Set();
        Assert.True(WatchdogStatus.Load(File_)!.Enabled);
    }

    [Fact]
    public async Task Turning_off_is_saved_even_if_the_file_was_held_open_at_the_time()
    {
        var settings = On();
        settings.Watchdog.CheckSeconds = 1;
        using var service = new WatchdogService(() => settings, File_, null, new FakeProbes());
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Enabled: true, Running: true });
        using (new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            settings.Watchdog.Enabled = false;
            service.SettingsChanged();
            await Task.Delay(3000);
        }
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Enabled: false, Running: false });
    }

    [Fact]
    public async Task A_stuck_command_from_the_process_poll_reaches_a_connected_agent()
    {
        var settings = On();
        using var service = new WatchdogService(() => settings, File_, null, new FakeProbes());
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Port: > 0 });
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(WatchdogStatus.Load(File_)!.Url.Replace("{session}", "s1")), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.Sessions.Contains("s1"));

        ProcessInfo Proc(int pid, string name) => new(new ProcessKey(pid, 100 + pid), 0, name, null, name, DateTime.UtcNow, 0, 0, 0, [], 0);
        var session = new AgentSession(AgentKind.ClaudeCode, new ProcessNode(Proc(1, "claude.exe"), TimeSpan.Zero, []));
        service.NoteStuck([new StuckProcess(session, new ProcessNode(Proc(9, "bash.exe"), TimeSpan.Zero, []), StuckKind.Waiting, TimeSpan.FromMinutes(11))]);

        var buffer = new byte[1024];
        var got = await client.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Contains("HUNG", Encoding.UTF8.GetString(buffer, 0, got.Count));
    }

    [Fact]
    public async Task An_impossible_port_is_reported_to_the_user_and_the_status_says_so()
    {
        var notified = new List<WatchdogAlert>();
        var settings = On();
        settings.Watchdog.Port = 70000;
        using var service = new WatchdogService(() => settings, File_, a => { lock (notified) notified.Add(a); }, new FakeProbes());
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_)?.Error is not null);
        await Task.Delay(2500); // more rounds, one notification
        lock (notified) Assert.Equal(WatchdogAlertKind.Problem, Assert.Single(notified).Kind);
    }

    [Fact]
    public async Task A_null_watchdog_section_does_not_kill_the_loop()
    {
        var settings = new AppSettings { Watchdog = null! };
        using var service = new WatchdogService(() => settings, File_, null, new FakeProbes());
        service.Start();
        await Task.Delay(1500);
        settings.Watchdog = new WatchdogSettings { Enabled = true, Port = 0, CheckSeconds = 1 };
        service.SettingsChanged();
        await WaitUntil(() => WatchdogStatus.Load(File_)?.Enabled == true);
    }

    [Fact]
    public async Task Turning_it_off_closes_the_socket()
    {
        var settings = On();
        using var service = new WatchdogService(() => settings, File_, null, new FakeProbes());
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { Port: > 0 });
        var status = WatchdogStatus.Load(File_)!;
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(status.Url.Replace("{session}", "s1")), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        settings.Watchdog.Enabled = false;
        service.SettingsChanged();
        await WaitUntil(() => WatchdogStatus.Load(File_) is { } s && s.Enabled == false);
        using var refused = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await refused.ConnectAsync(IPAddress.Loopback, status.Port));
    }

    [Fact]
    public async Task A_port_already_taken_is_reported_not_fatal()
    {
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var settings = On();
        settings.Watchdog.Port = ((IPEndPoint)taken.LocalEndpoint).Port;
        using var service = new WatchdogService(() => settings, File_, null, new FakeProbes());
        service.Start();
        await WaitUntil(() => WatchdogStatus.Load(File_)?.Error is not null);
        Assert.Contains("Couldn't listen", WatchdogStatus.Load(File_)!.Error);
    }

    [Fact]
    public void A_status_file_saying_it_checked_long_ago_is_not_current()
    {
        var status = new WatchdogStatus { Enabled = true, Running = true, CheckSeconds = 120, LastCheckUtc = DateTime.UtcNow.AddHours(-1) };
        Assert.False(status.IsCurrent(DateTime.UtcNow));
        status.LastCheckUtc = DateTime.UtcNow.AddMinutes(-3);
        Assert.True(status.IsCurrent(DateTime.UtcNow));
        status.Running = false; // the app was closed a moment ago
        Assert.False(status.IsCurrent(DateTime.UtcNow));
        status.Running = true;
        status.Enabled = false;
        Assert.False(status.IsCurrent(DateTime.UtcNow));
    }

    [Fact]
    public void A_broken_status_file_reads_as_none()
    {
        File.WriteAllText(File_, "{ not json");
        Assert.Null(WatchdogStatus.Load(File_));
    }
}

public class WatchdogMcpTests
{
    private static string Ask(Func<WatchdogStatus?> status)
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"watchdog_status","arguments":{}}}""";
        var output = new StringWriter();
        new McpServer(new StringReader(request), output, watchdogStatus: status).Run();
        return System.Text.Json.Nodes.JsonNode.Parse(output.ToString())!["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }

    [Fact]
    public void The_tool_is_listed_as_read_only()
    {
        var output = new StringWriter();
        new McpServer(new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), output).Run();
        var tool = System.Text.Json.Nodes.JsonNode.Parse(output.ToString())!["result"]!["tools"]!.AsArray()
            .Single(t => t!["name"]!.GetValue<string>() == "watchdog_status")!;
        Assert.True(tool["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    [Fact]
    public void With_the_port_not_ours_it_gives_no_address_so_the_token_stays_private()
    {
        var text = Ask(() => new WatchdogStatus { Enabled = true, Running = true, Port = 5, Token = "tok", CheckSeconds = 120, LastCheckUtc = DateTime.UtcNow, Error = "Couldn't listen" });
        Assert.DoesNotContain("tok", text);
        Assert.Contains("\"checking_now\": false", text);
    }

    [Fact]
    public void After_the_app_has_closed_it_gives_no_address()
    {
        var text = Ask(() => new WatchdogStatus { Enabled = true, Running = false, Port = 5, Token = "tok", CheckSeconds = 120, LastCheckUtc = DateTime.UtcNow });
        Assert.DoesNotContain("tok", text);
    }

    [Fact]
    public void Never_run_says_so() => Assert.Contains("never run", Ask(() => null));

    [Fact]
    public void It_gives_the_address_and_says_whether_it_is_checking()
    {
        var text = Ask(() => new WatchdogStatus { Enabled = true, Running = true, Port = 5, Token = "tok", CheckSeconds = 120, LastCheckUtc = DateTime.UtcNow });
        Assert.Contains("ws://127.0.0.1:5/watchdog?session={session}", text.Replace("\\u0026", "&"));
        Assert.Contains("\"checking_now\": true", text);
    }

    [Fact]
    public void Off_it_gives_no_address_or_token()
    {
        var text = Ask(() => new WatchdogStatus { Enabled = false, Port = 5, Token = "tok" });
        Assert.DoesNotContain("tok", text);
        Assert.Contains("\"checking_now\": false", text);
    }
}

public class StuckExemptCommandTests
{
    private static ProcessInfo Proc(int pid, int parent, string name, string command, DateTime started) =>
        new(new ProcessKey(pid, 100 + pid), parent, name, null, command, started, 0, 0, 0, [], 0);

    private static readonly DateTime Now = new(2026, 11, 6, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Listed_text_exempts_a_command_ignoring_case_anywhere_in_its_tree()
    {
        var tool = Proc(2, 1, "bash.exe", "bash long-run.sh", Now.AddHours(-1));
        var child = Proc(3, 2, "python.exe", "python My_Soak_Test.py", Now.AddHours(-1));
        var tree = new List<ProcessNode> { new(tool, TimeSpan.Zero, []), new(child, TimeSpan.Zero, []) };

        Assert.Null(StuckExemptions.Reason(tool, tree, Now));
        Assert.Null(StuckExemptions.Reason(tool, tree, Now, ["other"]));
        Assert.Null(StuckExemptions.Reason(tool, tree, Now, [""])); // an empty line in the list doesn't exempt everything
        Assert.NotNull(StuckExemptions.Reason(tool, tree, Now, ["my_soak_test"]));
    }
}
