using System.Diagnostics;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Collection;
using Xunit.Abstractions;

namespace AgentReclaim.Core.Tests;

/// <summary>Runs the real collector against this machine. Read-only: it never stops anything.</summary>
public class LiveMachineTests(ITestOutputHelper output)
{
    [Fact]
    public void Collector_sees_this_process_with_details()
    {
        var snapshot = new ProcessCollector().Collect();
        var self = snapshot.Processes.Single(p => p.Pid == Environment.ProcessId);

        Assert.NotNull(self.CommandLine);
        Assert.NotNull(self.ExePath);
        Assert.True(self.PrivateBytes > 0);
        Assert.True(snapshot.System.TotalMemoryBytes > 0);
    }

    [Fact]
    public void Report_current_agent_usage()
    {
        var collector = new ProcessCollector();
        collector.Collect();
        Thread.Sleep(500); // second sample gives CPU percentages

        var stopwatch = Stopwatch.StartNew();
        var snapshot = collector.Collect();
        var collectMs = stopwatch.ElapsedMilliseconds;
        var result = new Analyzer(new LineageStore()).Analyze(snapshot);

        output.WriteLine($"{snapshot.Processes.Count} processes collected in {collectMs} ms");
        foreach (var s in result.Sessions)
            output.WriteLine($"SESSION {AgentSignatures.DisplayName(s.Kind)} pid {s.Root.Process.Pid}: {s.Root.TotalCount} procs, {s.Root.TotalPrivateBytes >> 20} MB");
        foreach (var o in result.Orphans)
            output.WriteLine($"ORPHAN  {o.Root.Process.Name} pid {o.Root.Process.Pid} ({o.Attribution}): {o.Root.TotalCount} procs, {o.Root.TotalPrivateBytes >> 20} MB :: {o.Root.Process.CommandLine}");
        foreach (var f in result.Findings)
            output.WriteLine($"FINDING {f.Title}: {f.Bytes >> 20} MB");
    }
}
