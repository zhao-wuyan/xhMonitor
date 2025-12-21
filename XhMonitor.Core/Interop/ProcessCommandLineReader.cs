using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XhMonitor.Core.Interop;

public static class ProcessCommandLineReader
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInformation = 0;
    private const int StatusSuccess = 0;

    public static string? GetCommandLine(int processId)
    {
        using SafeProcessHandle processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        return TryReadCommandLine(processHandle.DangerousGetHandle(), out string? commandLine)
            ? commandLine
            : null;
    }

    private static bool TryReadCommandLine(IntPtr processHandle, out string? commandLine)
    {
        commandLine = null;

        if (NtQueryInformationProcess(
                processHandle,
                ProcessBasicInformation,
                out PROCESS_BASIC_INFORMATION pbi,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out _)
            != StatusSuccess)
        {
            return false;
        }

        IntPtr pebAddress = pbi.PebBaseAddress;
        int processParametersOffset = IntPtr.Size == 8 ? 0x20 : 0x10;
        if (!TryReadIntPtr(processHandle, IntPtr.Add(pebAddress, processParametersOffset), out IntPtr processParameters))
        {
            return false;
        }

        int commandLineOffset = IntPtr.Size == 8 ? 0x70 : 0x40;
        if (!TryReadStruct(processHandle, IntPtr.Add(processParameters, commandLineOffset), out UNICODE_STRING commandLineUnicode))
        {
            return false;
        }

        if (commandLineUnicode.Length == 0 || commandLineUnicode.Buffer == IntPtr.Zero)
        {
            commandLine = string.Empty;
            return true;
        }

        byte[] buffer = new byte[commandLineUnicode.Length];
        if (!ReadProcessMemory(processHandle, commandLineUnicode.Buffer, buffer, buffer.Length, out _))
        {
            return false;
        }

        commandLine = Encoding.Unicode.GetString(buffer);
        return true;
    }

    private static bool TryReadIntPtr(IntPtr processHandle, IntPtr address, out IntPtr value)
    {
        byte[] buffer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(processHandle, address, buffer, buffer.Length, out _))
        {
            value = IntPtr.Zero;
            return false;
        }

        value = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(buffer, 0))
            : new IntPtr(BitConverter.ToInt32(buffer, 0));

        return true;
    }

    private static bool TryReadStruct<T>(IntPtr processHandle, IntPtr address, out T value)
        where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        byte[] buffer = new byte[size];
        if (!ReadProcessMemory(processHandle, address, buffer, size, out _))
        {
            value = default;
            return false;
        }

        value = MemoryMarshal.Read<T>(buffer.AsSpan());
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out IntPtr lpNumberOfBytesRead);
}
