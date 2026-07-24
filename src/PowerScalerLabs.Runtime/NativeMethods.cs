using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerScalerLabs.Runtime;

internal static class NativeMethods
{
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    private const uint Th32CsSnapModule = 0x00000008;
    private const uint Th32CsSnapModule32 = 0x00000010;
    private const int ErrorBadLength = 24;
    private static readonly nint InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(
        SafeProcessHandle process,
        nint address,
        out MemoryBasicInformation information,
        nuint informationLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32FirstW(SafeFileHandle snapshot, ref ModuleEntry32 moduleEntry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32NextW(SafeFileHandle snapshot, ref ModuleEntry32 moduleEntry);

    internal static ProcessAccessProbe ProbeReadAccess(int processId)
    {
        using SafeProcessHandle handle = OpenReadOnlyProcess(processId);
        if (!handle.IsInvalid)
        {
            return new ProcessAccessProbe(true, true, null);
        }

        int error = Marshal.GetLastWin32Error();
        return new ProcessAccessProbe(false, false, FormatWin32Error(error));
    }

    internal static SafeProcessHandle OpenReadOnlyProcess(int processId) =>
        OpenProcess(
            ProcessQueryInformation | ProcessQueryLimitedInformation | ProcessVmRead,
            inheritHandle: false,
            checked((uint)processId));

    internal static IReadOnlyList<RemoteModule> EnumerateModules(int processId)
    {
        const int maximumAttempts = 4;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                return EnumerateModulesOnce(processId);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorBadLength && attempt < maximumAttempts)
            {
                Thread.Sleep(10 * attempt);
            }
        }

        throw new Win32Exception(ErrorBadLength, FormatWin32Error(ErrorBadLength));
    }

    private static IReadOnlyList<RemoteModule> EnumerateModulesOnce(int processId)
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(
            Th32CsSnapModule | Th32CsSnapModule32,
            checked((uint)processId));

        if (snapshot.IsInvalid || snapshot.DangerousGetHandle() == InvalidHandleValue)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, FormatWin32Error(error));
        }

        ModuleEntry32 entry = new()
        {
            Size = checked((uint)Marshal.SizeOf<ModuleEntry32>())
        };

        List<RemoteModule> modules = [];
        if (!Module32FirstW(snapshot, ref entry))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, FormatWin32Error(error));
        }

        do
        {
            modules.Add(new RemoteModule(
                entry.ModuleName,
                entry.ExecutablePath,
                unchecked((ulong)entry.BaseAddress.ToInt64()),
                entry.ModuleSize));
            entry.Size = checked((uint)Marshal.SizeOf<ModuleEntry32>());
        }
        while (Module32NextW(snapshot, ref entry));

        int lastError = Marshal.GetLastWin32Error();
        const int noMoreFiles = 18;
        if (lastError != 0 && lastError != noMoreFiles)
        {
            throw new Win32Exception(lastError, FormatWin32Error(lastError));
        }

        return modules;
    }

    internal static string FormatWin32Error(int error)
    {
        string detail = new Win32Exception(error).Message;
        return $"Win32 {error}: {detail}";
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    internal nint BaseAddress;
    internal nint AllocationBase;
    internal uint AllocationProtect;
    internal ushort PartitionId;
    internal nuint RegionSize;
    internal uint State;
    internal uint Protect;
    internal uint Type;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ModuleEntry32
{
    internal uint Size;
    internal uint ModuleId;
    internal uint ProcessId;
    internal uint GlobalUsageCount;
    internal uint ProcessUsageCount;
    internal nint BaseAddress;
    internal uint ModuleSize;
    internal nint ModuleHandle;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    internal string ModuleName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    internal string ExecutablePath;
}

internal sealed record ProcessAccessProbe(bool CanQuery, bool CanRead, string? Error);
internal sealed record RemoteModule(string Name, string Path, ulong BaseAddress, uint ImageSize);
