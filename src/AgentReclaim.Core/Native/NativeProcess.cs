using System.Runtime.InteropServices;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Native;

/// <summary>Per-process queries that only need PROCESS_QUERY_LIMITED_INFORMATION (no admin).</summary>
internal static class NativeProcess
{
    public static IntPtr Open(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        return handle != IntPtr.Zero ? handle : OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    }

    public static long? GetStartTicks(int pid)
    {
        var handle = Open(pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Reads the process's current directory from its PEB, so a stopped process can be relaunched
    /// where it was running. 64-bit targets only; returns null for anything it can't read.
    /// </summary>
    public static string? GetCurrentDirectory(int pid)
    {
        if (!Environment.Is64BitProcess) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (IsWow64Process(handle, out var wow64) && wow64) return null;
            if (NtQueryInformationProcess(handle, ProcessBasicInformation, out PROCESS_BASIC_INFORMATION basic,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;

            // PEB+0x20 -> RTL_USER_PROCESS_PARAMETERS; +0x38 -> CurrentDirectory.DosPath (UNICODE_STRING).
            var parameters = ReadPointer(handle, basic.PebBaseAddress + 0x20);
            if (parameters == IntPtr.Zero) return null;
            var unicode = Read(handle, parameters + 0x38, 16);
            if (unicode is null) return null;

            var length = BitConverter.ToUInt16(unicode, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(unicode, 8);
            if (length == 0 || buffer == IntPtr.Zero) return null;
            var text = Read(handle, buffer, length);
            return text is null ? null : System.Text.Encoding.Unicode.GetString(text).TrimEnd('\\') is { Length: > 0 } dir
                ? (dir.EndsWith(':') ? dir + "\\" : dir)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr ReadPointer(IntPtr handle, IntPtr address) =>
        Read(handle, address, 8) is { } bytes ? (IntPtr)BitConverter.ToInt64(bytes, 0) : IntPtr.Zero;

    private static byte[]? Read(IntPtr handle, IntPtr address, int size)
    {
        var buffer = new byte[size];
        return ReadProcessMemory(handle, address, buffer, size, out var read) && (int)read == size ? buffer : null;
    }

    public static string? GetImagePath(IntPtr handle)
    {
        var buffer = new char[1024];
        var size = buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    public static string? GetCommandLine(IntPtr handle)
    {
        NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
                return null;

            // The buffer starts with a UNICODE_STRING whose Buffer points just past it.
            var lengthBytes = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return lengthBytes == 0 ? null : Marshal.PtrToStringUni(text, lengthBytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
