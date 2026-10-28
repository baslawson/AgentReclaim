using System.Diagnostics;
using System.Text.Json;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Rules;
using AgentReclaim.Core.Settings;
using AgentReclaim.Core.Updates;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

/// <summary>Regression tests for the third Codex adversarial review.</summary>
[Collection("Global path overrides")]
public class CodexRound3Tests
{
    private static readonly AppSettings AutoOn = new() { AutoStopLeftovers = true, AutoStopIdleMinutes = 15, HookGraceSeconds = 60 };

    // 1. Only AgentReclaim's own single-file exe may replace itself.

    [Fact]
    public void The_test_host_is_not_treated_as_a_self_updating_build()
    {
        Assert.False(AppInfo.IsSingleFileBuild);
        Assert.False(AppInfo.IsOwnBundledExe(@"C:\Program Files\dotnet\dotnet.exe", ""));
        Assert.False(AppInfo.IsOwnBundledExe(Environment.ProcessPath, ""));
        Assert.False(AppInfo.IsOwnBundledExe(null, ""));
    }

    [Fact]
    public void A_dev_build_next_to_its_dlls_is_not_a_bundle()
    {
        var dir = Directory.CreateTempSubdirectory("ar-bundle-").FullName;
        try
        {
            var exe = Path.Combine(dir, "AgentReclaim.exe");
            File.WriteAllText(exe, "");
            Assert.True(AppInfo.IsOwnBundledExe(exe, ""));
            Assert.False(AppInfo.IsOwnBundledExe(exe, Path.Combine(dir, "AgentReclaim.Core.dll"))); // loaded from disk

            File.WriteAllText(Path.Combine(dir, "AgentReclaim.Core.dll"), "");
            Assert.False(AppInfo.IsOwnBundledExe(exe, ""));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // 2. Rollback supervisor: a version that reports ready is kept.

    [Fact]
    public void Supervisor_stands_down_once_the_new_version_reports_ready()
    {
        var dir = Directory.CreateTempSubdirectory("ar-supervise-").FullName;
        try
        {
            var ready = Path.Combine(dir, "ready.flag");
            File.WriteAllText(ready, "0.2.1");
            var exitCode = UpdateInstaller.RunSupervisor(Path.Combine(dir, "AgentReclaim.exe"), Environment.ProcessId, ready, "0.2.1",
                TimeSpan.FromSeconds(5));
            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(ready));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // 3. A hook-reported session end applies to that agent instance only, not to a later one with the same pid.

    private static AnalysisResult IdleLeftover(int idleMinutes)
    {
        var analyzer = new Analyzer(new LineageStore());
        var server = Proc(12, 10, "node.exe", startMinute: 1);
        analyzer.Analyze(Snapshot(2, Proc(10, 1, "claude.exe"), server));
        analyzer.Analyze(Snapshot(3, server));
        return analyzer.Analyze(Snapshot(3 + idleMinutes, server));
    }

    [Fact]
    public void Leftovers_remember_the_exact_owner_instance()
    {
        var orphan = Assert.Single(IdleLeftover(5).Orphans);
        Assert.Equal(Proc(10, 1, "claude.exe").Key, orphan.Owner);
    }

    [Fact]
    public void Session_end_for_a_reused_pid_does_not_shorten_the_wait()
    {
        var result = IdleLeftover(5); // under the 15 min threshold; only a matching session end allows it
        var now = result.Snapshot.TakenAtUtc;

        var otherInstance = new AutoRuleEngine();
        otherInstance.MarkSessionEnded(Proc(10, 1, "claude.exe", startMinute: 4).Key, now.AddMinutes(-2));
        Assert.Empty(otherInstance.LeftoversToStop(result, AutoOn, 1));

        var unknownStart = new AutoRuleEngine();
        unknownStart.MarkSessionEnded(new ProcessKey(10, 0), now.AddMinutes(-2));
        Assert.Empty(unknownStart.LeftoversToStop(result, AutoOn, 1));

        var sameInstance = new AutoRuleEngine();
        sameInstance.MarkSessionEnded(Proc(10, 1, "claude.exe").Key, now.AddMinutes(-2));
        Assert.Single(sameInstance.LeftoversToStop(result, AutoOn, 1));
    }

    [Fact]
    public void Owner_start_time_survives_a_lineage_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-lineage-{Guid.NewGuid():N}.json");
        try
        {
            var store = new LineageStore(path);
            store.Record(new ProcessKey(12, 5), new LineageEntry(AgentKind.ClaudeCode, 10, "claude.exe", OwnerStartTicks: 42));
            store.SaveIfDirty();

            Assert.Equal(42, new LineageStore(path, readOnly: true).Get(new ProcessKey(12, 5))!.OwnerStartTicks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 4. Malformed release data is a failed check, never a crash or "up to date".

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("""{ "tag_name": 5 }""")]
    [InlineData("""{ "tag_name": "latest" }""")]
    public void Malformed_releases_raise_a_handled_error(string json)
    {
        Assert.Throws<InvalidDataException>(() => UpdateChecker.Parse(json));
    }

    [Fact]
    public void Invalid_json_raises_a_json_error()
    {
        Assert.ThrowsAny<JsonException>(() => UpdateChecker.Parse("{ not json"));
    }

    [Fact]
    public void Odd_assets_are_skipped_rather_than_crashing()
    {
        var update = UpdateChecker.Parse("""
            { "tag_name": "v1.0.0", "draft": "no", "body": 3,
              "assets": [ 7, { "name": 1 }, { "name": "AgentReclaim-1.0.0-win-x64.zip" },
                          { "name": "AgentReclaim-1.0.0-win-x64.zip", "browser_download_url": "https://example/zip" } ] }
            """)!;
        Assert.Equal(new Version(1, 0, 0), update.Version);
        Assert.Equal("https://example/zip", update.ZipUrl);
        Assert.Null(update.ChecksumUrl);
        Assert.Equal("", update.Notes);

        Assert.Null(UpdateChecker.Parse("""{ "tag_name": "v1.0.0", "assets": {} }""")!.ZipUrl);
    }

    // 5. Retention also applies to a long-running app that crosses midnight.

    [Fact]
    public void Old_logs_are_pruned_at_the_daily_rollover()
    {
        var dir = Directory.CreateTempSubdirectory("ar-logroll-").FullName;
        var day = new DateTime(2026, 10, 28, 23, 59, 0);
        var previousClock = Log.Now;
        try
        {
            Log.Now = () => day;
            Log.Initialize(dir);
            var oldest = Path.Combine(dir, $"agentreclaim-{day.AddDays(-6):yyyyMMdd}.log");
            File.WriteAllText(oldest, "old");
            Log.Info("before midnight");
            Assert.True(File.Exists(oldest)); // still within 7 days

            day = day.AddMinutes(2); // next day, same process
            Log.Info("after midnight");

            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(Path.Combine(dir, "agentreclaim-20261028.log")));
            Assert.True(File.Exists(Path.Combine(dir, "agentreclaim-20261029.log")));
        }
        finally
        {
            Log.Now = previousClock;
            Log.Initialize(Path.Combine(Path.GetTempPath(), "ar-log-unused"));
            Directory.Delete(dir, recursive: true);
        }
    }

    // Round 4: log names use Gregorian dates under any culture, and odd names fall back to the write time.

    [Fact]
    public void Log_names_and_retention_ignore_the_culture_calendar()
    {
        var dir = Directory.CreateTempSubdirectory("ar-logcal-").FullName;
        var previousClock = Log.Now;
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH"); // Buddhist calendar
            Log.Now = () => new DateTime(2026, 10, 28, 12, 0, 0);

            var staleThai = Path.Combine(dir, "agentreclaim-25690901.log"); // "year 2569", really two months old
            var freshThai = Path.Combine(dir, "agentreclaim-25691027.log");
            File.WriteAllText(staleThai, "");
            File.WriteAllText(freshThai, "");
            File.SetLastWriteTime(staleThai, new DateTime(2026, 9, 1));
            File.SetLastWriteTime(freshThai, new DateTime(2026, 10, 27));

            Log.Initialize(dir);
            Log.Info("hello");

            Assert.True(File.Exists(Path.Combine(dir, "agentreclaim-20261028.log")));
            Assert.False(File.Exists(staleThai));
            Assert.True(File.Exists(freshThai));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            Log.Now = previousClock;
            Log.Initialize(Path.Combine(Path.GetTempPath(), "ar-log-unused"));
            Directory.Delete(dir, recursive: true);
        }
    }
}
