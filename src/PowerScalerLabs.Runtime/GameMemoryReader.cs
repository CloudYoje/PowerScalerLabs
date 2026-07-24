using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal sealed class GameMemoryReader : IDisposable
{
    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint PageNoAccess = 0x01;
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;

    private readonly SafeProcessHandle _handle;
    private readonly byte[] _primitiveBuffer = new byte[sizeof(ulong)];
    private IReadOnlyList<RemoteModule> _modules;
    private long _readRequests;
    private long _readProcessMemoryCalls;
    private long _requestedBytes;
    private long _completedBytes;
    private long _failedReadCalls;
    private long _rejectedReadRequests;
    private long _virtualQueryCalls;
    private long _failedVirtualQueryCalls;
    private long _moduleRefreshCount;

    internal GameMemoryReader(int processId)
    {
        ProcessId = processId;
        _handle = NativeMethods.OpenReadOnlyProcess(processId);
        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new InvalidOperationException(NativeMethods.FormatWin32Error(error));
        }

        _modules = NativeMethods.EnumerateModules(processId);
        _moduleRefreshCount = 1;
        GameModule = FindModule("DBXV2.exe") ??
            throw new InvalidOperationException("DBXV2.exe was not found in the target module list.");
        GameVersion = ReadFileVersion(GameModule.Path);
    }

    internal int ProcessId { get; }
    internal RemoteModule GameModule { get; private set; }
    internal string? GameVersion { get; }

    internal void RefreshModules()
    {
        _modules = NativeMethods.EnumerateModules(ProcessId);
        Interlocked.Increment(ref _moduleRefreshCount);
        GameModule = FindModule("DBXV2.exe") ?? GameModule;
    }

    internal MemoryAccessMetricsMessage SnapshotMetrics(string lane) => new(
        lane,
        Interlocked.Read(ref _readRequests),
        Interlocked.Read(ref _readProcessMemoryCalls),
        Interlocked.Read(ref _requestedBytes),
        Interlocked.Read(ref _completedBytes),
        Interlocked.Read(ref _failedReadCalls),
        Interlocked.Read(ref _rejectedReadRequests),
        Interlocked.Read(ref _virtualQueryCalls),
        Interlocked.Read(ref _failedVirtualQueryCalls),
        Interlocked.Read(ref _moduleRefreshCount));

    internal RemoteModule? FindModule(string fileName) =>
        _modules.FirstOrDefault(module =>
            string.Equals(module.Name, fileName, StringComparison.OrdinalIgnoreCase));

    internal bool TryReadInto(ulong address, byte[] buffer, int length) => TryRead(address, buffer, length);

    // Chronology targets are rooted in fighter objects already validated by the observer.
    // Avoiding a VirtualQueryEx call for every 25 ms scalar sample keeps the high-rate lane bounded.
    // ReadProcessMemory remains fail-closed if an object disappears between observer heartbeats.
    internal bool TryReadKnownReadable(ulong address, byte[] buffer, int length)
    {
        Interlocked.Increment(ref _readRequests);
        if (address == 0 || length <= 0 || length > buffer.Length ||
            address > ulong.MaxValue - checked((ulong)length))
        {
            Interlocked.Increment(ref _rejectedReadRequests);
            return false;
        }

        Interlocked.Increment(ref _readProcessMemoryCalls);
        Interlocked.Add(ref _requestedBytes, length);
        bool success = NativeMethods.ReadProcessMemory(
            _handle,
            unchecked((nint)address),
            buffer,
            checked((nuint)length),
            out nuint bytesRead);
        Interlocked.Add(ref _completedBytes, checked((long)bytesRead));
        bool complete = success && bytesRead == checked((nuint)length);
        if (!complete)
        {
            Interlocked.Increment(ref _failedReadCalls);
        }
        return complete;
    }

    internal bool TryReadBytes(ulong address, int length, out byte[] buffer)
    {
        if (length <= 0)
        {
            buffer = [];
            return false;
        }

        buffer = new byte[length];
        if (TryRead(address, buffer))
        {
            return true;
        }

        buffer = [];
        return false;
    }

    internal bool TryReadUInt32(ulong address, out uint value)
    {
        if (!TryRead(address, _primitiveBuffer, sizeof(uint)))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt32(_primitiveBuffer, 0);
        return true;
    }

    internal bool TryReadUInt64(ulong address, out ulong value)
    {
        if (!TryRead(address, _primitiveBuffer, sizeof(ulong)))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt64(_primitiveBuffer, 0);
        return true;
    }

    internal bool TryReadSingle(ulong address, out float value)
    {
        if (!TryRead(address, _primitiveBuffer, sizeof(float)))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToSingle(_primitiveBuffer, 0);
        return true;
    }

    internal bool IsReadableRange(ulong address, ulong size)
    {
        if (address == 0 || size == 0 || address > ulong.MaxValue - size)
        {
            return false;
        }

        if (!TryQuery(address, out MemoryBasicInformation information) ||
            information.State != MemCommit ||
            !IsReadableProtection(information.Protect))
        {
            return false;
        }

        ulong regionStart = unchecked((ulong)information.BaseAddress.ToInt64());
        ulong regionSize = information.RegionSize.ToUInt64();
        if (regionStart > ulong.MaxValue - regionSize)
        {
            return false;
        }

        ulong regionEnd = regionStart + regionSize;
        return address >= regionStart && address + size <= regionEnd;
    }

    internal bool IsReadableAddress(ulong address) => IsReadableRange(address, 1);

    internal bool IsLikelyHeapObject(ulong address)
    {
        if (address < 0x10000 || IsGameImageAddress(address) ||
            !TryQuery(address, out MemoryBasicInformation information))
        {
            return false;
        }

        return information.State == MemCommit &&
            information.Type == MemPrivate &&
            IsReadableProtection(information.Protect);
    }

    internal bool IsPrivateWritableObject(ulong address)
    {
        if (address < 0x10000 || IsGameImageAddress(address) ||
            !TryQuery(address, out MemoryBasicInformation information))
        {
            return false;
        }

        return information.State == MemCommit &&
            information.Type == MemPrivate &&
            IsWritableProtection(information.Protect);
    }

    internal bool IsGameImageAddress(ulong address) =>
        IsAddressInside(address, GameModule.BaseAddress, GameModule.ImageSize);

    public void Dispose() => _handle.Dispose();

    private bool TryRead(ulong address, byte[] buffer) => TryRead(address, buffer, buffer.Length);

    private bool TryRead(ulong address, byte[] buffer, int length)
    {
        Interlocked.Increment(ref _readRequests);
        if (length <= 0 || length > buffer.Length || !IsReadableRange(address, checked((ulong)length)))
        {
            Interlocked.Increment(ref _rejectedReadRequests);
            return false;
        }

        Interlocked.Increment(ref _readProcessMemoryCalls);
        Interlocked.Add(ref _requestedBytes, length);
        bool success = NativeMethods.ReadProcessMemory(
            _handle,
            unchecked((nint)address),
            buffer,
            checked((nuint)length),
            out nuint bytesRead);
        Interlocked.Add(ref _completedBytes, checked((long)bytesRead));
        bool complete = success && bytesRead == checked((nuint)length);
        if (!complete)
        {
            Interlocked.Increment(ref _failedReadCalls);
        }
        return complete;
    }

    private bool TryQuery(ulong address, out MemoryBasicInformation information)
    {
        nuint length = checked((nuint)Marshal.SizeOf<MemoryBasicInformation>());
        Interlocked.Increment(ref _virtualQueryCalls);
        nuint result = NativeMethods.VirtualQueryEx(
            _handle,
            unchecked((nint)address),
            out information,
            length);
        bool complete = result == length;
        if (!complete)
        {
            Interlocked.Increment(ref _failedVirtualQueryCalls);
        }
        return complete;
    }

    private static bool IsAddressInside(ulong address, ulong baseAddress, uint imageSize) =>
        baseAddress != 0 && imageSize != 0 &&
        baseAddress <= ulong.MaxValue - imageSize &&
        address >= baseAddress &&
        address < baseAddress + imageSize;

    private static bool IsReadableProtection(uint protection)
    {
        if ((protection & PageGuard) != 0 || protection == PageNoAccess)
        {
            return false;
        }

        uint basic = protection & 0xFF;
        return basic is PageReadOnly or PageReadWrite or PageWriteCopy or
            PageExecuteRead or PageExecuteReadWrite or PageExecuteWriteCopy;
    }

    private static bool IsWritableProtection(uint protection)
    {
        if ((protection & PageGuard) != 0 || protection == PageNoAccess)
        {
            return false;
        }

        uint basic = protection & 0xFF;
        return basic is PageReadWrite or PageWriteCopy or
            PageExecuteReadWrite or PageExecuteWriteCopy;
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch
        {
            return null;
        }
    }
}
