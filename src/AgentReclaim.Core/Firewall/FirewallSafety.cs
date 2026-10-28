using System.Runtime.InteropServices;
using Microsoft.Win32;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Firewall;

/// <param name="LoopbackOnly">Bound to 127.0.0.1 or ::1 only: other machines can't reach it anyway.</param>
public sealed record ListeningSocket(FwProtocol Protocol, int Port, int Pid, bool LoopbackOnly);

public sealed record TcpConnection(int LocalPort, int RemotePort, bool RemoteIsLoopback, int Pid);

/// <param name="Complete">False if a table couldn't be read: then "no connections" can't be trusted.</param>
public sealed record SocketSnapshot(IReadOnlyList<ListeningSocket> Listening, IReadOnlyList<TcpConnection> Established, bool Complete)
{
    public static SocketSnapshot Read()
    {
        var listening = new List<ListeningSocket>();
        var established = new List<TcpConnection>();
        var complete = true;

        complete &= ReadTable((IntPtr b, ref int s) => GetExtendedTcpTable(b, ref s, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0), (buffer, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                var state = Marshal.ReadInt32(row, 0);
                var local = Marshal.ReadInt32(row, 4);
                var localPort = Port(row, 8);
                var remote = Marshal.ReadInt32(row, 12);
                var pid = Marshal.ReadInt32(row, 20);
                if (state == MIB_TCP_STATE_LISTEN) listening.Add(new(FwProtocol.Tcp, localPort, pid, IsLoopback4(local)));
                else if (state == MIB_TCP_STATE_ESTAB) established.Add(new(localPort, Port(row, 16), IsLoopback4(remote), pid));
            }
        });
        complete &= ReadTable((IntPtr b, ref int s) => GetExtendedTcpTable(b, ref s, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0), (buffer, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 56;
                var state = Marshal.ReadInt32(row, 48);
                var pid = Marshal.ReadInt32(row, 52);
                if (state == MIB_TCP_STATE_LISTEN) listening.Add(new(FwProtocol.Tcp, Port(row, 20), pid, IsLoopback6(row)));
                else if (state == MIB_TCP_STATE_ESTAB) established.Add(new(Port(row, 20), Port(row, 44), IsLoopback6(row + 24), pid));
            }
        });
        complete &= ReadTable((IntPtr b, ref int s) => GetExtendedUdpTable(b, ref s, false, AF_INET, UDP_TABLE_OWNER_PID, 0), (buffer, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 12;
                listening.Add(new(FwProtocol.Udp, Port(row, 4), Marshal.ReadInt32(row, 8), IsLoopback4(Marshal.ReadInt32(row, 0))));
            }
        });
        complete &= ReadTable((IntPtr b, ref int s) => GetExtendedUdpTable(b, ref s, false, AF_INET6, UDP_TABLE_OWNER_PID, 0), (buffer, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 28;
                listening.Add(new(FwProtocol.Udp, Port(row, 20), Marshal.ReadInt32(row, 24), IsLoopback6(row)));
            }
        });
        return new SocketSnapshot(listening, established, complete);
    }

    private delegate uint TableReader(IntPtr buffer, ref int size);

    private static bool ReadTable(TableReader read, Action<IntPtr, int> parse)
    {
        // The table can grow between the size query and the read; retry with the new size.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var size = 0;
            read(IntPtr.Zero, ref size);
            if (size == 0) return false;
            size += 4096;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = read(buffer, ref size);
                if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER
                if (result != 0) return false;
                parse(buffer, Marshal.ReadInt32(buffer));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return false;
    }

    private static int Port(IntPtr row, int offset)
    {
        var raw = Marshal.ReadInt32(row, offset);
        return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    }

    /// <summary>127.0.0.0/8 (the address is in network byte order: the first octet is the low byte).</summary>
    internal static bool IsLoopback4(int address) => (address & 0xFF) == 127;

    /// <summary>::1, or an IPv4-mapped 127.x address.</summary>
    private static bool IsLoopback6(IntPtr address)
    {
        var bytes = new byte[16];
        Marshal.Copy(address, bytes, 0, 16);
        return IsLoopback6(bytes);
    }

    internal static bool IsLoopback6(byte[] b)
    {
        if (b.Take(15).All(x => x == 0) && b[15] == 1) return true;
        return b.Take(10).All(x => x == 0) && b[10] == 0xFF && b[11] == 0xFF && b[12] == 127;
    }
}

/// <param name="Blocking">The change is refused (it would lock the user out); otherwise the user is asked.</param>
public sealed record SafetyIssue(bool Blocking, string Text);

/// <summary>What blocking would cut off right now: a Remote Desktop session, live SSH or other connections.</summary>
public static class FirewallSafety
{
    public const int DefaultRdpPort = 3389;

