using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

public class SafetyPolicyTests
{
    private static OrphanTree Tree(Core.Model.ProcessInfo root, TimeSpan idle, params Core.Model.ProcessInfo[] children) =>
        new(new ProcessNode(root, idle, children.Select(c => new ProcessNode(c, idle, [])).ToList()),
            AgentKind.ClaudeCode, Attribution.Observed, "claude.exe (pid 10)");

    [Fact]
    public void Idle_orphan_is_allowed_without_warnings()
    {
        var check = SafetyPolicy.Check(Tree(Proc(11, 10, "node.exe"), TimeSpan.FromMinutes(30)), selfPid: 1);

        Assert.True(check.Allowed);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public void Protected_process_in_the_tree_is_blocked()
    {
        var check = SafetyPolicy.Check(
            Tree(Proc(11, 10, "bash.exe"), TimeSpan.FromMinutes(30), Proc(12, 11, "Code.exe")), selfPid: 1);

        Assert.False(check.Allowed);
        Assert.Contains("Code.exe", check.BlockedReason);
    }

    [Fact]
    public void Tree_containing_ourselves_is_blocked()
    {
        var check = SafetyPolicy.Check(Tree(Proc(11, 10, "node.exe"), TimeSpan.FromMinutes(30)), selfPid: 11);

        Assert.False(check.Allowed);
    }

    [Fact]
    public void Ports_connections_and_recent_cpu_produce_warnings()
    {
        var check = SafetyPolicy.Check(
            Tree(Proc(11, 10, "node.exe", ports: [5173], connections: 2), TimeSpan.FromSeconds(10)), selfPid: 1);

        Assert.True(check.Allowed);
        Assert.Equal(3, check.Warnings.Count);
    }
}
