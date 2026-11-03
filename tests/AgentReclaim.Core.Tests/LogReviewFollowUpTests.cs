using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using AgentReclaim.Core.Updates;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

/// <summary>The smaller items from reading a week of real logs.</summary>
public class LogReviewFollowUpTests
{
    // A whole core busy: the same exemptions as no activity

    private static AnalysisResult Busy(int until, params ProcessInfo[] tree)
    {
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions { CoreCount = 4, SpinningGap = TimeSpan.FromMinutes(2) });
        AnalysisResult? last = null;
        for (var minute = 5; minute <= until; minute++)
            last = analyzer.Analyze(Snapshot(minute, [Proc(10, 1, "claude.exe", startMinute: 0), .. tree]));
        return last!;
    }

    private static readonly AppSettings StopSpinning = new() { AutoStopStuckSpinning = true };

    [Fact]
    public void A_busy_process_inside_its_commands_time_limit_is_left_alone()
    {
        var result = Busy(40,
            Proc(11, 10, "bash.exe", "bash -c 'timeout 3000 python train.py'", startMinute: 5),
            Proc(12, 11, "python.exe", "python train.py", startMinute: 5, cpu: 30));
        Assert.Empty(result.Stuck);
        Assert.Empty(new AutoRuleEngine().StuckToStop(result, StopSpinning, 1));
    }

    [Fact]
    public void A_busy_process_past_its_commands_time_limit_can_be_stopped()
    {
        var tree = new[]
        {
            Proc(11, 10, "bash.exe", "bash -c 'timeout 900 python train.py'", startMinute: 5),
            Proc(12, 11, "python.exe", "python train.py", startMinute: 5, cpu: 30),
        };
        Assert.Empty(Busy(19, tree).Stuck);                  // 14 of its 15 minutes
        var result = Busy(21, tree);
        Assert.Equal(12, Assert.Single(result.Stuck).Node.Process.Pid);
        Assert.Single(new AutoRuleEngine().StuckToStop(result, StopSpinning, 1));
    }

    [Fact]
    public void A_busy_hook_or_agent_helper_is_left_alone() =>
        Assert.Empty(Busy(40, Proc(11, 10, "node_repl.exe", @"C:\x\OpenAI\Codex\runtimes\n\node_repl.exe", startMinute: 5, cpu: 30)).Stuck);

    [Fact]
    public void A_busy_tool_with_no_limit_is_still_reported()
    {
        var result = Busy(21, Proc(11, 10, "python.exe", "python loop.py", startMinute: 5, cpu: 30));
        Assert.Equal(StuckKind.Spinning, Assert.Single(result.Stuck).Kind);
    }

    [Fact]
    public void A_busy_agent_is_reported_even_under_a_limited_command()
    {
        var result = Busy(21,
            Proc(11, 10, "bash.exe", "bash -c 'timeout 3000 codex exec x'", startMinute: 5),
            Proc(12, 11, "codex.exe", "codex exec x", startMinute: 5, cpu: 30));
        Assert.Equal(12, Assert.Single(result.Stuck).Node.Process.Pid);
        Assert.Empty(new AutoRuleEngine().StuckToStop(result, StopSpinning, 1)); // and never stopped
    }

    [Fact]
    public void Under_a_nested_agent_the_nearest_command_decides()
    {
        // The outer shell has a limit, but the loop belongs to what the inner agent started.
        var result = Busy(21,
            Proc(11, 10, "bash.exe", "bash -c 'timeout 3000 codex'", startMinute: 5),
            Proc(12, 11, "codex.exe", "codex", startMinute: 5),
            Proc(13, 12, "python.exe", "python loop.py", startMinute: 5, cpu: 30));
        Assert.Contains(result.Stuck, s => s.Node.Process.Pid == 13);
    }

    // Logging

    private static (LogLevel Level, string Text)? Note(PollLog log, TimeSpan at, int sessions = 4, int leftovers = 0, int candidates = 0, double ms = 30, int processes = 300) =>
        log.Note(T0 + at, processes, sessions, leftovers, candidates, ms, () => 100L << 20);

    [Fact]
    public void An_unchanged_poll_is_not_logged_but_the_process_count_alone_does_not_count_as_a_change()
    {
        var log = new PollLog();
        Assert.NotNull(Note(log, TimeSpan.Zero));
        Assert.Null(Note(log, TimeSpan.FromSeconds(3)));
        Assert.Null(Note(log, TimeSpan.FromSeconds(6), processes: 345));
    }

    [Theory]
    [InlineData(5, 0, 0)]
    [InlineData(4, 1, 0)]
    [InlineData(4, 0, 1)]
    public void A_change_in_sessions_leftovers_or_candidates_is_logged(int sessions, int leftovers, int candidates)
    {
        var log = new PollLog();
        Note(log, TimeSpan.Zero);
        var line = Note(log, TimeSpan.FromSeconds(3), sessions, leftovers, candidates);
        Assert.Equal(LogLevel.Debug, line!.Value.Level);
        Assert.Contains($"{sessions} sessions, {leftovers} leftovers, {candidates} auto-stop candidate(s)", line.Value.Text);
    }

    [Fact]
    public void A_slow_poll_is_logged_with_how_long_polls_took()
    {
        var log = new PollLog();
        Note(log, TimeSpan.Zero);
        Note(log, TimeSpan.FromSeconds(3), ms: 20);
        var line = Note(log, TimeSpan.FromSeconds(6), ms: 1500);
        Assert.Contains("Last 2 poll(s): average 760 ms, slowest 1500 ms", line!.Value.Text);
        Assert.Contains("App: 100 MB.", line.Value.Text);
    }

    [Fact]
    public void Memory_is_only_read_for_a_line_that_is_written()
    {
        var log = new PollLog();
        var reads = 0;
        long? Read() { reads++; return 1; }
        log.Note(T0, 1, 1, 0, 0, 1, Read);
        log.Note(T0.AddSeconds(3), 1, 1, 0, 0, 1, Read);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Quiet_polls_still_log_a_heartbeat_and_an_hourly_summary_at_info_level()
    {
        var log = new PollLog();
        var lines = new List<(LogLevel Level, string Text)>();
        for (var second = 0; second <= 3600; second += 3)
            if (Note(log, TimeSpan.FromSeconds(second)) is { } line) lines.Add(line);

        Assert.Equal(1 + 11 + 1, lines.Count);                    // first, every 5 minutes, then the hour's summary
        var summary = lines[^1];
        Assert.Equal(LogLevel.Info, summary.Level);
        Assert.Contains("Last 1201 poll(s)", summary.Text);       // the whole hour, not just since the last heartbeat
        Assert.All(lines.SkipLast(1), l => Assert.Equal(LogLevel.Debug, l.Level));
    }

    [Fact]
    public void A_day_of_quiet_polls_is_a_small_log()
    {
        var log = new PollLog();
        var lines = 0;
        for (var second = 0; second < 86400; second += 3)
            if (Note(log, TimeSpan.FromSeconds(second), processes: 300 + (second % 7)) is not null) lines++;
        Assert.InRange(lines, 280, 300); // was 28,800 a day
    }

    private static OrphanTree Observed(ProcessInfo root, params ProcessInfo[] children) => new(
        new ProcessNode(root, TimeSpan.FromHours(1), children.Select(c => new ProcessNode(c, TimeSpan.FromHours(1), [])).ToList()),
        AgentKind.Codex, Attribution.Observed, "codex.exe (pid 10)", 10, 1);

    private static AnalysisResult With(params OrphanTree[] trees) =>
        new(Snapshot(90, trees.SelectMany(t => t.Root.SelfAndDescendants().Select(x => x.Node.Process)).ToArray()), [], trees, []);

    [Fact]
    public void Why_a_leftover_was_not_stopped_is_noted_once_per_process_and_reason()
    {
        var engine = new AutoRuleEngine();
        var settings = new AppSettings { AutoStopLeftovers = true };
        var analysis = With(Observed(Proc(30, 1, "node.exe", "node x.js", connections: 2)));

        engine.LeftoversToStop(analysis, settings, 1);
        Assert.Equal("Auto-stop skipped node.exe (pid 30): it has open connections.", Assert.Single(engine.TakeSkipNotes()));

        engine.LeftoversToStop(analysis, settings, 1);
        Assert.Empty(engine.TakeSkipNotes());
    }

    [Fact]
    public void A_refusal_by_the_safety_checks_gives_their_reason()
    {
        var engine = new AutoRuleEngine();
        engine.LeftoversToStop(With(Observed(Proc(30, 1, "svchost.exe"))), new AppSettings { AutoStopLeftovers = true }, 1);
        Assert.Contains("svchost.exe is on the protected list", Assert.Single(engine.TakeSkipNotes()));
    }

    [Fact]
    public void Nothing_is_noted_when_the_rule_is_off_or_a_leftover_is_only_waiting_its_turn()
    {
        var engine = new AutoRuleEngine();
        engine.LeftoversToStop(With(Observed(Proc(30, 1, "node.exe", "node x.js", connections: 2))), new AppSettings { AutoStopLeftovers = false }, 1);
        Assert.Empty(engine.TakeSkipNotes());

        // The tray's "Free safe leftovers" asks the same checks, and isn't an automatic stop being held back.
        engine.SafeLeftovers(With(Observed(Proc(30, 1, "node.exe", "node x.js", connections: 2))), new AppSettings(), 1);
        Assert.Empty(engine.TakeSkipNotes());

        var waiting = new OrphanTree(new ProcessNode(Proc(31, 1, "node.exe", "node y.js"), TimeSpan.FromMinutes(1), []),
            AgentKind.Codex, Attribution.Observed, "codex.exe (pid 10)", 10, 1);
        engine.LeftoversToStop(With(waiting), new AppSettings { AutoStopLeftovers = true }, 1);
        Assert.Empty(engine.TakeSkipNotes());
    }

    [Fact]
    public void Why_a_stuck_process_was_not_stopped_is_noted()
    {
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions { CoreCount = 4 });
        AnalysisResult? last = null;
        for (var minute = 5; minute <= 20; minute++)
            last = analyzer.Analyze(Snapshot(minute, Proc(10, 1, "claude.exe"),
                Proc(11, 10, "bash.exe", "bash -c 'ssh host'", startMinute: 5, connections: 1)));

        var engine = new AutoRuleEngine();
        Assert.Empty(engine.StuckToStop(last!, new AppSettings { AutoStopStuckWaiting = true }, 1));
        Assert.Equal("Auto-stop skipped bash.exe (pid 11): it has open connections.", Assert.Single(engine.TakeSkipNotes()));
    }

    [Fact]
    public void Missing_connection_data_is_noted_once_until_it_comes_back()
    {
        var engine = new AutoRuleEngine();
        var settings = new AppSettings { AutoStopLeftovers = true };
        var blind = With(Observed(Proc(30, 1, "node.exe", "node x.js")));
        blind = blind with { Snapshot = blind.Snapshot with { NetworkComplete = false } };

        engine.LeftoversToStop(blind, settings, 1);
        Assert.Single(engine.TakeSkipNotes());
        engine.LeftoversToStop(blind, settings, 1);
        Assert.Empty(engine.TakeSkipNotes());
    }

    [Fact]
    public void A_burst_says_why_each_was_held_back()
    {
        var engine = new AutoRuleEngine();
        var trees = Enumerable.Range(0, AutoRuleEngine.BurstSize).Select(i => Observed(Proc(100 + i, 1, "tool.exe"))).ToArray();
        engine.LeftoversToStop(With(trees), new AppSettings { AutoStopLeftovers = true }, 1);
        var notes = engine.TakeSkipNotes();
        Assert.Equal(AutoRuleEngine.BurstSize, notes.Count);
        Assert.Contains("appeared together with many others", notes[0]);
    }

    // Leftover notices

    private static LeftoverNotice? Notice(params ProcessInfo[] left)
    {
        var analyzer = new Analyzer(new LineageStore());
        var watch = new LeftoverWatch();
        var owner = Proc(20, 1, "claude.exe");
        watch.Evaluate(analyzer.Analyze(Snapshot(1, [owner, .. left])), true, _ => null);
        watch.Evaluate(analyzer.Analyze(Snapshot(2, left)), true, _ => null);
        return watch.Evaluate(analyzer.Analyze(Snapshot(3, left)), true, _ => null);
    }

    [Theory]
    [InlineData("sleep.exe")]
    [InlineData("conhost.exe")]
    [InlineData("tail.exe")]
    [InlineData("HEAD.EXE")]
    [InlineData("cut.exe")]
    public void A_small_pipeline_tool_gets_no_notice(string name) =>
        Assert.Null(Notice(Proc(21, 20, name, startMinute: 0)));

    [Fact]
    public void A_tree_with_anything_else_in_it_still_does()
    {
        var notice = Notice(Proc(21, 20, "conhost.exe", startMinute: 0), Proc(22, 21, "node.exe", "node x.js", startMinute: 0));
        Assert.NotNull(notice);
    }

    [Fact]
    public void A_small_tool_holding_a_port_still_does() =>
        Assert.NotNull(Notice(Proc(21, 20, "tail.exe", startMinute: 0, ports: [8000])));

    [Fact]
    public void A_real_leftover_next_to_a_small_tool_is_named_alone()
    {
        var notice = Notice(Proc(21, 20, "sleep.exe", startMinute: 0), Proc(22, 20, "java.exe", "java -jar x", startMinute: 0));
        Assert.Equal("Claude Code left java.exe running.", notice!.Text);
    }

    [Fact]
    public void Small_tools_stay_listed_and_can_still_be_stopped_automatically()
    {
        var engine = new AutoRuleEngine();
        var tree = Observed(Proc(30, 1, "conhost.exe"));
        Assert.Single(engine.LeftoversToStop(With(tree), new AppSettings { AutoStopLeftovers = true }, 1));
    }

    // Polling

    private static AnalysisResult Machine(params ProcessInfo[] processes) => new Analyzer(new LineageStore()).Analyze(Snapshot(1, processes));

    [Fact]
    public void Polling_slows_down_only_with_the_window_closed_and_nothing_to_watch()
    {
        var empty = Machine(Proc(5, 1, "notepad.exe"));
        Assert.Equal(PollSchedule.Quiet, PollSchedule.Next(windowVisible: false, empty, workQueued: false));
        Assert.Equal(PollSchedule.Normal, PollSchedule.Next(windowVisible: true, empty, workQueued: false));
        Assert.Equal(PollSchedule.Normal, PollSchedule.Next(windowVisible: false, empty, workQueued: true));
    }

    [Fact]
    public void An_agent_keeps_the_normal_pace_since_attribution_depends_on_it() =>
        Assert.Equal(PollSchedule.Normal, PollSchedule.Next(false, Machine(Proc(10, 1, "claude.exe")), false));

    [Fact]
    public void A_leftover_keeps_the_normal_pace()
    {
        var result = With(Observed(Proc(30, 1, "node.exe", "node x.js")));
        Assert.Equal(PollSchedule.Normal, PollSchedule.Next(false, result, false));
    }

    // Update checks

    [Fact]
    public void A_daily_check_runs_every_24_hours_without_drifting()
    {
        // Follows the real loop: look, check if due (saving the time), wait.
        var now = T0;
        DateTime? last = null;
        var checks = new List<DateTime>();
        while (now < T0.AddDays(5))
        {
            if (UpdateSchedule.IsDue(last, now))
            {
                last = now;
                checks.Add(now);
            }
            // Timers don't run to the wall clock: fired a little early, 24 hourly waits land just short of a day, which
            // is how an hourly check came to run every 25 hours.
            now += UpdateSchedule.NextWait(last, now) - TimeSpan.FromMilliseconds(50);
        }

        Assert.Equal(5, checks.Count);
        foreach (var (a, b) in checks.Zip(checks.Skip(1)))
            Assert.InRange(b - a, TimeSpan.FromHours(24), TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_failed_check_is_retried_after_an_hour_not_at_once() =>
        Assert.Equal(TimeSpan.FromHours(1), UpdateSchedule.NextWait(T0, T0.AddHours(25)));

    [Fact]
    public void A_check_that_never_ran_is_due_and_exactly_a_day_later_is_due()
    {
        Assert.True(UpdateSchedule.IsDue(null, T0));
        Assert.False(UpdateSchedule.IsDue(T0, T0.AddHours(24).AddSeconds(-1)));
        Assert.True(UpdateSchedule.IsDue(T0, T0.AddHours(24)));
    }

    // Temp folders

    [Fact]
    public void Tests_write_temp_files_inside_their_own_run_folder()
    {
        Assert.StartsWith(AppPaths.DataDirectory, Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AgentReclaim.Tests", Path.GetTempPath());
    }

    [Fact]
    public void Finished_runs_are_swept_and_a_run_still_going_is_not()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sweep-{Guid.NewGuid():N}");
        var finished = Directory.CreateDirectory(Path.Combine(root, "111"));
        var going = Directory.CreateDirectory(Path.Combine(root, "222"));
        var old = Directory.CreateDirectory(Path.Combine(root, "333"));   // running, but the pid must have been reused
        var other = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));
        Directory.SetCreationTimeUtc(old.FullName, DateTime.UtcNow.AddDays(-2));
        var readOnly = Path.Combine(finished.FullName, "object");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);            // as git leaves its objects

        TestSetup.SweepFinishedRuns(root, pid => pid is 222 or 333, DateTime.UtcNow);

        Assert.False(finished.Exists);
        Assert.True(Directory.Exists(going.FullName));
        Assert.False(Directory.Exists(old.FullName));
        Assert.False(Directory.Exists(other.FullName));
    }

    [Fact]
    public void Sweeping_a_folder_that_is_not_there_is_fine() =>
        TestSetup.SweepFinishedRuns(Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}"), _ => false, DateTime.UtcNow);
}
