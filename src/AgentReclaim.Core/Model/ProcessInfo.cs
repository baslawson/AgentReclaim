namespace AgentReclaim.Core.Model;

/// <summary>
/// Identifies one process instance. PIDs get reused on Windows, so a PID alone is not
/// enough to tell "the same process" apart from a new one that happens to share its number.
/// </summary>
public readonly record struct ProcessKey(int Pid, long StartTicks)
{
    public override string ToString() => $"{Pid}@{StartTicks}";
}

public sealed record ProcessInfo(
    ProcessKey Key,
    int ParentPid,
    string Name,
    string? ExePath,
    string? CommandLine,
    DateTime StartTimeUtc,
    long PrivateBytes,
    long WorkingSetBytes,
    double CpuPercent,
    IReadOnlyList<int> ListeningPorts,
    int EstablishedConnections)
{
    public int Pid => Key.Pid;
}

public sealed record SystemStats(long TotalMemoryBytes, long AvailableMemoryBytes, double CpuPercent)
{
    public long UsedMemoryBytes => TotalMemoryBytes - AvailableMemoryBytes;
    public double MemoryLoad => TotalMemoryBytes == 0 ? 0 : (double)UsedMemoryBytes / TotalMemoryBytes;
}

/// <param name="NetworkComplete">False if the TCP tables couldn't be read, so connection counts are unknown (not zero).</param>
public sealed record ProcessSnapshot(DateTime TakenAtUtc, IReadOnlyList<ProcessInfo> Processes, SystemStats System, bool NetworkComplete = true);
