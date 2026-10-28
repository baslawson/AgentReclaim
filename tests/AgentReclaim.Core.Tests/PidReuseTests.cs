using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Model;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

/// <summary>
/// Seen on a real machine: wininit.exe's parent PID (1364, long gone) was reused by a Codex child. A non-admin app
/// can't read system processes' start times, and the PID-reuse check was skipped when it couldn't, so wininit and
/// every service under it joined the Codex session, then became its "leftovers" when that child exited.
/// </summary>
public class PidReuseTests
{
    private static ProcessInfo System(int pid, int parentPid, string name) =>
        Proc(pid, parentPid, name) with { Key = new ProcessKey(pid, 0), StartTimeUtc = DateTime.MinValue };

    private static readonly ProcessInfo Codex = Proc(10, 1, "codex.exe", startMinute: 0);
    private static readonly ProcessInfo Repl = Proc(1364, 10, "node_repl.exe", startMinute: 2); // took the dead parent's PID
    private static readonly ProcessInfo Wininit = System(1468, 1364, "wininit.exe");
    private static readonly ProcessInfo Lsass = System(1636, 1468, "lsass.exe");
    private static readonly ProcessInfo Services = System(1608, 1468, "services.exe");
    private static readonly ProcessInfo RustDesk = Proc(5004, 1608, "RustDesk.exe", startMinute: -60); // readable, under an unreadable parent

    [Fact]
    public void A_reused_pid_never_pulls_system_processes_into_a_session()
    {
        var lineage = new LineageStore();
        var analyzer = new Analyzer(lineage);
        var result = analyzer.Analyze(Snapshot(5, Codex, Repl, Wininit, Lsass, Services, RustDesk));

        var session = Assert.Single(result.Sessions);
        Assert.Equal(2, session.Root.TotalCount); // codex and its own child, nothing else
        foreach (var p in new[] { Wininit, Lsass, Services, RustDesk })
            Assert.Null(lineage.Get(p.Key));

        // The child exits: nothing becomes a leftover.
        Assert.Empty(analyzer.Analyze(Snapshot(6, Codex, Wininit, Lsass, Services, RustDesk)).Orphans);
    }

    [Fact]
    public void Lineage_without_a_start_time_is_never_recorded_or_trusted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-lineage-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """[{"Pid":1636,"StartTicks":0,"Kind":1,"OwnerPid":10,"OwnerName":"codex.exe","Guessed":false,"OwnerStartTicks":5,"Agents":""}]""");
            var store = new LineageStore(path);
            Assert.Null(store.Get(new ProcessKey(1636, 0)));
            store.Record(new ProcessKey(1636, 0), new LineageEntry(AgentKind.Codex, 10, "codex.exe"));
            Assert.Null(store.Get(new ProcessKey(1636, 0)));
            store.SaveIfDirty();
            Assert.DoesNotContain("1636", File.ReadAllText(path)); // dropped from the file too
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Nothing_with_an_unknown_start_time_is_ever_stopped()
    {
        var node = new ProcessNode(System(4540, 1608, "mytool.exe"), TimeSpan.FromHours(1), []);
        var check = SafetyPolicy.CheckSubtree(node, selfPid: 1);
        Assert.False(check.Allowed);
        Assert.Contains("Couldn't read when", check.BlockedReason);

        Assert.True(SafetyPolicy.IsProtected("spoolsv.exe"));
        Assert.True(SafetyPolicy.IsProtected("LsaIso.exe"));
        Assert.True(SafetyPolicy.IsProtected("WmiPrvSE.exe"));
    }

    [Fact]
    public void The_lineage_file_moved_so_old_untrusted_records_are_left_behind()
    {
        Assert.EndsWith("lineage-v2.json", AppPaths.LineageFile);
        Assert.EndsWith("lineage.json", AppPaths.LegacyLineageFile);
    }
}
