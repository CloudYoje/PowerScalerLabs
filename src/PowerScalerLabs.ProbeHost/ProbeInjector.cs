using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PowerScalerLabs.ProbeHost;

internal sealed class ProbeInjectionSession : IDisposable
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(10);
    private readonly SafeProcessHandle _processHandle;

    internal ProbeInjectionSession(Process gameProcess, SafeProcessHandle processHandle, ProcessModule remoteModule, ProbeSharedMemory sharedMemory, string probePath)
    {
        GameProcess = gameProcess;
        _processHandle = processHandle;
        RemoteModule = remoteModule;
        SharedMemory = sharedMemory;
        ProbePath = probePath;
    }

    internal Process GameProcess { get; }
    internal ProcessModule RemoteModule { get; }
    internal ProbeSharedMemory SharedMemory { get; }
    internal string ProbePath { get; }
    internal bool IsGameAlive => !GameProcess.HasExited;

    internal async Task<bool> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        long initialHeartbeat = SharedMemory.ProbeHeartbeatSequence;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StageTimeout);
        while (!timeout.IsCancellationRequested && IsGameAlive)
        {
            if (SharedMemory.State == ProbeSharedMemory.NativeState.Ready &&
                SharedMemory.IsHandshakeValid() &&
                SharedMemory.ProbeHeartbeatSequence > initialHeartbeat)
            {
                return true;
            }
            if (SharedMemory.State == ProbeSharedMemory.NativeState.Faulted)
            {
                return false;
            }
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
        return false;
    }

    internal async Task<bool> ShutdownAndUnloadAsync(CancellationToken cancellationToken)
    {
        if (!IsGameAlive)
        {
            return true;
        }

        SharedMemory.RequestShutdown();
        if (!IsGameAlive)
        {
            return true;
        }
        IntPtr prepareUnload = RemoteModuleResolver.ResolveProbeExport(
            GameProcess, RemoteModule, ProbePath, "PSL_PrepareUnload");
        uint prepareResult = ProbeInjector.RunRemoteThread(
            _processHandle, prepareUnload, IntPtr.Zero, StageTimeout + TimeSpan.FromSeconds(2));
        if (prepareResult == 0 || SharedMemory.State != ProbeSharedMemory.NativeState.SafeToUnload)
        {
            return false;
        }

        IntPtr localKernel32 = NativeMethods.LoadLibrary("kernel32.dll");
        if (localKernel32 == IntPtr.Zero)
        {
            throw NativeMethods.Error("LoadLibrary(kernel32.dll) failed");
        }
        try
        {
            IntPtr localFreeLibrary = NativeMethods.GetProcAddress(localKernel32, "FreeLibrary");
            IntPtr remoteFreeLibrary = RemoteModuleResolver.ResolveSystemFunction(GameProcess, localFreeLibrary);
            uint exitCode = ProbeInjector.RunRemoteThread(
                _processHandle,
                remoteFreeLibrary,
                RemoteModule.BaseAddress,
                StageTimeout);
            if (exitCode == 0)
            {
                return false;
            }
        }
        finally
        {
            NativeMethods.FreeLibrary(localKernel32);
        }

        for (int attempt = 0; attempt < 40 && IsGameAlive; attempt++)
        {
            if (RemoteModuleResolver.FindModule(GameProcess, RemoteModule.ModuleName) is null)
            {
                return true;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return !IsGameAlive;
    }

    public void Dispose()
    {
        SharedMemory.Dispose();
        _processHandle.Dispose();
        GameProcess.Dispose();
    }
}

internal static class ProbeInjector
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<ProbeInjectionSession> AttachAsync(int processId, string probePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(probePath))
        {
            throw new FileNotFoundException("Native probe DLL is missing.", probePath);
        }

        Process process = Process.GetProcessById(processId);
        SafeProcessHandle? processHandle = null;
        ProbeSharedMemory? sharedMemory = null;
        ProcessModule? loadedModule = null;
        try
        {
            ValidateTarget(process);
            if (RemoteModuleResolver.FindModule(process, Path.GetFileName(probePath)) is not null)
            {
                throw new InvalidOperationException("A native probe module is already loaded; its session identity cannot be trusted.");
            }

            NativeMethods.ProcessAccess access = NativeMethods.ProcessAccess.QueryInformation |
                NativeMethods.ProcessAccess.VmRead |
                NativeMethods.ProcessAccess.VmWrite |
                NativeMethods.ProcessAccess.VmOperation |
                NativeMethods.ProcessAccess.CreateThread;
            processHandle = NativeMethods.OpenProcess(access, false, processId);
            if (processHandle.IsInvalid)
            {
                throw NativeMethods.Error("OpenProcess failed for explicit probe attachment");
            }

            ValidateX64(processHandle);
            ProbeLog.Write($"DBXV2 PID {processId} architecture validated x64.");
            sharedMemory = ProbeSharedMemory.Create(processId);
            ProbeLog.Write($"Shared mapping created for session {sharedMemory.SessionId}.");

            IntPtr kernel32 = NativeMethods.LoadLibrary("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                throw NativeMethods.Error("LoadLibrary(kernel32.dll) failed");
            }
            try
            {
                IntPtr localLoadLibrary = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");
                IntPtr remoteLoadLibrary = RemoteModuleResolver.ResolveSystemFunction(process, localLoadLibrary);
                byte[] pathBytes = Encoding.Unicode.GetBytes(Path.GetFullPath(probePath) + '\0');
                WithRemoteBuffer(processHandle, pathBytes, remoteAddress =>
                {
                    _ = RunRemoteThread(processHandle, remoteLoadLibrary, remoteAddress, StageTimeout);
                });
            }
            finally
            {
                NativeMethods.FreeLibrary(kernel32);
            }

            ProbeLog.Write("NativeProbe LoadLibrary completed; resolving remote module by enumeration.");
            ProcessModule remoteModule = await WaitForModuleAsync(process, Path.GetFileName(probePath), cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("LoadLibrary returned but the remote probe module was not found.");
            loadedModule = remoteModule;

            IntPtr initialize = RemoteModuleResolver.ResolveProbeExport(process, remoteModule, probePath, "PSL_Initialize");
            ProbeInitializationArguments arguments = ProbeInitializationArguments.Create(sharedMemory);
            byte[] argumentBytes = StructureToBytes(arguments);
            uint initializationResult = 0;
            WithRemoteBuffer(processHandle, argumentBytes, remoteAddress =>
            {
                initializationResult = RunRemoteThread(processHandle, initialize, remoteAddress, StageTimeout);
            });
            if (initializationResult == 0)
            {
                throw new InvalidOperationException($"PSL_Initialize failed; native status={sharedMemory.InitializationStatus}.");
            }
            ProbeLog.Write("PSL_Initialize invoked successfully.");

            ProbeInjectionSession session = new(process, processHandle, remoteModule, sharedMemory, probePath);
            processHandle = null;
            sharedMemory = null;
            return session;
        }
        catch
        {
            if (loadedModule is not null && processHandle is { IsInvalid: false } && !process.HasExited)
            {
                TryUnloadFailedInitialization(process, processHandle, loadedModule);
            }
            sharedMemory?.Dispose();
            processHandle?.Dispose();
            process.Dispose();
            throw;
        }
    }

    private static void TryUnloadFailedInitialization(
        Process process,
        SafeProcessHandle processHandle,
        ProcessModule remoteModule)
    {
        try
        {
            IntPtr kernel32 = NativeMethods.LoadLibrary("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                return;
            }
            try
            {
                IntPtr localFreeLibrary = NativeMethods.GetProcAddress(kernel32, "FreeLibrary");
                IntPtr remoteFreeLibrary = RemoteModuleResolver.ResolveSystemFunction(process, localFreeLibrary);
                _ = RunRemoteThread(processHandle, remoteFreeLibrary, remoteModule.BaseAddress, StageTimeout);
                ProbeLog.Write("Failed initialization rollback unloaded the remote probe module.");
            }
            finally
            {
                NativeMethods.FreeLibrary(kernel32);
            }
        }
        catch (Exception exception)
        {
            ProbeLog.Write($"Failed initialization rollback could not confirm unload: {exception.Message}");
        }
    }

    internal static uint RunRemoteThread(SafeProcessHandle process, IntPtr startAddress, IntPtr parameter, TimeSpan timeout)
    {
        using SafeWaitHandle thread = NativeMethods.CreateRemoteThread(process, IntPtr.Zero, 0, startAddress, parameter, 0, out _);
        if (thread.IsInvalid)
        {
            throw NativeMethods.Error("CreateRemoteThread failed");
        }
        uint wait = NativeMethods.WaitForSingleObject(thread, checked((uint)timeout.TotalMilliseconds));
        if (wait == NativeMethods.WaitTimeout)
        {
            throw new TimeoutException("Remote thread did not complete within the bounded stage timeout.");
        }
        if (wait != NativeMethods.WaitObject0 || !NativeMethods.GetExitCodeThread(thread, out uint exitCode))
        {
            throw NativeMethods.Error("Waiting for the remote thread failed");
        }
        return exitCode;
    }

    private static void ValidateTarget(Process process)
    {
        if (process.HasExited || !string.Equals(process.ProcessName, "DBXV2", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested PID is not a live DBXV2 process.");
        }
        string? fileName = process.MainModule?.FileName;
        if (!string.Equals(Path.GetFileName(fileName), "DBXV2.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested process executable is not DBXV2.exe.");
        }
    }

    private static void ValidateX64(SafeProcessHandle process)
    {
        const ushort imageFileMachineUnknown = 0;
        const ushort imageFileMachineAmd64 = 0x8664;
        if (!NativeMethods.IsWow64Process2(process, out ushort processMachine, out ushort nativeMachine) ||
            processMachine != imageFileMachineUnknown || nativeMachine != imageFileMachineAmd64)
        {
            throw new InvalidOperationException("The requested DBXV2 process is not native x64.");
        }
    }

    private static async Task<ProcessModule?> WaitForModuleAsync(Process process, string name, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessModule? module = RemoteModuleResolver.FindModule(process, name);
            if (module is not null)
            {
                return module;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static void WithRemoteBuffer(SafeProcessHandle process, byte[] bytes, Action<IntPtr> action)
    {
        IntPtr remote = NativeMethods.VirtualAllocEx(
            process, IntPtr.Zero, (nuint)bytes.Length,
            NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);
        if (remote == IntPtr.Zero)
        {
            throw NativeMethods.Error("VirtualAllocEx failed");
        }
        try
        {
            if (!NativeMethods.WriteProcessMemory(process, remote, bytes, (nuint)bytes.Length, out nuint written) || written != (nuint)bytes.Length)
            {
                throw NativeMethods.Error("WriteProcessMemory failed");
            }
            action(remote);
        }
        finally
        {
            NativeMethods.VirtualFreeEx(process, remote, 0, NativeMethods.MemRelease);
        }
    }

    private static byte[] StructureToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] bytes = new byte[size];
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, memory, false);
            Marshal.Copy(memory, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
