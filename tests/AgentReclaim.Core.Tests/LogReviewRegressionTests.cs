using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

/// <summary>Regressions found by reading a week of real logs and the action log (command lines shortened from them).</summary>
public class LogReviewRegressionTests
{
    private static Analyzer NewAnalyzer(TimeSpan? gapAfter = null) => new(new LineageStore(), new AnalyzerOptions
    {
        CoreCount = 4,
        UnobservedGapAfter = gapAfter,
    });

    private static AnalysisResult Run(Analyzer analyzer, int from, int to, Func<int, ProcessInfo[]> at)
    {
        AnalysisResult? last = null;
        for (var minute = from; minute <= to; minute++) last = analyzer.Analyze(Snapshot(minute, at(minute)));
        return last!;
    }

    /// <summary>A command the agent started at minute 5, watched well past the point where it would count as stuck.</summary>
    private static AnalysisResult Idle(int until, params ProcessInfo[] tree) =>
        Run(NewAnalyzer(), 5, until, _ => [Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1), .. tree]);

    // 1. Commands whose lack of local CPU means nothing

    [Theory]
    [InlineData("""bash -c "eval 'timeout 3000 docker run --rm img:test python job.py > job.log 2>&1'" """)]
    [InlineData("""bash -c "eval 'timeout 1500 sh -c "until [ -z ... ]; do sleep 30; done"; grep x results.txt'" """)]
    [InlineData("""bash -c "eval 'timeout 1800 codex review - < prompt.txt > out.txt 2>&1'" """)]
    public void A_command_inside_its_own_time_limit_is_not_stuck(string command)
    {
        var result = Idle(29, Proc(11, 10, "bash.exe", command, startMinute: 5)); // 24 minutes in; the shortest limit here is 25
        Assert.Empty(result.Stuck);
        Assert.Empty(new AutoRuleEngine().StuckToStop(result, new AppSettings { AutoStopStuckWaiting = true }, 1));
    }

    [Fact]
    public void A_command_past_its_own_time_limit_can_be_stuck_again()
    {
        var command = "bash -c 'timeout 900 python prompt.py'"; // 15 min, started at minute 5
        Assert.Empty(Idle(19, Proc(11, 10, "bash.exe", command, startMinute: 5)).Stuck);
        Assert.Single(Idle(20, Proc(11, 10, "bash.exe", command, startMinute: 5)).Stuck);
    }

    [Theory]
    [InlineData("timeout 3000 docker run x", 3000)]
    [InlineData("/usr/bin/timeout.exe 90s make", 90)]
    [InlineData("timeout 5m make", 300)]
    [InlineData("timeout 2h make", 7200)]
    [InlineData("timeout -k 5 60 make", 60)]
    [InlineData("timeout --signal=KILL 60 make", 60)]
    [InlineData("timeout 10 a; timeout 600 b", 600)]
    [InlineData("timeout /t 30", 0)]          // Windows' own timeout waits, it doesn't limit anything
    [InlineData("python timeout.py 30", 0)]
    [InlineData("curl --connect-timeout 30 x", 0)]
    [InlineData("", 0)]
    public void Time_limits_are_read_from_the_command_line(string command, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), StuckExemptions.TimeLimitOf(command));

    [Fact]
    public void An_absurd_time_limit_does_not_overflow() =>
        Assert.Equal(TimeSpan.FromDays(365), StuckExemptions.TimeLimitOf("timeout 99999999999999d make"));

    [Theory]
    [InlineData("docker.exe", "docker run --rm img")]
    [InlineData("docker-compose.exe", "docker-compose up")]
    [InlineData("kubectl.exe", "kubectl wait pod/x")]
    [InlineData("gh.exe", "gh run watch 123")]
    public void Work_done_elsewhere_is_not_stuck(string name, string command)
    {
        var result = Idle(40, Proc(11, 10, "bash.exe", "bash", startMinute: 5), Proc(12, 11, name, command, startMinute: 5));
        Assert.Empty(result.Stuck);
    }

    [Theory]
    [InlineData("bash -c 'docker run --rm img; echo done'")] // the client hasn't shown up under the shell
    [InlineData("bash -c '/c/bin/codex.exe exec -s read-only prompt'")]
    [InlineData("bash -c 'node codex-companion.mjs status'")]
    [InlineData("bash -c 'claude -p summarize'")]
    public void A_shell_running_such_work_is_not_stuck(string command) =>
        Assert.Empty(Idle(40, Proc(11, 10, "bash.exe", command, startMinute: 5)).Stuck);

    [Theory]
    [InlineData("bash.exe", """bash -c "node \"${CLAUDE_PLUGIN_ROOT}/scripts/stop-review-gate-hook.mjs\"" """)]
    [InlineData("node.exe", @"C:\x\AppData\Local\OpenAI\Codex\runtimes\cua_node\abc\bin\node.exe C:\x\node_modules\@oai\cua-repl\bin\cua-repl.mjs")]
    [InlineData("node_repl.exe", @"C:\x\OpenAI\Codex\runtimes\cua_node\abc\bin\node_repl.exe")]
    [InlineData("codex-code-mode-host.exe", @"C:\x\.codex\packages\standalone\releases\0.166.0\bin\codex-code-mode-host.exe")]
    [InlineData("dart.exe", @"C:\x\flutter\bin\cache\dart-sdk\bin\dart.exe mcp-server")]
    [InlineData("AgentReclaim.exe", @"C:\x\AgentReclaim.exe mcp")]
    public void Hooks_and_agent_helpers_started_late_are_not_stuck(string name, string command) =>
        Assert.Empty(Idle(40, Proc(11, 10, name, command, startMinute: 5)).Stuck);

    [Fact]
    public void A_tree_with_another_agent_in_it_is_not_stuck()
    {
        var result = Idle(40, Proc(11, 10, "bash.exe", "bash codex-round.sh 20", startMinute: 5), Proc(12, 11, "codex.exe", "codex", startMinute: 5));
        Assert.DoesNotContain(result.Stuck, s => s.Node.Process.Pid == 11);
    }

    [Theory]
    [InlineData("bash -c 'git push'")]                      // the case this rule exists for: a prompt nobody can see
    [InlineData("bash -c 'ssh host uptime'")]               // ssh can ask for a password; a connection still blocks the stop
    [InlineData("bash -c 'python --timeout 3000 x.py'")]    // an option of the program, not a limit on it
    [InlineData("bash -c 'cat docker.log'")]
    [InlineData("bash -c 'python train.py --embedding-dim 64'")]
    public void Commands_that_really_can_wait_at_a_prompt_are_still_reported(string command) =>
        Assert.Single(Idle(20, Proc(11, 10, "bash.exe", command, startMinute: 5)).Stuck);

    // 2. One notice per episode

    [Fact]
    public void A_process_flapping_on_and_off_the_list_is_announced_once()
    {
        // The real case: an idle powershell.exe came and went every few polls for 3.5 hours, 399 notifications.
        var watch = new StuckWatch();
        var notices = 0;
        ProcessInfo[] Procs = [Proc(10, 1, "codex.exe", startMinute: 0), Proc(11, 10, "powershell.exe", startMinute: 5)];
        var stuck = new StuckProcess(
            new AgentSession(AgentKind.Codex, new ProcessNode(Procs[0], TimeSpan.Zero, [])),
            new ProcessNode(Procs[1], TimeSpan.FromMinutes(10), []), StuckKind.Waiting, TimeSpan.FromMinutes(10));

        AnalysisResult At(int minute, bool listed) =>
            new(Snapshot(minute, Procs), [], [], []) { Stuck = listed ? [stuck] : [] };

        for (var minute = 15; minute < 15 + 210; minute++)
            if (watch.Evaluate(At(minute, listed: minute % 2 == 0), enabled: true) is not null) notices++;

        Assert.Equal(1, notices);
    }

    [Fact]
    public void A_process_stuck_again_long_after_recovering_is_announced_again()
    {
        var watch = new StuckWatch();
        ProcessInfo[] Procs = [Proc(10, 1, "codex.exe", startMinute: 0), Proc(11, 10, "cmd.exe", startMinute: 5)];
        var stuck = new StuckProcess(
            new AgentSession(AgentKind.Codex, new ProcessNode(Procs[0], TimeSpan.Zero, [])),
            new ProcessNode(Procs[1], TimeSpan.FromMinutes(10), []), StuckKind.Waiting, TimeSpan.FromMinutes(10));
        AnalysisResult At(int minute, bool listed) =>
            new(Snapshot(minute, Procs), [], [], []) { Stuck = listed ? [stuck] : [] };

        var first = watch.Evaluate(At(15, listed: true), enabled: true);
        Assert.Contains("cmd.exe pid 11", first!.Processes);
        Assert.Null(watch.Evaluate(At(16, listed: false), enabled: true));
        Assert.Null(watch.Evaluate(At(74, listed: true), enabled: true));   // 59 min after it was last on the list
        Assert.Null(watch.Evaluate(At(75, listed: false), enabled: true));
        Assert.NotNull(watch.Evaluate(At(135, listed: true), enabled: true)); // an hour clear
    }

    [Fact]
    public void A_new_process_reusing_a_pid_is_a_new_notice()
    {
        var watch = new StuckWatch();
        StuckProcess Stuck(ProcessInfo p) => new(
            new AgentSession(AgentKind.Codex, new ProcessNode(Proc(10, 1, "codex.exe"), TimeSpan.Zero, [])),
            new ProcessNode(p, TimeSpan.FromMinutes(10), []), StuckKind.Waiting, TimeSpan.FromMinutes(10));
        var before = Proc(11, 10, "cmd.exe", startMinute: 5);
        var after = Proc(11, 10, "cmd.exe", startMinute: 20);

        Assert.NotNull(watch.Evaluate(new(Snapshot(15, before), [], [], []) { Stuck = [Stuck(before)] }, enabled: true));
        Assert.NotNull(watch.Evaluate(new(Snapshot(30, after), [], [], []) { Stuck = [Stuck(after)] }, enabled: true));
    }

    // 3. What Windows starts

    /// <summary>The 25 stopped as "Codex leftovers" on 2026-10-28, from the action log (duplicates and dllhost's name-only case aside).</summary>
    public static TheoryData<string, string, string> StartedByWindows => new()
    {
        { "SearchHost.exe", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", "-ServerName:CortanaUI.AppXstmw.mca" },
        { "SystemSettings.exe", @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", "-ServerName:microsoft.windows.immersivecontrolpanel" },
        { "PhoneExperienceHost.exe", @"C:\Program Files\WindowsApps\Microsoft.YourPhone_1.26082.121.0_x64__8wekyb3d8bbwe\PhoneExperienceHost.exe", "-ComServer:Background -Embedding" },
        { "LockApp.exe", @"C:\Windows\SystemApps\Microsoft.LockApp_cw5n1h2txyewy\LockApp.exe", "-ServerName:WindowsDefaultLockScreen.AppX.mca" },
        { "CHXSmartScreen.exe", @"C:\Windows\SystemApps\Microsoft.Windows.AppRep.ChxApp_cw5n1h2txyewy\CHXSmartScreen.exe", "-ServerName:App.AppXk7.mca" },
        { "ArmouryCrate.exe", @"C:\Program Files\WindowsApps\B9ECED6F.ArmouryCrate_6.5.14.0_x64__qmba6cd70vzyy\ArmouryCrate.exe", "-ServerName:App.AppXvfb" },
        { "TextInputHost.exe", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe", "-ServerName:InputApp.AppXk0.mca" },
        { "XboxPcApp.exe", @"C:\Program Files\WindowsApps\Microsoft.GamingApp_2609.1001.16.0_x64__8wekyb3d8bbwe\XboxPcApp.exe", "-ServerName:Microsoft.Xbox.AppL.AppXhg" },
        { "ShellExperienceHost.exe", @"C:\Windows\SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe", "-ServerName:App.AppXtk.mca" },
        { "CrossDeviceService.exe", @"C:\Program Files\WindowsApps\MicrosoftWindows.CrossDevice_1.26082.74.0_x64__cw5n1h2txyewy\CrossDeviceService.exe", "-RegisterProcessAsComServer -Embedding" },
        { "backgroundTaskHost.exe", @"C:\Windows\System32\backgroundTaskHost.exe", "-ServerName:App.AppXe9.mca" },
        { "RuntimeBroker.exe", @"C:\Windows\System32\RuntimeBroker.exe", "-Embedding" },
        { "DataExchangeHost.exe", @"C:\Windows\System32\DataExchangeHost.exe", "-Embedding" },
        { "MicrosoftStartFeedProvider.exe", @"C:\Program Files\WindowsApps\Microsoft.StartExperiencesApp_1.398.4.0_x64__8wekyb3d8bbwe\MicrosoftStartFeedProvider\MicrosoftStartFeedProvider.exe", "-Embedding" },
        { "ApplicationFrameHost.exe", @"C:\Windows\System32\ApplicationFrameHost.exe", "-Embedding" },
        { "XboxPcAppFT.exe", @"C:\Program Files\WindowsApps\Microsoft.GamingApp_2609.1001.16.0_x64__8wekyb3d8bbwe\XboxPcAppFT.exe", "-Embedding" },
        { "WidgetService.exe", @"C:\Program Files\WindowsApps\Microsoft.WidgetsPlatformRuntime_1.6.19.0_x64__8wekyb3d8bbwe\WidgetService\WidgetService.exe", "-RegisterProcessAsComServer" },
        { "WidgetBoard.exe", @"C:\Program Files\WindowsApps\MicrosoftWindows.Client.WebExperience_526.21100.40.0_x64__cw5n1h2txyewy\WidgetBoard.exe", "-RegisterProcessAsComServer -ServerName:Global.WidgetBoard" },
        { "unsecapp.exe", @"C:\Windows\System32\wbem\unsecapp.exe", "-Embedding" },
        { "Aac3572MbHal_x86.exe", @"C:\Program Files\ASUS\AacMB\Aac3572MbHal_x86.exe", "-Embedding" },
        { "UserOOBEBroker.exe", @"C:\Windows\System32\oobe\UserOOBEBroker.exe", "-Embedding" },
    };

    private static ProcessInfo WithPath(int pid, int parent, string name, string path, string? arguments) =>
        Proc(pid, parent, name, arguments is null ? $"\"{path}\"" : $"\"{path}\" {arguments}", startMinute: 5) with { ExePath = path };

    private static OrphanTree Leftover(params ProcessInfo[] chain)
    {
        ProcessNode? node = null;
        foreach (var p in chain.Reverse()) node = new ProcessNode(p, TimeSpan.FromHours(1), node is null ? [] : [node]);
        return new OrphanTree(node!, AgentKind.Codex, Attribution.Observed, "codex.exe (pid 10)", 10, 1);
    }

    [Theory]
    [MemberData(nameof(StartedByWindows))]
    public void What_Windows_started_is_never_stopped_whatever_it_seems_to_belong_to(string name, string path, string arguments)
    {
        var process = WithPath(30, 10, name, path, arguments);
        Assert.True(SafetyPolicy.IsStartedByWindows(process));

        Assert.False(SafetyPolicy.Check(Leftover(process), selfPid: 1).Allowed);
        Assert.Empty(new AutoRuleEngine().SafeLeftovers(
            new AnalysisResult(Snapshot(90, process), [], [Leftover(process)], []), new AppSettings(), selfPid: 1));
    }

    [Fact]
    public void The_reason_says_who_started_it()
    {
        var process = WithPath(30, 10, "ArmouryCrate.exe", @"C:\Program Files\WindowsApps\B9ECED6F.ArmouryCrate_6.5.14.0_x64__qmba6cd70vzyy\ArmouryCrate.exe", "-ServerName:App.AppXvfb");
        Assert.Contains("started by Windows", SafetyPolicy.Check(Leftover(process), selfPid: 1).BlockedReason);
    }

    [Fact]
    public void One_inside_a_tree_blocks_the_whole_tree()
    {
        var tree = Leftover(Proc(30, 10, "node.exe", "node x.js", startMinute: 5),
            WithPath(31, 30, "UserOOBEBroker.exe", @"C:\Windows\System32\oobe\UserOOBEBroker.exe", "-Embedding"));
        Assert.False(SafetyPolicy.Check(tree, selfPid: 1).Allowed);
    }

    [Theory]
    [InlineData("conhost.exe", @"C:\WINDOWS\system32\conhost.exe", "0x4")]
    [InlineData("cmd.exe", @"C:\WINDOWS\system32\cmd.exe", "/c npm test")]
    [InlineData("powershell.exe", @"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile -Command Start-Sleep 5")]
    [InlineData("curl.exe", @"C:\WINDOWS\system32\curl.exe", "http://localhost")]
    [InlineData("ssh.exe", @"C:\WINDOWS\System32\OpenSSH\ssh.exe", "host")]
    [InlineData("python3.12.exe", @"C:\Program Files\WindowsApps\PythonSoftwareFoundation.Python.3.12_3.12.2032.0_x64__qbz5n2kfra8p0\python3.12.exe", "train.py --embedding-dim 64")]
    [InlineData("java.exe", @"C:\Users\x\.jdks\jbr-21\bin\java.exe", "-Xmx1g org.gradle.launcher.daemon.bootstrap.GradleDaemon")]
    public void An_agents_own_tools_can_still_be_stopped(string name, string path, string arguments)
    {
        var tool = WithPath(30, 10, name, path, arguments);
        Assert.False(SafetyPolicy.IsStartedByWindows(tool));
        Assert.True(SafetyPolicy.Check(Leftover(tool), selfPid: 1).Allowed);
    }

    [Fact]
    public void Many_leftovers_appearing_at_once_are_left_to_a_person()
    {
        var engine = new AutoRuleEngine();
        var settings = new AppSettings { AutoStopLeftovers = true };
        var tools = Enumerable.Range(0, AutoRuleEngine.BurstSize).Select(i => Proc(100 + i, 1, "tool.exe", startMinute: 5)).ToArray();
        var analysis = new AnalysisResult(Snapshot(90, tools), [], tools.Select(t => Leftover(t)).ToList(), []);

        Assert.Empty(engine.LeftoversToStop(analysis, settings, selfPid: 1));
        Assert.Equal(AutoRuleEngine.BurstSize, engine.TakeBurstNotice());
        Assert.Null(engine.TakeBurstNotice());

        // Later polls still leave them alone, and don't report them twice.
        Assert.Empty(engine.LeftoversToStop(analysis, settings, selfPid: 1));
        Assert.Null(engine.TakeBurstNotice());

        // A person can still free them.
        Assert.Equal(AutoRuleEngine.BurstSize, engine.SafeLeftovers(analysis, settings, selfPid: 1).Count);
    }

    [Fact]
    public void A_burst_still_counts_when_automatic_stopping_is_off_at_the_time()
    {
        var engine = new AutoRuleEngine();
        var tools = Enumerable.Range(0, AutoRuleEngine.BurstSize).Select(i => Proc(100 + i, 1, "tool.exe", startMinute: 5)).ToArray();
        var analysis = new AnalysisResult(Snapshot(90, tools), [], tools.Select(t => Leftover(t)).ToList(), []);

        Assert.Empty(engine.LeftoversToStop(analysis, new AppSettings { AutoStopLeftovers = false }, selfPid: 1));
        Assert.Empty(engine.LeftoversToStop(analysis, new AppSettings { AutoStopLeftovers = true }, selfPid: 1));
    }

    [Fact]
    public void A_burst_member_whose_parent_exits_stays_with_a_person()
    {
        var engine = new AutoRuleEngine();
        var settings = new AppSettings { AutoStopLeftovers = true };
        var child = Proc(200, 100, "child.exe", startMinute: 5);
        var trees = Enumerable.Range(0, AutoRuleEngine.BurstSize).Select(i => i == 0
            ? Leftover(Proc(100, 1, "tool.exe", startMinute: 5), child)
            : Leftover(Proc(100 + i, 1, "tool.exe", startMinute: 5))).ToList();

        Assert.Empty(engine.LeftoversToStop(new AnalysisResult(Snapshot(90, child), [], trees, []), settings, selfPid: 1));
        Assert.Empty(engine.LeftoversToStop(new AnalysisResult(Snapshot(91, child), [], [Leftover(child)], []), settings, selfPid: 1));
    }

    [Fact]
    public void Fewer_new_leftovers_than_a_burst_are_handled_as_usual()
    {
        var tools = Enumerable.Range(0, AutoRuleEngine.BurstSize - 1).Select(i => Proc(100 + i, 1, "tool.exe", startMinute: 5)).ToArray();
        var engine = new AutoRuleEngine();
        var analysis = new AnalysisResult(Snapshot(90, tools), [], tools.Select(t => Leftover(t)).ToList(), []);

        Assert.Equal(tools.Length, engine.LeftoversToStop(analysis, new AppSettings { AutoStopLeftovers = true }, selfPid: 1).Count);
        Assert.Null(engine.TakeBurstNotice());
    }

    // 4. Time as a leftover, not just idle time

    /// <summary>A child idle under its agent, whose agent exits at <paramref name="agentExit"/>.</summary>
    private static ProcessInfo[] AgentExitsAt(int minute, int agentExit) => minute < agentExit
        ? [Proc(10, 1, "codex.exe", startMinute: 0, cpu: 1), Proc(11, 10, "node_repl.exe", startMinute: 5)]
        : [Proc(11, 10, "node_repl.exe", startMinute: 5)];

    [Fact]
    public void A_leftover_already_idle_when_its_agent_exits_gets_the_full_wait()
    {
        // The real case: 16 idle children became stop candidates in the poll where their Codex exited.
        var analyzer = NewAnalyzer();
        var settings = new AppSettings { AutoStopLeftovers = true, AutoStopIdleMinutes = 15 };
        var engine = new AutoRuleEngine();

        var atExit = Run(analyzer, 5, 60, m => AgentExitsAt(m, 60));
        Assert.Equal(TimeSpan.Zero, Assert.Single(atExit.Orphans).LeftoverFor);
        Assert.True(Assert.Single(atExit.Orphans).Root.IdleFor >= TimeSpan.FromMinutes(15));
        Assert.Empty(engine.LeftoversToStop(atExit, settings, selfPid: 1));

        Assert.Empty(engine.LeftoversToStop(Run(analyzer, 61, 74, m => AgentExitsAt(m, 60)), settings, selfPid: 1));
        var after = Run(analyzer, 75, 75, m => AgentExitsAt(m, 60));
        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(after.Orphans).LeftoverFor);
        Assert.Single(engine.LeftoversToStop(after, settings, selfPid: 1));
    }

    [Fact]
    public void A_reported_session_end_is_still_the_start_of_the_wait()
    {
        var analyzer = NewAnalyzer();
        var settings = new AppSettings { AutoStopLeftovers = true, AutoStopIdleMinutes = 15, HookGraceSeconds = 60 };
        var engine = new AutoRuleEngine();
        var agent = Proc(10, 1, "codex.exe", startMinute: 0, cpu: 1);
        engine.MarkSessionEnded(agent.Key, T0.AddMinutes(60));

        Assert.Empty(engine.LeftoversToStop(Run(analyzer, 5, 60, m => AgentExitsAt(m, 60)), settings, selfPid: 1));
        Assert.Single(engine.LeftoversToStop(Run(analyzer, 61, 61, m => AgentExitsAt(m, 60)), settings, selfPid: 1));
    }

    [Fact]
    public void A_process_stays_a_leftover_from_when_it_first_became_one_when_its_parent_exits()
    {
        var analyzer = NewAnalyzer();
        ProcessInfo[] At(int minute) => minute < 20
            ? [Proc(10, 1, "codex.exe", startMinute: 0), Proc(11, 10, "bash.exe", startMinute: 5), Proc(12, 11, "node.exe", "node x.js", startMinute: 5)]
            : minute < 30
                ? [Proc(11, 10, "bash.exe", startMinute: 5), Proc(12, 11, "node.exe", "node x.js", startMinute: 5)]
                : [Proc(12, 11, "node.exe", "node x.js", startMinute: 5)];

        var result = Run(analyzer, 5, 30, At);
        var tree = Assert.Single(result.Orphans);
        Assert.Equal(12, tree.Root.Process.Pid);
        Assert.Equal(TimeSpan.FromMinutes(10), tree.LeftoverFor); // since the agent exited at 20, not the shell at 30
    }

    // 5. Sleep

    [Fact]
    public void A_sleep_does_not_make_a_working_command_look_stuck()
    {
        var analyzer = NewAnalyzer(gapAfter: TimeSpan.FromMinutes(5));
        ProcessInfo[] Tree(int _) => [Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1), Proc(11, 10, "bash.exe", "bash -c 'git push'", startMinute: 5)];

        Run(analyzer, 5, 12, Tree);                                   // 7 minutes idle, then the lid closes
        Assert.Empty(Run(analyzer, 200, 201, Tree).Stuck);            // woke 3 hours later
        Assert.Empty(Run(analyzer, 202, 202, Tree).Stuck);            // 9 observed minutes
        var stuck = Assert.Single(Run(analyzer, 203, 203, Tree).Stuck);
        Assert.Equal(10, (int)stuck.For.TotalMinutes);
    }

    [Fact]
    public void A_sleep_does_not_make_a_leftover_old_enough_to_stop()
    {
        var analyzer = NewAnalyzer(gapAfter: TimeSpan.FromMinutes(5));
        var settings = new AppSettings { AutoStopLeftovers = true, AutoStopIdleMinutes = 15 };
        var engine = new AutoRuleEngine();

        Run(analyzer, 5, 30, m => AgentExitsAt(m, 30));
        var woke = Run(analyzer, 200, 200, m => AgentExitsAt(m, 30)); // left over just before the sleep
        var tree = Assert.Single(woke.Orphans);
        Assert.Equal(TimeSpan.Zero, tree.LeftoverFor);
        Assert.Empty(engine.LeftoversToStop(woke, settings, selfPid: 1));

        Assert.Empty(engine.LeftoversToStop(Run(analyzer, 201, 214, m => AgentExitsAt(m, 30)), settings, selfPid: 1));
        Assert.Single(engine.LeftoversToStop(Run(analyzer, 215, 215, m => AgentExitsAt(m, 30)), settings, selfPid: 1));
    }

    [Fact]
    public void A_sleep_does_not_make_a_busy_process_look_busy_for_longer()
    {
        var analyzer = new Analyzer(new LineageStore(), new AnalyzerOptions
        {
            CoreCount = 4,
            SpinningGap = TimeSpan.FromMinutes(2),
            UnobservedGapAfter = TimeSpan.FromMinutes(5),
        });
        ProcessInfo[] Tree(int _) => [Proc(10, 1, "claude.exe", startMinute: 0), Proc(11, 10, "python.exe", "python x.py", startMinute: 1, cpu: 30)];

        Run(analyzer, 1, 10, Tree);
        var woke = Run(analyzer, 300, 300, Tree);                     // 9 busy minutes seen so far
        Assert.DoesNotContain(woke.Stuck, s => s.Kind == StuckKind.Spinning);
    }

    [Fact]
    public void Short_gaps_still_count()
    {
        var analyzer = NewAnalyzer(gapAfter: TimeSpan.FromMinutes(5));
        ProcessInfo[] Tree(int _) => [Proc(10, 1, "claude.exe", startMinute: 0, cpu: 1), Proc(11, 10, "bash.exe", "bash -c 'git push'", startMinute: 5)];

        Run(analyzer, 5, 5, Tree);
        Run(analyzer, 9, 9, Tree);                                    // 4 minutes: a busy machine, not a sleep
        Run(analyzer, 13, 13, Tree);
        Assert.Single(Run(analyzer, 15, 15, Tree).Stuck);

        var noOption = NewAnalyzer();
        Run(noOption, 5, 5, Tree);
        Assert.Single(Run(noOption, 15, 15, Tree).Stuck);             // without the option, every gap counts
    }
}
