using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Tests;

internal static class FakeProcesses
{
    public static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    public static ProcessInfo Proc(int pid, int parentPid, string name, string? cmd = null, int startMinute = 0,
        long mb = 10, double cpu = 0, int[]? ports = null, int connections = 0)
    {
        var start = T0.AddMinutes(startMinute);
        return new ProcessInfo(new ProcessKey(pid, start.ToFileTimeUtc()), parentPid, name, null, cmd, start,
            mb * 1024 * 1024, mb * 1024 * 1024, cpu, ports ?? [], connections);
    }

    public static ProcessSnapshot Snapshot(int minute, params ProcessInfo[] processes) =>
        new(T0.AddMinutes(minute), processes, new SystemStats(16L << 30, 8L << 30, 10));
}
