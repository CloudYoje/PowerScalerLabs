using System.Diagnostics;
using System.Buffers.Binary;
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
        internal const long CommandResultCode = 132;
        internal const long CommandTraceSessionId = 136;
        internal const long CommandWatchId = 144;
        internal const long CommandTargetAddress = 152;
        internal const long CommandWidth = 160;
        internal const long CommandAccessType = 164;
        internal const long CommandEventCount = 168;
        internal const long CommandIntervalMilliseconds = 172;
        internal const long CommandGeneratedEventCount = 176;
        internal const long CommandResultDetail = 180;
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
    private readonly SemaphoreSlim _commandGate = new(1, 1);

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

    internal async Task<NativeCommandOutcome> EmitSyntheticEventsAsync(
        ulong traceSessionId,
        ulong watchId,
        int count,
        int intervalMilliseconds,
        CancellationToken cancellationToken)
    {
        if (count is < 1 or > PowerScalerLabs.Protocol.ProbeProtocol.MaximumEventBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        if (intervalMilliseconds is < 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        }

        return await ExecuteCommandAsync(2u, traceSessionId, watchId, 0, 0, 0,
            checked((uint)count), checked((uint)intervalMilliseconds),
            TimeSpan.FromSeconds(Math.Max(10, count * intervalMilliseconds / 1000 + 10)), cancellationToken).ConfigureAwait(false);
    }

    internal Task<NativeCommandOutcome> ArmWriteWatchAsync(
        ulong traceSessionId, ulong watchId, ulong address, CancellationToken cancellationToken) =>
        ExecuteCommandAsync(3u, traceSessionId, watchId, address, 4u, 1u, 0u, 0u, TimeSpan.FromSeconds(15), cancellationToken);

    internal Task<NativeCommandOutcome> DisarmWatchAsync(CancellationToken cancellationToken) =>
        ExecuteCommandAsync(4u, 0, 0, 0, 0, 0, 0, 0, TimeSpan.FromSeconds(15), cancellationToken);

    private async Task<NativeCommandOutcome> ExecuteCommandAsync(uint command, ulong traceSessionId, ulong watchId,
        ulong address, uint width, uint accessType, uint eventCount, uint intervalMilliseconds, TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ulong sequence = checked((ulong)Interlocked.Increment(ref _commandSequence));
            Write(Offset.CommandTraceSessionId, traceSessionId);
            Write(Offset.CommandWatchId, watchId);
            Write(Offset.CommandTargetAddress, address);
            Write(Offset.CommandWidth, width);
            Write(Offset.CommandAccessType, accessType);
            Write(Offset.CommandEventCount, eventCount);
            Write(Offset.CommandIntervalMilliseconds, intervalMilliseconds);
            Write(Offset.CommandGeneratedEventCount, 0u);
            Write(Offset.CommandResultDetail, 0u);
            Write(Offset.CommandResultCode, uint.MaxValue);
            Write(Offset.Command, command);
            Write(Offset.CommandSequence, sequence);
            CommandEvent.Set();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(commandTimeout);
            while (!timeout.IsCancellationRequested)
            {
                if (ReadUInt64(Offset.CommandAckSequence) == sequence)
                    return new NativeCommandOutcome(ReadUInt32(Offset.CommandResultCode),
                        checked((int)ReadUInt32(Offset.CommandGeneratedEventCount)), ReadUInt32(Offset.CommandResultDetail));
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
            }
            throw new TimeoutException("Native command did not acknowledge within the bounded timeout.");
        }
        finally { _commandGate.Release(); }
    }

    internal IReadOnlyList<PowerScalerLabs.Protocol.ProbeEventMessage> DrainCommittedEvents()
    {
        List<PowerScalerLabs.Protocol.ProbeEventMessage> events = [];
        ulong readSequence = ReadUInt64(Offset.EventReadSequence);
        while (events.Count < EventCapacity)
        {
            ulong expected = readSequence + 1;
            int slot = checked((int)((expected - 1) % EventCapacity));
            long eventOffset = HeaderSize + slot * EventSize;
            if (ReadUInt64(eventOffset) != expected)
            {
                break;
            }

            byte[] bytes = new byte[EventSize];
            _view.ReadArray(eventOffset, bytes, 0, bytes.Length);
            Thread.MemoryBarrier();
            if (ReadUInt64(eventOffset) != expected || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != expected)
            {
                break;
            }
            events.Add(ParseEvent(bytes));
            readSequence = expected;
            Write(Offset.EventReadSequence, readSequence);
        }
        return events;
    }

    private static PowerScalerLabs.Protocol.ProbeEventMessage ParseEvent(ReadOnlySpan<byte> bytes)
    {
        ulong[] registers = new ulong[16];
        for (int index = 0; index < registers.Length; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(64 + index * 8, 8));
        }
        return new PowerScalerLabs.Protocol.ProbeEventMessage(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8)),
            checked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(16, 8))),
            (PowerScalerLabs.Protocol.ProbeEventType)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(228, 4)),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(224, 4))),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(40, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(48, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(56, 8)),
            registers,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(192, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(200, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(208, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(216, 8)),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(232, 4))),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(236, 4))),
            "NativeProbe");
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
        _commandGate.Dispose();
    }
}

internal readonly record struct NativeCommandOutcome(uint ResultCode, int GeneratedEventCount, uint ResultDetail);

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
