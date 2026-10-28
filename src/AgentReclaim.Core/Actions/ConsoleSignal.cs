using System.Diagnostics;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Actions;

/// <summary>
/// Sends Ctrl+C to a console process tree, the way a user pressing Ctrl+C in its terminal would.
///
/// Ctrl+C goes to every process on the console, including whoever sends it, so the sending is done
/// by a short-lived helper process (`AgentReclaim.exe send-ctrl-c ...`). The main app never attaches
/// to anyone else's console. The helper only sends when every process on that console is one the
/// user approved, so a terminal the user is working in is never interrupted.
/// </summary>
public static class ConsoleSignal
{
    /// <summary>The exe that understands `send-ctrl-c`. Null (e.g. in tests) skips the graceful step.</summary>
    public static string? HelperPath { get; set; }

    public static bool TrySendCtrlC(IEnumerable<int> candidatePids, IReadOnlySet<int> approvedPids)
    {
        if (HelperPath is null || !File.Exists(HelperPath)) return false;

        var start = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("send-ctrl-c");
        start.ArgumentList.Add(string.Join(',', candidatePids));
        start.ArgumentList.Add(string.Join(',', approvedPids));

        try
        {
            using var helper = Process.Start(start);
            if (helper is null || !helper.WaitForExit(TimeSpan.FromSeconds(5))) return false;
            return helper.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The helper side. Exit code 0 means Ctrl+C was sent.</summary>
    public static int RunHelper(string candidates, string approved)
    {
        var approvedPids = Parse(approved).ToHashSet();
        approvedPids.Add(Environment.ProcessId);

        // This throwaway process ignores the Ctrl+C it is about to receive along with everyone else.
        SetConsoleCtrlHandler(null, true);

        foreach (var pid in Parse(candidates))
        {
            FreeConsole();
            if (!AttachConsole((uint)pid)) continue;

            var attached = new uint[256];
            var count = GetConsoleProcessList(attached, (uint)attached.Length);
            if (count == 0 || count > attached.Length) return 2;
            if (attached.Take((int)count).Any(p => !approvedPids.Contains((int)p)))
                return 3; // shared with something the user didn't approve

            if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)) return 4;
            Thread.Sleep(200); // let the event be delivered before this process exits
            return 0;
        }
        return 1;
    }

    private static IEnumerable<int> Parse(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0);
}
