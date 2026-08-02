using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PowerScalerLabs.ProbeHost;

internal sealed class ProbeSharedMemory : IDisposable
{
    internal const uint Magic = 0x50534C50;
    internal const int HeaderSize = 256;
    internal const int EventSize = 256;
    internal const int EventCapacity = 256;
    internal const int MappingSize = HeaderSize + EventSize * EventCapacity;

    internal static class Offset
    {
        internal const long Magic = 0;
        internal const long AbiVersion = 4;
        internal const long HeaderSize = 8;
        internal const long EventSize = 12;
        internal const long Capacity = 16;
        internal const long State = 20;
        internal const long HostPid = 24;
        internal const long GamePid = 28;
        internal const long NonceLow = 32;
        internal const long NonceHigh = 40;
        internal const long QpcFrequency = 48;
        internal const long HostHeartbeatQpc = 56;
        internal const long ProbeHeartbeatQpc = 64;
        internal const long ProbeHeartbeatSequence = 72;
        internal const long DroppedEventCount = 80;
        internal const long ActiveWatchpointCount = 88;
        internal const long Command = 92;
        internal const long CommandSequence = 96;
        internal const long CommandAckSequence = 104;
        internal const long EventWriteSequence = 112;
        internal const long EventReadSequence = 120;
        internal const long InitializationStatus = 128;
    }

    internal enum NativeState : uint
    {
        Created = 1,
        Initializing = 2,
        Ready = 3,
        Inert = 4,
        ShuttingDown = 5,
        SafeToUnload = 6,
        Faulted = 7
    }

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private long _commandSequence;

    private ProbeSharedMemory(int gameProcessId, ulong nonceLow, ulong nonceHigh)
    {
        GameProcessId = gameProcessId;
        NonceLow = nonceLow;
        NonceHigh = nonceHigh;
        MappingName = $"Local\\PowerScalerLabs.Probe.{gameProcessId}.{nonceLow:X16}";
        CommandEventName = MappingName + ".Command";
        EventReadyName = MappingName + ".Events";
        CommandEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CommandEventName);
        EventReady = new EventWaitHandle(false, EventResetMode.AutoReset, EventReadyName);
        _mapping = MemoryMappedFile.CreateNew(MappingName, MappingSize, MemoryMappedFileAccess.ReadWrite);
        _view = _mapping.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.ReadWrite);

        Write(Offset.Magic, Magic);
        Write(Offset.AbiVersion, (uint)PowerScalerLabs.Protocol.ProbeProtocol.NativeAbiVersion);
        Write(Offset.HeaderSize, (uint)HeaderSize);
        Write(Offset.EventSize, (uint)EventSize);
        Write(Offset.Capacity, (uint)EventCapacity);
        Write(Offset.State, (uint)NativeState.Created);
        Write(Offset.HostPid, (uint)Environment.ProcessId);
        Write(Offset.GamePid, (uint)gameProcessId);
        Write(Offset.NonceLow, nonceLow);
        Write(Offset.NonceHigh, nonceHigh);
        Write(Offset.QpcFrequency, (ulong)Stopwatch.Frequency);
        WriteHostHeartbeat();
    }

    internal int GameProcessId { get; }
    internal ulong NonceLow { get; }
    internal ulong NonceHigh { get; }
    internal string MappingName { get; }
    internal string CommandEventName { get; }
    internal string EventReadyName { get; }
    internal EventWaitHandle CommandEvent { get; }
    internal EventWaitHandle EventReady { get; }
    internal string SessionId => $"{NonceHigh:X16}{NonceLow:X16}";
    internal NativeState State => (NativeState)ReadUInt32(Offset.State);
    internal long ProbeHeartbeatQpc => checked((long)ReadUInt64(Offset.ProbeHeartbeatQpc));
    internal long ProbeHeartbeatSequence => checked((long)ReadUInt64(Offset.ProbeHeartbeatSequence));
    internal long DroppedEventCount => checked((long)ReadUInt64(Offset.DroppedEventCount));
    internal int ActiveWatchpointCount => checked((int)ReadUInt32(Offset.ActiveWatchpointCount));
    internal uint InitializationStatus => ReadUInt32(Offset.InitializationStatus);

    internal static ProbeSharedMemory Create(int gameProcessId)
    {
        Span<byte> nonce = stackalloc byte[16];
        RandomNumberGenerator.Fill(nonce);
        return new ProbeSharedMemory(
            gameProcessId,
            BitConverter.ToUInt64(nonce[..8]),
            BitConverter.ToUInt64(nonce[8..]));
    }

    internal void WriteHostHeartbeat() => Write(Offset.HostHeartbeatQpc, (ulong)Stopwatch.GetTimestamp());

    internal void RequestShutdown()
    {
        long sequence = Interlocked.Increment(ref _commandSequence);
        Write(Offset.Command, 1u);
        Write(Offset.CommandSequence, (ulong)sequence);
        CommandEvent.Set();
    }

    internal bool IsHandshakeValid() =>
        ReadUInt32(Offset.Magic) == Magic &&
        ReadUInt32(Offset.AbiVersion) == PowerScalerLabs.Protocol.ProbeProtocol.NativeAbiVersion &&
        ReadUInt32(Offset.HeaderSize) == HeaderSize &&
        ReadUInt32(Offset.EventSize) == EventSize &&
        ReadUInt32(Offset.Capacity) == EventCapacity &&
        ReadUInt32(Offset.HostPid) == Environment.ProcessId &&
        ReadUInt32(Offset.GamePid) == GameProcessId &&
        ReadUInt64(Offset.NonceLow) == NonceLow &&
        ReadUInt64(Offset.NonceHigh) == NonceHigh &&
        ReadUInt64(Offset.QpcFrequency) == (ulong)Stopwatch.Frequency;

    private uint ReadUInt32(long offset) => _view.ReadUInt32(offset);
    private ulong ReadUInt64(long offset) => _view.ReadUInt64(offset);
    private void Write(long offset, uint value) => _view.Write(offset, value);
    private void Write(long offset, ulong value) => _view.Write(offset, value);

    public void Dispose()
    {
        EventReady.Dispose();
        CommandEvent.Dispose();
        _view.Dispose();
        _mapping.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
internal struct ProbeInitializationArguments
{
    internal const uint Magic = 0x49534C50;
    internal uint StructureMagic;
    internal uint AbiVersion;
    internal uint StructureSize;
    internal uint HostProcessId;
    internal uint GameProcessId;
    internal uint Reserved;
    internal ulong NonceLow;
    internal ulong NonceHigh;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string MappingName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string CommandEventName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string EventReadyName;

    internal static ProbeInitializationArguments Create(ProbeSharedMemory memory) => new()
    {
        StructureMagic = Magic,
        AbiVersion = PowerScalerLabs.Protocol.ProbeProtocol.NativeAbiVersion,
        StructureSize = (uint)Marshal.SizeOf<ProbeInitializationArguments>(),
        HostProcessId = (uint)Environment.ProcessId,
        GameProcessId = (uint)memory.GameProcessId,
        NonceLow = memory.NonceLow,
        NonceHigh = memory.NonceHigh,
        MappingName = memory.MappingName,
        CommandEventName = memory.CommandEventName,
        EventReadyName = memory.EventReadyName
    };
}