    public static IReadOnlyList<SafetyIssue> Check(IEnumerable<BlockRule> adding, SocketSnapshot sockets, Func<int, string> processName,
        bool remoteSession, int rdpPort)
    {
        var issues = new List<SafetyIssue>();
        var rules = adding.ToList();
        var listeningTcp = sockets.Listening.Where(s => s.Protocol == FwProtocol.Tcp).Select(s => s.Port).ToHashSet();

        foreach (var rule in rules)
        {
            if (CheckLockout(rule, remoteSession, rdpPort) is { } lockout) issues.Add(lockout);
            if (rule.Protocol != FwProtocol.Tcp) continue;

            // Incoming: a connection to a port something here listens on. Outgoing: to that port elsewhere.
            var cut = rule.Direction == FwDirection.In
                ? sockets.Established.Where(c => !c.RemoteIsLoopback && rule.Ports.Contains(c.LocalPort) && listeningTcp.Contains(c.LocalPort))
                : sockets.Established.Where(c => !c.RemoteIsLoopback && rule.Ports.Contains(c.RemotePort) && !listeningTcp.Contains(c.LocalPort));
            foreach (var group in cut.GroupBy(c => c.Pid))
            {
                var ports = string.Join(", ", group.Select(c => rule.Direction == FwDirection.In ? c.LocalPort : c.RemotePort).Distinct().Order());
                issues.Add(new(false, rule.Direction == FwDirection.In
                    ? $"{processName(group.Key)} has {group.Count()} connection(s) coming in on port {ports} right now (for example SSH or Remote Desktop). Blocking may cut them off."
                    : $"{processName(group.Key)} is connected to port {ports} on another machine right now. Blocking may cut it off."));
            }
        }

        if (!sockets.Complete && rules.Count > 0)
            issues.Add(new(false, "Couldn't read the current connections, so AgentReclaim can't tell whether you're connected through these ports."));
        return issues.Distinct().ToList();
    }

    /// <summary>
    /// Removing or restoring AgentReclaim rules goes by what they are now, not by their names: one changed outside
    /// AgentReclaim into an allow rule grants access today, and taking it away revokes that. Refused if it may be what
    /// lets a Remote Desktop session in; otherwise the user is told.
    /// </summary>
    public static IReadOnlyList<SafetyIssue> CheckRevocations(IEnumerable<FirewallRuleInfo> touched, bool remoteSession, int rdpPort)
    {
        var issues = new List<SafetyIssue>();
        foreach (var rule in touched.Where(r => r.Action == -1))
        {
            // Settings unreadable: what it does can't be known, so from a Remote Desktop session it's left alone.
            issues.Add(new(remoteSession, $"\"{rule.Name}\" can't be read, so AgentReclaim can't tell what removing or restoring it takes away" +
                (remoteSession ? " (it might be what lets your Remote Desktop session in)." : ".")));
        }
        foreach (var rule in touched.Where(r => r.Enabled && r.Action is not (0 or -1)))
        {
            var mayCoverRdp = rule.Direction is 1 or -1 && rule.Protocol is 6 or 256 or -1 && PortsMayInclude(rule.LocalPorts, rdpPort);
            if (remoteSession && mayCoverRdp)
                issues.Add(new(true, $"\"{rule.Name}\" was changed outside AgentReclaim into a rule that may let your Remote Desktop session in; removing or restoring it could lock you out."));
            else
                issues.Add(new(false, $"\"{rule.Name}\" was changed outside AgentReclaim and now allows traffic ({DescribeActual(rule)}); this change takes that away."));
        }
        return issues;
    }

    /// <summary>Whether a firewall port list may include <paramref name="port"/>. Anything not understood counts as yes.</summary>
    internal static bool PortsMayInclude(string ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") return true;
        foreach (var part in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!PortRange.TryParse(part, out var range)) return true; // keywords like RPC, or anything unexpected
            if (range.Contains(port)) return true;
        }
        return false;
    }

    /// <summary>What a rule does now, in words (for rules changed outside AgentReclaim).</summary>
    public static string DescribeActual(FirewallRuleInfo rule) =>
        $"{(rule.Enabled ? "" : "switched off: ")}{(rule.Action switch { 0 => "blocks ", 1 => "allows ", _ => "" })}" +
        $"{(rule.Protocol switch { 6 => "TCP", 17 => "UDP", 256 => "any protocol", _ => $"protocol {rule.Protocol}" })} " +
        $"{(rule.Direction == 2 ? "outgoing to port " + Or(rule.RemotePorts) : "incoming on port " + Or(rule.LocalPorts))}" +
        (rule.Restrictions.Length > 0 ? $" (narrowed: {rule.Restrictions})" : "");

    private static string Or(string ports) => string.IsNullOrWhiteSpace(ports) ? "any" : ports;

    /// <summary>Blocking this would cut off the Remote Desktop session this runs in (refused).</summary>
    public static SafetyIssue? CheckLockout(BlockRule rule, bool remoteSession, int rdpPort) =>
        remoteSession && rule.Protocol == FwProtocol.Tcp && rule.Direction == FwDirection.In && rule.Ports.Contains(rdpPort)
            ? new SafetyIssue(true, $"You're connected over Remote Desktop: blocking {rule.Describe()} would disconnect you and lock you out.")
            : null;

    /// <summary>This desktop is a Remote Desktop session.</summary>
    public static bool IsRemoteSession() => GetSystemMetrics(SM_REMOTESESSION) != 0;

    /// <summary>The port Remote Desktop listens on (3389 unless changed).</summary>
    public static int RdpPort()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "PortNumber", null);
            return value is int port and > 0 and <= 65535 ? port : DefaultRdpPort;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return DefaultRdpPort;
        }
    }
}
