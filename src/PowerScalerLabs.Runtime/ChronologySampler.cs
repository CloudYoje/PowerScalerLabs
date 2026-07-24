using System.Collections.Concurrent;
using System.Diagnostics;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal sealed class ChronologySampler : IDisposable
{
    private const int MaximumPendingSamples = 20_000;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReaderRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<ChronologySampleMessage> _pending = new();
    private readonly Task _worker;

    private ChronologyConfiguration _configuration = Normalize(ChronologyConfiguration.Default);
    private FighterTarget[] _fighters = [];
    private int? _processId;
    private long _stateGeneration;
    private string _detail = "Chronology sampler is configured and waiting for validated fighter objects.";
    private long _pollCount;
    private long _readCount;
    private long _emittedSampleCount;
    private long _changedSampleCount;
    private long _unreadableReadCount;
    private long _droppedSampleCount;
    private long _invalidatedSampleCount;
    private long _deliveredSequence;
    private long _epochEmittedSampleCount;
    private long _epochInitialSampleCount;
    private long _epochChangedSampleCount;
    private long _epochPollCount;
    private long _epochReadCount;
    private long _epochUnreadableReadCount;
    private long _epochDroppedSampleCount;
    private long _epochPollOverrunCount;
    private double _epochMaximumPollDurationMilliseconds;
    private long _pollOverrunCount;
    private double _lastPollDurationMilliseconds;
    private double _maximumPollDurationMilliseconds;
    private DateTimeOffset? _lastSampleUtc;
    private long? _lastSampleMonotonicTicks;
    private bool _workerSamplingActive;
    private MemoryAccessMetricsMessage _memoryMetrics = EmptyMetrics();
    private bool _disposed;

    internal ChronologySampler()
    {
        _worker = Task.Run(WorkerAsync);
    }

    internal static bool IsChronologyCommand(RuntimeCommand command)
    {
        string normalized = command.Command.Trim().ToLowerInvariant();
        return normalized is "configure_chronology" or "pause_chronology" or
            "resume_chronology" or "reset_chronology" or "new_chronology_epoch";
    }

    internal void ApplyCommands(IReadOnlyList<RuntimeCommand> commands)
    {
        foreach (RuntimeCommand command in commands)
        {
            string normalized = command.Command.Trim().ToLowerInvariant();
            lock (_stateGate)
            {
                switch (normalized)
                {
                    case "configure_chronology":
                        _configuration = Normalize(command.ChronologyConfiguration ?? ChronologyConfiguration.Default);
                        AdvanceEpochLocked($"Chronology configured for {_configuration.Targets.Count} target(s) at {_configuration.IntervalMs} ms.");
                        break;
                    case "pause_chronology":
                        _configuration = _configuration with { Enabled = false };
                        _detail = "Chronology pause requested; waiting for the active poll to finish.";
                        break;
                    case "resume_chronology":
                        _configuration = _configuration with { Enabled = true };
                        AdvanceEpochLocked("Chronology sampling resumed.");
                        break;
                    case "reset_chronology":
                        _configuration = Normalize(ChronologyConfiguration.Default);
                        AdvanceEpochLocked("Chronology watchlist reset to the six focused resource anchors.");
                        break;
                    case "new_chronology_epoch":
                        AdvanceEpochLocked(command.Label is { Length: > 0 }
                            ? $"Chronology epoch started: {command.Label}."
                            : "New chronology epoch started.");
                        break;
                }
            }
        }
    }

    internal void UpdateTarget(int? processId, IReadOnlyList<FighterSnapshot> fighters)
    {
        FighterTarget[] next = fighters
            .OrderBy(fighter => fighter.Slot)
            .Select(fighter => new FighterTarget(
                fighter.Slot,
                fighter.ActorAddress,
                fighter.Identity.IdentityKey,
                fighter.Identity.SlotGeneration))
            .ToArray();

        lock (_stateGate)
        {
            bool changed = _processId != processId || !_fighters.SequenceEqual(next);
            _processId = processId;
            _fighters = next;
            if (changed)
            {
                AdvanceEpochLocked(processId is null
                    ? "Chronology target detached."
                    : "Chronology fighter target set changed.");
            }

            if (processId is null)
            {
                _detail = "Chronology sampler is waiting for DBXV2.exe.";
            }
            else if (next.Length == 0)
            {
                _detail = "Chronology sampler is attached read-only and waiting for validated fighter objects.";
            }
        }
    }

    internal ChronologyFrame DrainFrame()
    {
        List<ChronologySampleMessage> samples = [];
        lock (_stateGate)
        {
            long currentEpoch = _stateGeneration;
            while (samples.Count < RuntimeProtocol.MaximumChronologyBatch &&
                   _pending.TryDequeue(out ChronologySampleMessage? sample))
            {
                if (sample is null)
                {
                    _invalidatedSampleCount++;
                    continue;
                }

                if (sample.Epoch != currentEpoch)
                {
                    _invalidatedSampleCount++;
                    continue;
                }

                ChronologySampleMessage sequenced = sample with { Sequence = ++_deliveredSequence };
                samples.Add(sequenced);
                _emittedSampleCount++;
                _epochEmittedSampleCount++;
                if (sequenced.Initial)
                {
                    _epochInitialSampleCount++;
                }
                if (sequenced.Changed)
                {
                    _changedSampleCount++;
                    _epochChangedSampleCount++;
                }
                _lastSampleUtc = sequenced.TimestampUtc;
                _lastSampleMonotonicTicks = sequenced.MonotonicTicks;
            }

            bool active = _workerSamplingActive;
            ChronologyStatusMessage status = new(
                true,
                _configuration.Enabled,
                active,
                _detail,
                _configuration,
                Math.Min(_fighters.Length, _configuration.MaximumFighters),
                _configuration.Targets.Count,
                _stateGeneration,
                _epochEmittedSampleCount,
                _epochInitialSampleCount,
                _epochChangedSampleCount,
                _epochPollCount,
                _epochReadCount,
                _epochUnreadableReadCount,
                _epochDroppedSampleCount,
                _epochPollOverrunCount,
                _epochMaximumPollDurationMilliseconds,
                _pollCount,
                _readCount,
                _emittedSampleCount,
                _changedSampleCount,
                _unreadableReadCount,
                _pending.Count,
                _droppedSampleCount,
                _invalidatedSampleCount,
                _pollOverrunCount,
                _lastPollDurationMilliseconds,
                _maximumPollDurationMilliseconds,
                _lastSampleUtc,
                _lastSampleMonotonicTicks);
            return new ChronologyFrame(status, samples, _memoryMetrics);
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        _shutdown.Cancel();
        try
        {
            if (!_worker.Wait(TimeSpan.FromSeconds(5)))
            {
                RuntimeLog.Write("Chronology sampler did not stop within the five-second shutdown audit window.");
            }
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            // Expected during shutdown.
        }
        finally
        {
            if (_worker.IsCompleted)
            {
                _shutdown.Dispose();
            }
        }
    }

    private async Task WorkerAsync()
    {
        GameMemoryReader? reader = null;
        int? attachedProcessId = null;
        long observedGeneration = -1;
        Dictionary<WatchKey, WatchValue> previous = [];
        byte[] readBuffer = new byte[sizeof(ulong)];
        long captureId = 0;

        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                StateSnapshot state = SnapshotState();
                if (state.Generation != observedGeneration)
                {
                    previous.Clear();
                    observedGeneration = state.Generation;
                }

                if (!state.Configuration.Enabled || state.ProcessId is null || state.Fighters.Length == 0 ||
                    state.Configuration.Targets.Count == 0)
                {
                    lock (_stateGate)
                    {
                        _workerSamplingActive = false;
                        if (!_configuration.Enabled)
                        {
                            _detail = "Chronology sampling paused; no poll is active.";
                        }
                    }
                    if (attachedProcessId != state.ProcessId)
                    {
                        reader?.Dispose();
                        reader = null;
                        attachedProcessId = null;
                    }
                    await Task.Delay(IdleDelay, _shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                if (reader is null || attachedProcessId != state.ProcessId)
                {
                    reader?.Dispose();
                    reader = null;
                    attachedProcessId = null;
                    try
                    {
                        reader = new GameMemoryReader(state.ProcessId.Value);
                        attachedProcessId = state.ProcessId;
                        SetDetail($"Chronology sampler attached read-only to DBXV2 PID {state.ProcessId.Value}.");
                    }
                    catch (Exception exception)
                    {
                        SetDetail($"Chronology read-only attach failed: {exception.Message}");
                        await Task.Delay(ReaderRetryDelay, _shutdown.Token).ConfigureAwait(false);
                        continue;
                    }
                }

                lock (_stateGate)
                {
                    _workerSamplingActive = true;
                }
                long pollStarted = Stopwatch.GetTimestamp();
                DateTimeOffset pollStartedUtc = DateTimeOffset.UtcNow;
                long currentCaptureId = ++captureId;
                int reads = 0;
                int unreadable = 0;
                List<PendingSample> emitted = [];

                foreach (FighterTarget fighter in state.Fighters.Take(state.Configuration.MaximumFighters))
                {
                    foreach (ChronologyWatchTarget target in state.Configuration.Targets)
                    {
                        if (!string.Equals(target.RegionPath, "Battle_Mob", StringComparison.Ordinal))
                        {
                            unreadable++;
                            continue;
                        }
                        if (fighter.ActorAddress > ulong.MaxValue - target.ObjectOffset)
                        {
                            unreadable++;
                            continue;
                        }

                        ulong observedAddress = fighter.ActorAddress + target.ObjectOffset;
                        reads++;
                        if (!TryReadValue(reader, observedAddress, target.ValueType, readBuffer, out WatchValue current))
                        {
                            unreadable++;
                            continue;
                        }

                        long sampleTicks = Stopwatch.GetTimestamp();
                        WatchKey key = new(
                            fighter.Slot,
                            fighter.ActorAddress,
                            fighter.IdentityKey,
                            fighter.SlotGeneration,
                            target.RegionPath,
                            target.ObjectOffset,
                            target.ValueType);
                        bool initial = !previous.TryGetValue(key, out WatchValue prior);
                        bool changed = !initial && !ValuesEquivalent(prior, current);
                        previous[key] = current;
                        if (!initial && !changed)
                        {
                            continue;
                        }

                        double elapsedSeconds = (sampleTicks - pollStarted) / (double)Stopwatch.Frequency;
                        DateTimeOffset sampleUtc = pollStartedUtc + TimeSpan.FromSeconds(elapsedSeconds);
                        emitted.Add(new PendingSample(
                            currentCaptureId,
                            state.Generation,
                            sampleUtc,
                            sampleTicks,
                            fighter,
                            target,
                            observedAddress,
                            current,
                            initial ? current : prior,
                            changed,
                            initial));
                    }
                }

                long pollCompleted = Stopwatch.GetTimestamp();
                double durationMilliseconds = (pollCompleted - pollStarted) * 1000.0 / Stopwatch.Frequency;
                foreach (PendingSample pendingSample in emitted)
                {
                    ChronologySampleMessage sample = new(
                        0,
                        pendingSample.CaptureId,
                        pendingSample.Epoch,
                        pendingSample.TimestampUtc,
                        pendingSample.MonotonicTicks,
                        pollStarted,
                        pollCompleted,
                        pendingSample.Fighter.Slot,
                        pendingSample.Fighter.ActorAddress,
                        pendingSample.Fighter.IdentityKey,
                        pendingSample.Fighter.SlotGeneration,
                        pendingSample.Target.RegionPath,
                        pendingSample.Target.ObjectOffset,
                        pendingSample.ObservedAddress,
                        pendingSample.Target.ValueType,
                        pendingSample.Current.RawValue,
                        pendingSample.Previous.RawValue,
                        pendingSample.Current.NumericValue,
                        pendingSample.Previous.NumericValue,
                        pendingSample.Current.NumericValue - pendingSample.Previous.NumericValue,
                        pendingSample.Changed,
                        pendingSample.Initial,
                        pendingSample.Target.Label,
                        pendingSample.Target.ValidationStage);
                    Enqueue(sample);
                }

                lock (_stateGate)
                {
                    _pollCount++;
                    _readCount += reads;
                    _unreadableReadCount += unreadable;
                    _memoryMetrics = reader.SnapshotMetrics("chronology");
                    _lastPollDurationMilliseconds = durationMilliseconds;
                    _maximumPollDurationMilliseconds = Math.Max(_maximumPollDurationMilliseconds, durationMilliseconds);
                    bool overrun = durationMilliseconds > state.Configuration.IntervalMs;
                    if (overrun)
                    {
                        _pollOverrunCount++;
                    }
                    _workerSamplingActive = false;
                    if (_stateGeneration == state.Generation)
                    {
                        _epochPollCount++;
                        _epochReadCount += reads;
                        _epochUnreadableReadCount += unreadable;
                        _epochMaximumPollDurationMilliseconds = Math.Max(
                            _epochMaximumPollDurationMilliseconds, durationMilliseconds);
                        if (overrun)
                        {
                            _epochPollOverrunCount++;
                        }
                        if (_configuration.Enabled)
                        {
                            _detail = $"Chronology sampling {state.Configuration.Targets.Count} target(s) across " +
                                $"{Math.Min(state.Fighters.Length, state.Configuration.MaximumFighters)} fighter(s) every {state.Configuration.IntervalMs} ms.";
                        }
                    }
                }

                int remainingDelay = Math.Max(1, state.Configuration.IntervalMs - (int)Math.Ceiling(durationMilliseconds));
                await Task.Delay(TimeSpan.FromMilliseconds(remainingDelay), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            SetDetail($"Chronology sampler stopped after an unexpected error: {exception.Message}");
            RuntimeLog.Write($"Chronology sampler error: {exception}");
        }
        finally
        {
            lock (_stateGate)
            {
                _workerSamplingActive = false;
                if (reader is not null)
                {
                    _memoryMetrics = reader.SnapshotMetrics("chronology");
                }
            }
            reader?.Dispose();
        }
    }

    private StateSnapshot SnapshotState()
    {
        lock (_stateGate)
        {
            return new StateSnapshot(_configuration, _processId, _fighters, _stateGeneration);
        }
    }

    private void SetDetail(string detail)
    {
        lock (_stateGate)
        {
            _detail = detail;
        }
    }

    private void Enqueue(ChronologySampleMessage sample)
    {
        lock (_stateGate)
        {
            if (sample.Epoch != _stateGeneration)
            {
                _invalidatedSampleCount++;
                return;
            }

            while (_pending.Count >= MaximumPendingSamples &&
                   _pending.TryDequeue(out ChronologySampleMessage? discarded))
            {
                if (discarded is null)
                {
                    _invalidatedSampleCount++;
                    continue;
                }

                if (discarded.Epoch == _stateGeneration)
                {
                    _droppedSampleCount++;
                    _epochDroppedSampleCount++;
                }
                else
                {
                    _invalidatedSampleCount++;
                }
            }
            _pending.Enqueue(sample);
        }
    }

    private void AdvanceEpochLocked(string detail)
    {
        _stateGeneration++;
        _epochEmittedSampleCount = 0;
        _epochInitialSampleCount = 0;
        _epochChangedSampleCount = 0;
        _epochPollCount = 0;
        _epochReadCount = 0;
        _epochUnreadableReadCount = 0;
        _epochDroppedSampleCount = 0;
        _epochPollOverrunCount = 0;
        _epochMaximumPollDurationMilliseconds = 0;
        _detail = detail;
    }

    private static ChronologyConfiguration Normalize(ChronologyConfiguration configuration)
    {
        ChronologyWatchTarget[] targets = (configuration.Targets ?? [])
            .Where(target => string.Equals(target.RegionPath, "Battle_Mob", StringComparison.Ordinal))
            .GroupBy(target => $"{target.RegionPath}:{target.ObjectOffset:X}:{target.ValueType}", StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(RuntimeProtocol.MaximumChronologyTargets)
            .ToArray();
        if (targets.Length == 0)
        {
            targets = ChronologyConfiguration.Default.Targets.ToArray();
        }

        return new ChronologyConfiguration(
            configuration.Enabled,
            Math.Clamp(configuration.IntervalMs, 10, 1000),
            Math.Clamp(configuration.MaximumFighters, 1, RuntimeProtocol.ObservedFighterSlotCount),
            targets);
    }

    private static bool TryReadValue(
        GameMemoryReader reader,
        ulong address,
        ScannerValueType valueType,
        byte[] buffer,
        out WatchValue value)
    {
        int size = SizeOf(valueType);
        if (!reader.TryReadKnownReadable(address, buffer, size))
        {
            value = default;
            return false;
        }

        ulong raw;
        double numeric;
        switch (valueType)
        {
            case ScannerValueType.Byte:
                raw = buffer[0];
                numeric = raw;
                break;
            case ScannerValueType.Int16:
                short int16 = BitConverter.ToInt16(buffer, 0);
                raw = unchecked((ushort)int16);
                numeric = int16;
                break;
            case ScannerValueType.UInt16:
                ushort uint16 = BitConverter.ToUInt16(buffer, 0);
                raw = uint16;
                numeric = uint16;
                break;
            case ScannerValueType.Int32:
                int int32 = BitConverter.ToInt32(buffer, 0);
                raw = unchecked((uint)int32);
                numeric = int32;
                break;
            case ScannerValueType.UInt32:
                uint uint32 = BitConverter.ToUInt32(buffer, 0);
                raw = uint32;
                numeric = uint32;
                break;
            case ScannerValueType.Float32:
                raw = BitConverter.ToUInt32(buffer, 0);
                float float32 = BitConverter.ToSingle(buffer, 0);
                numeric = float32;
                if (!float.IsFinite(float32))
                {
                    value = default;
                    return false;
                }
                break;
            case ScannerValueType.Int64:
                long int64 = BitConverter.ToInt64(buffer, 0);
                raw = unchecked((ulong)int64);
                numeric = int64;
                break;
            case ScannerValueType.UInt64:
            case ScannerValueType.Pointer64:
                raw = BitConverter.ToUInt64(buffer, 0);
                numeric = raw;
                break;
            case ScannerValueType.Float64:
                raw = BitConverter.ToUInt64(buffer, 0);
                double float64 = BitConverter.ToDouble(buffer, 0);
                numeric = float64;
                if (!double.IsFinite(float64))
                {
                    value = default;
                    return false;
                }
                break;
            default:
                value = default;
                return false;
        }

        value = new WatchValue(raw, numeric);
        return true;
    }

    private static bool ValuesEquivalent(WatchValue previous, WatchValue current) =>
        previous.RawValue == current.RawValue;

    private static int SizeOf(ScannerValueType type) => type switch
    {
        ScannerValueType.Byte => 1,
        ScannerValueType.Int16 or ScannerValueType.UInt16 => 2,
        ScannerValueType.Float32 or ScannerValueType.Int32 or ScannerValueType.UInt32 => 4,
        ScannerValueType.Float64 or ScannerValueType.Int64 or ScannerValueType.UInt64 or ScannerValueType.Pointer64 => 8,
        _ => 4
    };

    private static MemoryAccessMetricsMessage EmptyMetrics() => new("chronology", 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private readonly record struct FighterTarget(
        int Slot,
        ulong ActorAddress,
        string IdentityKey,
        long SlotGeneration);
    private readonly record struct WatchKey(
        int Slot,
        ulong ActorAddress,
        string IdentityKey,
        long SlotGeneration,
        string RegionPath,
        uint ObjectOffset,
        ScannerValueType ValueType);
    private readonly record struct WatchValue(ulong RawValue, double NumericValue);
    private readonly record struct StateSnapshot(
        ChronologyConfiguration Configuration,
        int? ProcessId,
        FighterTarget[] Fighters,
        long Generation);
    private readonly record struct PendingSample(
        long CaptureId,
        long Epoch,
        DateTimeOffset TimestampUtc,
        long MonotonicTicks,
        FighterTarget Fighter,
        ChronologyWatchTarget Target,
        ulong ObservedAddress,
        WatchValue Current,
        WatchValue Previous,
        bool Changed,
        bool Initial);
}

internal sealed record ChronologyFrame(
    ChronologyStatusMessage Status,
    IReadOnlyList<ChronologySampleMessage> Samples,
    MemoryAccessMetricsMessage MemoryMetrics);
