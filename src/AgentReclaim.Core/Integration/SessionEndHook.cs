using System.Text.Json;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Collection;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Native;

namespace AgentReclaim.Core.Integration;

/// <summary>
/// `AgentReclaim.exe hook session-end`, run by Claude Code's SessionEnd hook. It works out which
/// claude.exe it belongs to and tells the tray app, so that session's leftovers can be cleaned
/// after a short grace period instead of the full idle timeout. Always exits 0 and never blocks
/// Claude for more than a moment.
/// </summary>
public static class SessionEndHook
{
    public static int Run(TextReader stdin)
    {
        try
        {
            var payload = ReadPayload(stdin, TimeSpan.FromSeconds(2));
            if (FindAgentAncestor() is not { } agentPid) return 0;

            NotificationPipe.TrySend(new SessionEndedMessage(
                agentPid,
                payload?.RootElement.TryGetProperty("session_id", out var id) == true ? id.GetString() : null,
                payload?.RootElement.TryGetProperty("cwd", out var cwd) == true ? cwd.GetString() : null,
                payload?.RootElement.TryGetProperty("reason", out var reason) == true ? reason.GetString() : null,
                NativeProcess.GetStartTicks(agentPid) ?? 0),
                TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // A cleanup helper must never get in the way of the user's session.
        }
        return 0;
    }

    private static JsonDocument? ReadPayload(TextReader stdin, TimeSpan timeout)
    {
        var read = Task.Run(stdin.ReadToEnd);
        if (!read.Wait(timeout) || string.IsNullOrWhiteSpace(read.Result)) return null;
        try
        {
            return JsonDocument.Parse(read.Result);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Walks up from this process (hook → shell → … → claude.exe) to the nearest agent.</summary>
    public static int? FindAgentAncestor()
    {
        var table = ProcessCollector.EnumerateProcesses().ToDictionary(p => p.Pid);
        var pid = Environment.ProcessId;
        for (var depth = 0; depth < 16 && table.TryGetValue(pid, out var entry); depth++)
        {
            var parentPid = entry.ParentPid;
            if (!table.TryGetValue(parentPid, out var parent)) return null;

            string? commandLine = null;
            if (parent.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
            {
                var handle = NativeProcess.Open(parentPid);
                if (handle != IntPtr.Zero)
                {
                    commandLine = NativeProcess.GetCommandLine(handle);
                    Native.NativeMethods.CloseHandle(handle);
                }
            }

            var probe = new ProcessInfo(new ProcessKey(parentPid, 0), parent.ParentPid, parent.Name, null, commandLine,
                DateTime.MinValue, 0, 0, 0, [], 0);
            if (AgentSignatures.AgentRootKind(probe) is not null) return parentPid;
            pid = parentPid;
        }
        return null;
    }
}
