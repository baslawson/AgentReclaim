using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Native;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Collection;

/// <summary>
/// Takes a snapshot of every process. It uses Toolhelp and per-process handle queries instead
/// of WMI, so a poll every few seconds costs milliseconds rather than waking up WmiPrvSE.
/// </summary>
public sealed class ProcessCollector
{
    private readonly Dictionary<ProcessKey, (string? Exe, string? CommandLine)> _staticInfo = new();
    private Dictionary<ProcessKey, long> _previousCpu = new();
    private long _previousTimestamp;
    private (long Idle, long Kernel, long User)? _previousSystem;

    public ProcessSnapshot Collect()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed100ns = _previousTimestamp == 0
            ? 0
            : (timestamp - _previousTimestamp) * 10_000_000.0 / Stopwatch.Frequency;

        var tcp = TcpTable.Read();
        var processes = new List<ProcessInfo>();
        var currentCpu = new Dictionary<ProcessKey, long>();

        foreach (var (pid, parentPid, name) in EnumerateProcesses())
        {
            var info = Describe(pid, parentPid, name, tcp, elapsed100ns, currentCpu);
            if (info is not null) processes.Add(info);
        }

        // Forget cached command lines for processes that have exited.
        foreach (var key in _staticInfo.Keys.Where(k => !currentCpu.ContainsKey(k)).ToList())
            _staticInfo.Remove(key);

        _previousCpu = currentCpu;
        _previousTimestamp = timestamp;

        return new ProcessSnapshot(DateTime.UtcNow, processes, ReadSystemStats(), tcp.Complete);
    }

    private ProcessInfo? Describe(int pid, int parentPid, string name, TcpTable tcp, double elapsed100ns,
        Dictionary<ProcessKey, long> currentCpu)
    {
        long startTicks = 0, cpuTicks = 0, privateBytes = 0, workingSet = 0;
        string? exe = null, commandLine = null;

        var handle = pid == 0 ? IntPtr.Zero : NativeProcess.Open(pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                if (GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
                {
                    startTicks = creation;
                    cpuTicks = kernel + user;
                }

                if (K32GetProcessMemoryInfo(handle, out var counters, (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>()))
                {
                    privateBytes = (long)counters.PrivateUsage;
                    workingSet = (long)counters.WorkingSetSize;
                }

                var key = new ProcessKey(pid, startTicks);
                if (!_staticInfo.TryGetValue(key, out var cached))
                {
                    cached = (NativeProcess.GetImagePath(handle), NativeProcess.GetCommandLine(handle));
                    _staticInfo[key] = cached;
                }
                (exe, commandLine) = cached;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        var processKey = new ProcessKey(pid, startTicks);
        currentCpu[processKey] = cpuTicks;

        var cpuPercent = 0.0;
        if (elapsed100ns > 0 && _previousCpu.TryGetValue(processKey, out var previous))
            cpuPercent = Math.Max(0, (cpuTicks - previous) / elapsed100ns / Environment.ProcessorCount * 100);

        return new ProcessInfo(
            processKey,
            parentPid,
            name,
            exe,
            commandLine,
            startTicks == 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(startTicks),
            privateBytes,
            workingSet,
            cpuPercent,
            tcp.ListeningPorts.TryGetValue(pid, out var ports) ? ports.ToArray() : [],
            tcp.Established.GetValueOrDefault(pid));
    }

    /// <summary>
    /// Lists every process. Throws instead of returning a partial list: an empty or truncated
    /// snapshot would look like "everything exited" and wipe the lineage history.
    /// </summary>
    internal static List<(int Pid, int ParentPid, string Name)> EnumerateProcesses()
    {
        const int ERROR_NO_MORE_FILES = 18;

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not snapshot the process list.");

        try
        {
            var result = new List<(int, int, string)>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the process list.");
            do
            {
                result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            } while (Process32NextW(snapshot, ref entry));

            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_NO_MORE_FILES)
                throw new Win32Exception(error, "The process list ended early.");
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>A cheap identity-only listing (no memory, CPU or command lines), used right before stopping processes.</summary>
    public static IReadOnlyList<(ProcessKey Key, int ParentPid, string Name)> ReadIdentities() =>
        EnumerateProcesses()
            .Select(p => (new ProcessKey(p.Pid, NativeProcess.GetStartTicks(p.Pid) ?? 0), p.ParentPid, p.Name))
            .ToList();

    private SystemStats ReadSystemStats()
    {
        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref memory);

        var cpu = 0.0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (_previousSystem is var (pIdle, pKernel, pUser))
            {
                // Kernel time includes idle time.
                var total = (kernel - pKernel) + (user - pUser);
                if (total > 0) cpu = (1.0 - (double)(idle - pIdle) / total) * 100;
            }
            _previousSystem = (idle, kernel, user);
        }

        return new SystemStats((long)memory.ullTotalPhys, (long)memory.ullAvailPhys, cpu);
    }
}
