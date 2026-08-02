using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerScalerLabs.ProbeHost;

internal static class RemoteModuleResolver
{
    internal static IntPtr ResolveSystemFunction(Process process, IntPtr localFunction)
    {
        uint flags = NativeMethods.GetModuleHandleExFlagFromAddress |
            NativeMethods.GetModuleHandleExFlagUnchangedRefCount;
        if (!NativeMethods.GetModuleHandleEx(flags, localFunction, out IntPtr localOwner) || localOwner == IntPtr.Zero)
        {
            throw NativeMethods.Error("GetModuleHandleEx failed for a system function");
        }

        string ownerPath = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .First(module => module.BaseAddress == localOwner).FileName;
        string ownerName = Path.GetFileName(ownerPath);
        long rva = localFunction.ToInt64() - localOwner.ToInt64();
        ProcessModule remoteOwner = FindModule(process, ownerName)
            ?? throw new InvalidOperationException($"Remote system module {ownerName} was not found.");
        return new IntPtr(checked(remoteOwner.BaseAddress.ToInt64() + rva));
    }

    internal static ProcessModule? FindModule(Process process, string moduleName)
    {
        process.Refresh();
        return process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
            string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
    }

    internal static IntPtr ResolveProbeExport(Process process, ProcessModule remoteModule, string probePath, string exportName)
    {
        IntPtr localModule = NativeMethods.LoadLibrary(probePath);
        if (localModule == IntPtr.Zero)
        {
            throw NativeMethods.Error("Loading the probe locally for export resolution failed");
        }

        try
        {
            IntPtr localExport = NativeMethods.GetProcAddress(localModule, exportName);
            if (localExport == IntPtr.Zero)
            {
                throw NativeMethods.Error($"Probe export {exportName} was not found");
            }
            long rva = localExport.ToInt64() - localModule.ToInt64();
            return new IntPtr(checked(remoteModule.BaseAddress.ToInt64() + rva));
        }
        finally
        {
            NativeMethods.FreeLibrary(localModule);
        }
    }
}
