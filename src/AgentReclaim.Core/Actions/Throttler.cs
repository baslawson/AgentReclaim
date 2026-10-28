using System.Runtime.InteropServices;
using AgentReclaim.Core.Model;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Actions;

/// <summary>
/// Lowers a runaway build's priority and turns on EcoQoS, so the agent, IDE and desktop stay responsive.
/// The build still finishes; it just yields to interactive work.
/// </summary>
public static class Throttler
{
    public static bool TryThrottle(ProcessInfo process, out string? error)
    {
        using var handle = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)process.Pid);
        if (handle.IsInvalid)
        {
            error = "access denied";
            return false;
        }

        // Make sure the PID still belongs to the process we decided about.
        if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created != process.Key.StartTicks)
        {
            error = "process exited";
            return false;
        }

        var ok = SetPriorityClass(handle.DangerousGetHandle(), BELOW_NORMAL_PRIORITY_CLASS);
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        };
        // EcoQoS is best effort (needs Windows 11); the priority change alone already helps.
        SetProcessInformation(handle.DangerousGetHandle(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());

        error = ok ? null : $"SetPriorityClass failed ({Marshal.GetLastWin32Error()})";
        return ok;
    }
}
