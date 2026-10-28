using System.Runtime.InteropServices;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Collection;

/// <summary>Listening ports and established connection counts per PID from the IPv4 and IPv6 TCP tables, and bound UDP ports.</summary>
internal sealed class TcpTable
{
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    public Dictionary<int, SortedSet<int>> ListeningPorts { get; } = new();
    public Dictionary<int, int> Established { get; } = new();

    /// <summary>
    /// False if any table couldn't be read. Missing data must never be mistaken for "no connections":
    /// automatic stops rely on it.
    /// </summary>
    public bool Complete { get; private set; } = true;

    public static TcpTable Read()
    {
        var table = new TcpTable();
        table.Complete &= table.ReadFamily(AF_INET, rowSize: 24, stateOffset: 0, localPortOffset: 8, pidOffset: 20);
        table.Complete &= table.ReadFamily(AF_INET6, rowSize: 56, stateOffset: 48, localPortOffset: 20, pidOffset: 52);
        // MIB_UDPROW_OWNER_PID and MIB_UDP6ROW_OWNER_PID.
        table.Complete &= table.ReadUdpFamily(AF_INET, rowSize: 12, localPortOffset: 4, pidOffset: 8);
        table.Complete &= table.ReadUdpFamily(AF_INET6, rowSize: 28, localPortOffset: 20, pidOffset: 24);
        return table;
    }

    private bool ReadFamily(int family, int rowSize, int stateOffset, int localPortOffset, int pidOffset)
    {
        // The table can grow between the size query and the read; retry with the new size.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
            if (size == 0) return false;
            size += 4096;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                if (result == ERROR_INSUFFICIENT_BUFFER) continue;
                if (result != 0) return false;

                Parse(buffer, rowSize, stateOffset, localPortOffset, pidOffset);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return false;
    }

    private void Parse(IntPtr buffer, int rowSize, int stateOffset, int localPortOffset, int pidOffset)
    {
        var count = Marshal.ReadInt32(buffer);
        for (var i = 0; i < count; i++)
        {
            var row = buffer + 4 + i * rowSize;
            var state = Marshal.ReadInt32(row, stateOffset);
            var pid = Marshal.ReadInt32(row, pidOffset);

            if (state == MIB_TCP_STATE_LISTEN)
            {
                var raw = Marshal.ReadInt32(row, localPortOffset);
                var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                if (!ListeningPorts.TryGetValue(pid, out var ports))
                    ListeningPorts[pid] = ports = new SortedSet<int>();
                ports.Add(port);
            }
            else if (state == MIB_TCP_STATE_ESTAB)
            {
                Established[pid] = Established.GetValueOrDefault(pid) + 1;
            }
        }
    }

    /// <summary>Established connection count for each of <paramref name="pids"/>, or null if the tables couldn't be read.</summary>
    public static int? EstablishedFor(IReadOnlySet<int> pids)
    {
        var table = Read();
        return table.Complete ? pids.Sum(p => table.Established.GetValueOrDefault(p)) : null;
    }

    /// <summary>Local UDP ports each PID has bound (UDP has no "listening" state: a bound socket may be a server).</summary>
    public Dictionary<int, SortedSet<int>> UdpPorts { get; } = new();

    private bool ReadUdpFamily(int family, int rowSize, int localPortOffset, int pidOffset)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UDP_TABLE_OWNER_PID, 0);
            if (size == 0) return false;
            size += 4096;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedUdpTable(buffer, ref size, false, family, UDP_TABLE_OWNER_PID, 0);
                if (result == ERROR_INSUFFICIENT_BUFFER) continue;
                if (result != 0) return false;

                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * rowSize;
                    var raw = Marshal.ReadInt32(row, localPortOffset);
                    var pid = Marshal.ReadInt32(row, pidOffset);
                    if (!UdpPorts.TryGetValue(pid, out var ports))
                        UdpPorts[pid] = ports = new SortedSet<int>();
                    ports.Add(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return false;
    }

    /// <summary>
    /// How many TCP listening ports and bound UDP ports <paramref name="pids"/> hold between them, or null if the
    /// tables couldn't be read.
    /// </summary>
    public static int? ListenersFor(IReadOnlySet<int> pids)
    {
        var table = Read();
        return table.Complete
            ? pids.Sum(p => table.ListeningPorts.GetValueOrDefault(p)?.Count ?? 0) + pids.Sum(p => table.UdpPorts.GetValueOrDefault(p)?.Count ?? 0)
            : null;
    }
}
