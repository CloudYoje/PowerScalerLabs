using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerScalerLabs.ProbeHost;

internal static class NativeMethods
{
    [Flags]
    internal enum ProcessAccess : uint
    {
        CreateThread = 0x0002,
        QueryInformation = 0x0400,
        VmOperation = 0x0008,
        VmRead = 0x0010,
        VmWrite = 0x0020
    }

    internal const uint MemCommit = 0x1000;
    internal const uint MemReserve = 0x2000;
    internal const uint MemRelease = 0x8000;
    internal const uint PageReadWrite = 0x04;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;
    internal const uint GetModuleHandleExFlagFromAddress = 0x00000004;
    internal const uint GetModuleHandleExFlagUnchangedRefCount = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(ProcessAccess access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(SafeProcessHandle process, IntPtr address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(SafeProcessHandle process, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, IntPtr attributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeThread(SafeHandle thread, out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
}
