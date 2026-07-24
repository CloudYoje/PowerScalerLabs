using System.Globalization;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal sealed class ObjectCapabilityScanner
{
    private const int ReadChunkSize = 0x200;
    private const int MaximumPendingObservations = 250_000;


    private readonly Queue<ScannerObservationMessage> _pending = new();
    private readonly Dictionary<int, FighterCapture> _baselineBySlot = [];
    private readonly Dictionary<int, FighterCapture> _lastBySlot = [];
    private ScannerConfiguration _configuration = ScannerConfiguration.Default;
    private string? _experimentId;
    private string? _baselineLabel;
    private string _detail = "Scanner configured. Capture a labeled baseline to begin a controlled experiment.";
    private DateTimeOffset? _lastCaptureUtc;
    private long? _lastCaptureMonotonicTicks;
    private DateTimeOffset _lastContinuousUtc = DateTimeOffset.MinValue;
    private int _lastObservationCount;
    private int _lastChangedCount;
    private int _lastStableCount;
    private int _droppedObservationCount;
    private int _baselineRegionCount;
    private int _baselineValueCount;

    internal ScannerFrame Process(
        GameMemoryReader reader,
        IReadOnlyList<FighterSnapshot> fighters,
        IReadOnlyList<RuntimeCommand> commands,
        DateTimeOffset now,
        long monotonicTicks,
        List<TelemetryEventMessage> events)
    {
        InvalidateStaleBaseline(fighters, now, monotonicTicks, events);

        foreach (RuntimeCommand command in commands)
        {
            HandleCommand(reader, fighters, command, now, monotonicTicks, events);
        }

        int effectiveContinuousIntervalMs = GetEffectiveContinuousIntervalMs();
        bool continuousScopeSafe = EstimateObservationCount(
            Math.Min(fighters.Count, _configuration.MaximumFighters)) <= RuntimeProtocol.MaximumContinuousObservations;
        if (_configuration.ContinuousTracking &&
            continuousScopeSafe &&
            _baselineBySlot.Count > 0 &&
            fighters.Count > 0 &&
            _pending.Count < Math.Max(10_000, _configuration.MaximumObservationsPerFrame * 8) &&
            now - _lastContinuousUtc >= TimeSpan.FromMilliseconds(effectiveContinuousIntervalMs))
        {
            CaptureContinuousChanges(reader, fighters, now, monotonicTicks);
            _lastContinuousUtc = now;
        }

        int outputLimit = Math.Clamp(_configuration.MaximumObservationsPerFrame, 50, RuntimeProtocol.MaximumObservationBatch);
        List<ScannerObservationMessage> outgoing = new(Math.Min(outputLimit, _pending.Count));
        while (outgoing.Count < outputLimit && _pending.Count > 0)
        {
            outgoing.Add(_pending.Dequeue());
        }

        ScannerStatusMessage status = new(
            true,
            _baselineBySlot.Count > 0,
            _experimentId,
            _baselineLabel,
            _detail,
            _configuration,
            _baselineBySlot.Count,
            _baselineRegionCount,
            _baselineValueCount,
            _lastObservationCount,
            _lastChangedCount,
            _lastStableCount,
            _pending.Count,
            _droppedObservationCount,
            _lastCaptureUtc,
            _lastCaptureMonotonicTicks);

        return new ScannerFrame(status, outgoing);
    }

    internal void ResetForDetach()
    {
        _baselineBySlot.Clear();
        _lastBySlot.Clear();
        if (_pending.Count > 0)
        {
            _droppedObservationCount += _pending.Count;
            _pending.Clear();
        }
        _experimentId = null;
        _baselineLabel = null;
        _detail = "Game detached. Scanner baseline cleared.";
        _lastCaptureUtc = null;
        _lastCaptureMonotonicTicks = null;
        _lastObservationCount = 0;
        _lastChangedCount = 0;
        _lastStableCount = 0;
        _baselineRegionCount = 0;
        _baselineValueCount = 0;
        _lastContinuousUtc = DateTimeOffset.MinValue;
    }

    internal ScannerStatusMessage OfflineStatus(string detail) => new(
        true,
        false,
        null,
        null,
        detail,
        _configuration,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        null);

    private void HandleCommand(
        GameMemoryReader reader,
        IReadOnlyList<FighterSnapshot> fighters,
        RuntimeCommand command,
        DateTimeOffset now,
        long monotonicTicks,
        List<TelemetryEventMessage> events)
    {
        string normalized = command.Command.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "configure_scanner":
                _configuration = Normalize(command.ScannerConfiguration ?? ScannerConfiguration.Default);
                long projected = EstimateObservationCount(_configuration.MaximumFighters);
                string continuousNote = _configuration.ContinuousTracking && projected > RuntimeProtocol.MaximumContinuousObservations
                    ? $" Continuous tracking is paused because the scope projects {projected:N0} observations; the continuous limit is {RuntimeProtocol.MaximumContinuousObservations:N0}."
                    : string.Empty;
                ClearBaseline($"Scanner configuration changed; capture a new baseline.{continuousNote}");
                events.Add(ScannerEvent(TelemetryEventKind.ScannerConfigured, command.Label ?? "Scanner configured", now, monotonicTicks));
                break;

            case "capture_baseline":
                CaptureBaseline(reader, fighters, command.Label, now, monotonicTicks, events);
                break;

            case "compare_after":
                CaptureComparison(reader, fighters, command.Label, now, monotonicTicks, events);
                break;

            case "capture_full_snapshot":
            case "capture_snapshot":
                CaptureFullSnapshot(reader, fighters, command.Label, now, monotonicTicks, events);
                break;

            case "clear_baseline":
                ClearBaseline("Baseline cleared by the user.");
                events.Add(ScannerEvent(TelemetryEventKind.ScannerBaselineCleared, "Scanner baseline cleared", now, monotonicTicks));
                break;
        }
    }

    private void CaptureBaseline(
        GameMemoryReader reader,
        IReadOnlyList<FighterSnapshot> fighters,
        string? label,
        DateTimeOffset now,
        long monotonicTicks,
        List<TelemetryEventMessage> events)
    {
        IReadOnlyList<FighterSnapshot> selected = SelectFighters(fighters);
        if (selected.Count == 0)
        {
            _detail = "Baseline request received, but no validated fighter objects are active.";
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }

        if (!CanCaptureComplete(selected.Count, out long projectedObservations))
        {
            _detail = $"Capture rejected: the current scanner configuration projects {projectedObservations:N0} typed observations, " +
                $"above the complete-capture limit of {RuntimeProtocol.MaximumCompleteCaptureObservations:N0}. Reduce range, value types, fighters, or pointer children.";
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }

        _experimentId = $"{now:yyyyMMdd-HHmmss}-{SanitizeLabel(label, "Capability-Experiment")}";
        _baselineLabel = string.IsNullOrWhiteSpace(label) ? "Baseline" : label.Trim();
        _baselineBySlot.Clear();
        _lastBySlot.Clear();

        int capturedValues = 0;
        foreach (FighterSnapshot fighter in selected)
        {
            FighterCapture capture = CaptureFighter(reader, fighter, _configuration);
            _baselineBySlot[fighter.Slot] = capture;
            _lastBySlot[fighter.Slot] = capture;
            SnapshotEnqueueResult result = EnqueueSnapshotObservations(
                capture,
                ScanObservationPhase.Baseline,
                _baselineLabel,
                now,
                monotonicTicks,
                previous: null);
            capturedValues += result.Emitted;
        }

        _baselineRegionCount = _baselineBySlot.Values.Sum(capture => capture.Regions.Count);
        _baselineValueCount = capturedValues;
        if (_baselineValueCount == 0)
        {
            ClearBaseline("Baseline capture produced no readable, plausible values. Verify fighter state and reduce the scan range.");
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }
        _lastObservationCount = capturedValues;
        _lastChangedCount = 0;
        _lastStableCount = 0;
        _lastContinuousUtc = now;
        _lastCaptureUtc = now;
        _lastCaptureMonotonicTicks = monotonicTicks;
        _detail = $"Baseline '{_baselineLabel}' captured for {_baselineBySlot.Count} fighter(s), " +
            $"{_baselineRegionCount} region(s), and {_baselineValueCount:N0} plausible typed values.";
        events.Add(ScannerEvent(TelemetryEventKind.ScannerBaselineCaptured, _detail, now, monotonicTicks));
    }

    private void CaptureComparison(
        GameMemoryReader reader,
        IReadOnlyList<FighterSnapshot> fighters,
        string? label,
        DateTimeOffset now,
        long monotonicTicks,
        List<TelemetryEventMessage> events)
    {
        if (_baselineBySlot.Count == 0 || string.IsNullOrWhiteSpace(_experimentId))
        {
            _detail = "Comparison request rejected because no baseline has been captured.";
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }

        IReadOnlyList<FighterSnapshot> selected = SelectFighters(fighters);
        if (selected.Count == 0)
        {
            _detail = "Comparison request received, but no validated fighter objects are active.";
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }
        if (!CanCaptureComplete(selected.Count, out long projectedObservations))
        {
            _detail = $"Comparison rejected: the current scanner configuration projects {projectedObservations:N0} typed observations, " +
                $"above the complete-capture limit of {RuntimeProtocol.MaximumCompleteCaptureObservations:N0}. Reduce scan scope before comparing.";
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }

        string action = string.IsNullOrWhiteSpace(label) ? "Unlabeled action" : label.Trim();
        int changed = 0;
        int stable = 0;
        int matchedFighters = 0;

        foreach (FighterSnapshot fighter in selected)
        {
            FighterCapture current = CaptureFighter(reader, fighter, _configuration);
            if (!_baselineBySlot.TryGetValue(fighter.Slot, out FighterCapture? baseline) ||
                baseline.ActorAddress != current.ActorAddress)
            {
                continue;
            }

            matchedFighters++;
            (int changedCount, int stableCount) = EnqueueComparisonObservations(
                baseline,
                current,
                ScanObservationPhase.Comparison,
                action,
                now,
                monotonicTicks,
                includeStable: true);
            changed += changedCount;
            stable += stableCount;
            _lastBySlot[fighter.Slot] = current;
        }

        if (matchedFighters == 0)
        {
            _detail = "Comparison rejected because none of the active fighter objects match the captured baseline.";
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }

        _lastObservationCount = changed + stable;
        _lastChangedCount = changed;
        _lastStableCount = stable;
        _lastCaptureUtc = now;
        _lastCaptureMonotonicTicks = monotonicTicks;
        _detail = $"Comparison '{action}' captured: {changed:N0} changed and {stable:N0} stable typed observations.";
        events.Add(ScannerEvent(TelemetryEventKind.ScannerComparisonCompleted, _detail, now, monotonicTicks));
    }

    private void CaptureFullSnapshot(
        GameMemoryReader reader,
        IReadOnlyList<FighterSnapshot> fighters,
        string? label,
        DateTimeOffset now,
        long monotonicTicks,
        List<TelemetryEventMessage> events)
    {
        IReadOnlyList<FighterSnapshot> selected = SelectFighters(fighters);
        if (selected.Count == 0)
        {
            _detail = "Snapshot request received, but no validated fighter objects are active.";
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }

        if (!CanCaptureComplete(selected.Count, out long projectedObservations))
        {
            _detail = $"Snapshot rejected: the current scanner configuration projects {projectedObservations:N0} typed observations, " +
                $"above the complete-capture limit of {RuntimeProtocol.MaximumCompleteCaptureObservations:N0}. Reduce scan scope before taking a snapshot.";
            events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
            return;
        }

        string action = string.IsNullOrWhiteSpace(label) ? "Full object snapshot" : label.Trim();
        int capturedObservations = 0;
        int changedObservations = 0;
        int stableObservations = 0;
        foreach (FighterSnapshot fighter in selected)
        {
            FighterCapture current = CaptureFighter(reader, fighter, _configuration);
            _baselineBySlot.TryGetValue(fighter.Slot, out FighterCapture? baseline);
            SnapshotEnqueueResult result = EnqueueSnapshotObservations(
                current,
                ScanObservationPhase.Snapshot,
                action,
                now,
                monotonicTicks,
                baseline);
            capturedObservations += result.Emitted;
            changedObservations += result.Changed;
            stableObservations += result.Stable;
            _lastBySlot[fighter.Slot] = current;
        }

        _lastObservationCount = capturedObservations;
        _lastChangedCount = changedObservations;
        _lastStableCount = stableObservations;
        _lastCaptureUtc = now;
        _lastCaptureMonotonicTicks = monotonicTicks;
        _detail = $"Full snapshot '{action}' captured {_lastObservationCount:N0} typed observations.";
        events.Add(ScannerEvent(TelemetryEventKind.ScannerSnapshotCaptured, _detail, now, monotonicTicks));
    }

    private void CaptureContinuousChanges(
        GameMemoryReader reader,
        IReadOnlyList<FighterSnapshot> fighters,
        DateTimeOffset now,
        long monotonicTicks)
    {
        int changed = 0;
        foreach (FighterSnapshot fighter in SelectFighters(fighters))
        {
            FighterCapture current = CaptureFighter(reader, fighter, _configuration);
            if (_lastBySlot.TryGetValue(fighter.Slot, out FighterCapture? previous) &&
                previous.ActorAddress == current.ActorAddress)
            {
                (int changedCount, _) = EnqueueComparisonObservations(
                    previous,
                    current,
                    ScanObservationPhase.ContinuousDelta,
                    "Continuous telemetry",
                    now,
                    monotonicTicks,
                    includeStable: false);
                changed += changedCount;
            }
            _lastBySlot[fighter.Slot] = current;
        }

        if (changed > 0)
        {
            _lastObservationCount = changed;
            _lastChangedCount = changed;
            _lastStableCount = 0;
            _lastCaptureUtc = now;
            _detail = $"Continuous scanner observed {changed:N0} changed typed values.";
        }
    }

    private IReadOnlyList<FighterSnapshot> SelectFighters(IReadOnlyList<FighterSnapshot> fighters) =>
        fighters
            .OrderBy(fighter => fighter.Slot)
            .Take(Math.Clamp(_configuration.MaximumFighters, 1, RuntimeProtocol.ObservedFighterSlotCount))
            .ToArray();

    private FighterCapture CaptureFighter(
        GameMemoryReader reader,
        FighterSnapshot fighter,
        ScannerConfiguration configuration)
    {
        List<RegionCapture> regions = [];
        RegionCapture root = ReadRegion(
            reader,
            "Battle_Mob",
            fighter.ActorAddress,
            configuration.StartOffset,
            configuration.EndOffset);
        regions.Add(root);

        if (configuration.FollowPointers && configuration.PointerDepth > 0 && configuration.MaximumChildObjects > 0)
        {
            HashSet<ulong> visited = [fighter.ActorAddress];
            Queue<(RegionCapture Region, int Depth)> frontier = new();
            frontier.Enqueue((root, 0));
            int childCount = 0;

            while (frontier.Count > 0 && childCount < configuration.MaximumChildObjects)
            {
                (RegionCapture parent, int depth) = frontier.Dequeue();
                if (depth >= configuration.PointerDepth)
                {
                    continue;
                }

                for (uint offset = AlignUp(parent.StartOffset, 8);
                     offset + sizeof(ulong) - 1 <= parent.EndOffset && childCount < configuration.MaximumChildObjects;
                     offset += 8)
                {
                    if (!parent.TryReadUInt64(offset, out ulong pointer) ||
                        pointer == 0 ||
                        visited.Contains(pointer) ||
                        !reader.IsLikelyHeapObject(pointer) ||
                        !reader.IsReadableRange(pointer, Math.Min(configuration.ChildScanSize, 16u)))
                    {
                        continue;
                    }

                    visited.Add(pointer);
                    uint childEnd = Math.Max(0x3Fu, configuration.ChildScanSize - 1);
                    string path = $"{parent.Path}/+0x{offset:X}->object";
                    RegionCapture child = ReadRegion(reader, path, pointer, 0, childEnd);
                    regions.Add(child);
                    frontier.Enqueue((child, depth + 1));
                    childCount++;
                }
            }
        }

        return new FighterCapture(fighter.Slot, fighter.ActorAddress, regions);
    }

    private static RegionCapture ReadRegion(
        GameMemoryReader reader,
        string path,
        ulong baseAddress,
        uint startOffset,
        uint endOffset)
    {
        const int decodeTailBytes = sizeof(ulong) - 1;
        int length = checked((int)(endOffset - startOffset + 1) + decodeTailBytes);
        byte[] bytes = new byte[length];
        bool[] valid = new bool[length];
        if (baseAddress > ulong.MaxValue - startOffset)
        {
            return new RegionCapture(path, baseAddress, startOffset, endOffset, bytes, valid);
        }

        ulong regionAddress = baseAddress + startOffset;
        if (reader.TryReadInto(regionAddress, bytes, length))
        {
            Array.Fill(valid, true);
            return new RegionCapture(path, baseAddress, startOffset, endOffset, bytes, valid);
        }

        byte[] chunk = new byte[Math.Min(ReadChunkSize, length)];
        for (int chunkStart = 0; chunkStart < length; chunkStart += ReadChunkSize)
        {
            int chunkLength = Math.Min(ReadChunkSize, length - chunkStart);
            if (regionAddress > ulong.MaxValue - checked((ulong)chunkStart))
            {
                break;
            }

            ulong address = regionAddress + checked((ulong)chunkStart);
            if (!reader.TryReadInto(address, chunk, chunkLength))
            {
                continue;
            }

            Buffer.BlockCopy(chunk, 0, bytes, chunkStart, chunkLength);
            Array.Fill(valid, true, chunkStart, chunkLength);
        }

        return new RegionCapture(path, baseAddress, startOffset, endOffset, bytes, valid);
    }

    private SnapshotEnqueueResult EnqueueSnapshotObservations(
        FighterCapture capture,
        ScanObservationPhase phase,
        string action,
        DateTimeOffset now,
        long monotonicTicks,
        FighterCapture? previous)
    {
        int changed = 0;
        int stable = 0;
        int emitted = 0;
        foreach (RegionCapture region in capture.Regions)
        {
            RegionCapture? previousRegion = previous?.FindRegion(region.Path);
            foreach (DecodedValue current in EnumerateValues(region))
            {
                if (!current.Plausible)
                {
                    continue;
                }

                DecodedValue? previousValue = previousRegion is null
                    ? null
                    : TryDecode(previousRegion, current.ObjectOffset, current.ValueType);
                bool isChanged = previousValue.HasValue && !ValuesEquivalent(previousValue.Value, current);
                if (isChanged)
                {
                    changed++;
                }
                else if (previousValue.HasValue)
                {
                    stable++;
                }
                Enqueue(ToObservation(capture, region, current, previousValue, phase, action, now, monotonicTicks));
                emitted++;
            }
        }

        return new SnapshotEnqueueResult(emitted, changed, stable);
    }

    private (int Changed, int Stable) EnqueueComparisonObservations(
        FighterCapture previous,
        FighterCapture current,
        ScanObservationPhase phase,
        string action,
        DateTimeOffset now,
        long monotonicTicks,
        bool includeStable)
    {
        int changed = 0;
        int stable = 0;
        foreach (RegionCapture currentRegion in current.Regions)
        {
            RegionCapture? previousRegion = previous.FindRegion(currentRegion.Path);
            if (previousRegion is null)
            {
                continue;
            }

            foreach (DecodedValue currentValue in EnumerateValues(currentRegion))
            {
                if (!currentValue.Plausible)
                {
                    continue;
                }

                DecodedValue? previousValue = TryDecode(previousRegion, currentValue.ObjectOffset, currentValue.ValueType);
                if (!previousValue.HasValue)
                {
                    continue;
                }

                bool isChanged = !ValuesEquivalent(previousValue.Value, currentValue);
                if (isChanged)
                {
                    changed++;
                }
                else
                {
                    stable++;
                    if (!includeStable)
                    {
                        continue;
                    }
                }

                Enqueue(ToObservation(current, currentRegion, currentValue, previousValue, phase, action, now, monotonicTicks));
            }
        }
        return (changed, stable);
    }

    private IEnumerable<DecodedValue> EnumerateValues(RegionCapture region)
    {
        int stride = Math.Clamp(_configuration.Stride, 1, 8);
        for (uint offset = region.StartOffset; offset <= region.EndOffset; offset += checked((uint)stride))
        {
            foreach (ScannerValueType valueType in _configuration.ValueTypes)
            {
                DecodedValue? value = TryDecode(region, offset, valueType);
                if (value.HasValue)
                {
                    yield return value.Value;
                }
            }

            if (region.EndOffset - offset < stride)
            {
                break;
            }
        }
    }

    private static DecodedValue? TryDecode(RegionCapture region, uint offset, ScannerValueType valueType)
    {
        int size = SizeOf(valueType);
        if (!region.IsValid(offset, size))
        {
            return null;
        }

        int index = checked((int)(offset - region.StartOffset));
        ulong raw;
        double numeric;
        bool plausible = true;
        string classification;

        switch (valueType)
        {
            case ScannerValueType.Byte:
                raw = region.Bytes[index];
                numeric = raw;
                classification = "Byte state/flag candidate";
                break;
            case ScannerValueType.Int16:
                short int16 = BitConverter.ToInt16(region.Bytes, index);
                raw = unchecked((ushort)int16);
                numeric = int16;
                classification = "Signed 16-bit candidate";
                break;
            case ScannerValueType.UInt16:
                ushort uint16 = BitConverter.ToUInt16(region.Bytes, index);
                raw = uint16;
                numeric = uint16;
                classification = "Unsigned 16-bit candidate";
                break;
            case ScannerValueType.Int32:
                int int32 = BitConverter.ToInt32(region.Bytes, index);
                raw = unchecked((uint)int32);
                numeric = int32;
                classification = "Signed 32-bit candidate";
                break;
            case ScannerValueType.UInt32:
                uint uint32 = BitConverter.ToUInt32(region.Bytes, index);
                raw = uint32;
                numeric = uint32;
                classification = "Unsigned 32-bit candidate";
                break;
            case ScannerValueType.Float32:
                uint floatBits = BitConverter.ToUInt32(region.Bytes, index);
                float float32 = BitConverter.Int32BitsToSingle(unchecked((int)floatBits));
                raw = floatBits;
                numeric = float32;
                plausible = float.IsFinite(float32) && Math.Abs(float32) <= 1.0e12f &&
                    (float32 == 0 || Math.Abs(float32) >= 1.0e-20f);
                classification = plausible ? "Finite Float32 candidate" : "Float32 noise/non-finite";
                break;
            case ScannerValueType.Int64:
                long int64 = BitConverter.ToInt64(region.Bytes, index);
                raw = unchecked((ulong)int64);
                numeric = int64;
                classification = "Signed 64-bit candidate";
                break;
            case ScannerValueType.UInt64:
                raw = BitConverter.ToUInt64(region.Bytes, index);
                numeric = raw;
                classification = "Unsigned 64-bit candidate";
                break;
            case ScannerValueType.Float64:
                raw = BitConverter.ToUInt64(region.Bytes, index);
                double float64 = BitConverter.Int64BitsToDouble(unchecked((long)raw));
                numeric = float64;
                plausible = double.IsFinite(float64) && Math.Abs(float64) <= 1.0e18 &&
                    (float64 == 0 || Math.Abs(float64) >= 1.0e-200);
                classification = plausible ? "Finite Float64 candidate" : "Float64 noise/non-finite";
                break;
            case ScannerValueType.Pointer64:
                raw = BitConverter.ToUInt64(region.Bytes, index);
                numeric = raw;
                plausible = raw == 0 || IsCanonicalUserPointer(raw);
                classification = raw == 0
                    ? "Null pointer"
                    : plausible
                        ? "Canonical user-mode pointer candidate"
                        : "Non-canonical pointer noise";
                break;
            default:
                return null;
        }

        return new DecodedValue(offset, valueType, raw, numeric, plausible, classification);
    }

    private ScannerObservationMessage ToObservation(
        FighterCapture fighter,
        RegionCapture region,
        DecodedValue current,
        DecodedValue? previous,
        ScanObservationPhase phase,
        string action,
        DateTimeOffset now,
        long monotonicTicks)
    {
        bool changed = previous.HasValue && !ValuesEquivalent(previous.Value, current);
        double previousNumeric = previous?.NumericValue ?? current.NumericValue;
        return new ScannerObservationMessage(
            now,
            monotonicTicks,
            _experimentId ?? $"snapshot-{now:yyyyMMdd-HHmmss}",
            phase,
            action,
            fighter.Slot,
            fighter.ActorAddress,
            region.Path,
            region.BaseAddress,
            current.ObjectOffset,
            region.BaseAddress + current.ObjectOffset,
            current.ValueType,
            current.RawValue,
            previous?.RawValue ?? current.RawValue,
            current.NumericValue,
            previousNumeric,
            current.NumericValue - previousNumeric,
            changed,
            previous.HasValue && !changed,
            current.Plausible,
            KnownClassification(region.Path, current.ObjectOffset, current.Classification));
    }

    private void Enqueue(ScannerObservationMessage observation)
    {
        if (_pending.Count >= MaximumPendingObservations)
        {
            _pending.Dequeue();
            _droppedObservationCount++;
        }
        _pending.Enqueue(observation);
    }

    private void ClearBaseline(string detail)
    {
        _baselineBySlot.Clear();
        _lastBySlot.Clear();
        if (_pending.Count > 0)
        {
            _droppedObservationCount += _pending.Count;
            _pending.Clear();
        }
        _experimentId = null;
        _baselineLabel = null;
        _detail = detail;
        _lastCaptureUtc = null;
        _lastCaptureMonotonicTicks = null;
        _lastObservationCount = 0;
        _lastChangedCount = 0;
        _lastStableCount = 0;
        _baselineRegionCount = 0;
        _baselineValueCount = 0;
        _lastContinuousUtc = DateTimeOffset.MinValue;
    }

    private void InvalidateStaleBaseline(
        IReadOnlyList<FighterSnapshot> fighters,
        DateTimeOffset now,
        long monotonicTicks,
        List<TelemetryEventMessage> events)
    {
        if (_baselineBySlot.Count == 0)
        {
            return;
        }

        Dictionary<int, ulong> activeActors = fighters.ToDictionary(fighter => fighter.Slot, fighter => fighter.ActorAddress);
        bool stale = _baselineBySlot.Any(pair =>
            !activeActors.TryGetValue(pair.Key, out ulong actorAddress) || actorAddress != pair.Value.ActorAddress);
        if (!stale)
        {
            return;
        }

        ClearBaseline("Fighter identity changed or left battle; scanner baseline was cleared to prevent cross-object evidence contamination.");
        events.Add(ScannerEvent(TelemetryEventKind.ScannerWarning, _detail, now, monotonicTicks));
    }

    private bool CanCaptureComplete(int fighterCount, out long projectedObservations)
    {
        projectedObservations = EstimateObservationCount(fighterCount);
        return projectedObservations <= RuntimeProtocol.MaximumCompleteCaptureObservations &&
            _pending.Count + projectedObservations <= MaximumPendingObservations;
    }

    private long EstimateObservationCount(int fighterCount)
    {
        long rootOffsets = ((long)_configuration.EndOffset - _configuration.StartOffset) / _configuration.Stride + 1;
        long childOffsets = 0;
        if (_configuration.FollowPointers && _configuration.PointerDepth > 0)
        {
            long offsetsPerChild = ((long)_configuration.ChildScanSize - 1) / _configuration.Stride + 1;
            // MaximumChildObjects is a total traversal cap, not a per-depth cap.
            childOffsets = offsetsPerChild * _configuration.MaximumChildObjects;
        }

        return Math.Max(0, fighterCount) * (rootOffsets + childOffsets) * _configuration.ValueTypes.Count;
    }

    private int GetEffectiveContinuousIntervalMs()
    {
        long rootBytes = (long)_configuration.EndOffset - _configuration.StartOffset + 1;
        long childBytes = _configuration.FollowPointers
            ? (long)_configuration.ChildScanSize * _configuration.MaximumChildObjects * Math.Max(1, _configuration.PointerDepth)
            : 0;
        long totalBytes = Math.Max(1, rootBytes + childBytes) * _configuration.MaximumFighters;
        int safetyFloor = totalBytes switch
        {
            <= 0x4000 => 150,
            <= 0x10000 => 250,
            <= 0x40000 => 500,
            _ => 1000
        };
        return Math.Max(_configuration.ContinuousIntervalMs, safetyFloor);
    }

    private static bool ValuesEquivalent(DecodedValue previous, DecodedValue current)
    {
        if (previous.ValueType != current.ValueType)
        {
            return false;
        }

        if (current.ValueType is ScannerValueType.Float32 or ScannerValueType.Float64)
        {
            return TelemetryComparisonPolicy.NumericEquivalent(previous.NumericValue, current.NumericValue);
        }

        return previous.RawValue == current.RawValue;
    }

    private static bool IsCanonicalUserPointer(ulong value) =>
        value >= 0x10000 && value <= 0x00007FFFFFFFFFFF;

    private static ScannerConfiguration Normalize(ScannerConfiguration configuration)
    {
        uint start = Math.Min(configuration.StartOffset, RuntimeProtocol.MaximumScanEndOffset);
        uint end = Math.Clamp(configuration.EndOffset, start, RuntimeProtocol.MaximumScanEndOffset);
        int stride = configuration.Stride is 1 or 2 or 4 or 8 ? configuration.Stride : 4;
        ScannerValueType[] values = (configuration.ValueTypes ?? [])
            .Distinct()
            .ToArray();
        if (values.Length == 0)
        {
            values = [ScannerValueType.Float32, ScannerValueType.Int32, ScannerValueType.UInt32];
        }

        return new ScannerConfiguration(
            start,
            end,
            stride,
            values,
            Math.Clamp(configuration.MaximumFighters, 1, RuntimeProtocol.ObservedFighterSlotCount),
            configuration.ContinuousTracking,
            Math.Clamp(configuration.ContinuousIntervalMs, 100, 5000),
            Math.Clamp(configuration.MaximumObservationsPerFrame, 50, RuntimeProtocol.MaximumObservationBatch),
            configuration.FollowPointers,
            Math.Clamp(configuration.PointerDepth, 0, RuntimeProtocol.MaximumPointerDepth),
            Math.Clamp(configuration.ChildScanSize, 0x40u, RuntimeProtocol.MaximumChildScanSize),
            Math.Clamp(configuration.MaximumChildObjects, 0, RuntimeProtocol.MaximumChildObjects));
    }

    private static int SizeOf(ScannerValueType type) => type switch
    {
        ScannerValueType.Byte => 1,
        ScannerValueType.Int16 or ScannerValueType.UInt16 => 2,
        ScannerValueType.Float32 or ScannerValueType.Int32 or ScannerValueType.UInt32 => 4,
        ScannerValueType.Float64 or ScannerValueType.Int64 or ScannerValueType.UInt64 or ScannerValueType.Pointer64 => 8,
        _ => 4
    };

    private static uint AlignUp(uint value, uint alignment) =>
        (value + alignment - 1) / alignment * alignment;

    private static string KnownClassification(string regionPath, uint offset, string fallback)
    {
        if (string.Equals(regionPath, "Battle_Mob", StringComparison.Ordinal) && offset == RuntimeProtocol.CurrentHealthOffset)
        {
            return "Known reference: current health";
        }
        if (string.Equals(regionPath, "Battle_Mob", StringComparison.Ordinal) && offset == RuntimeProtocol.MaximumHealthOffset)
        {
            return "Known reference: maximum health";
        }
        return fallback;
    }

    private static TelemetryEventMessage ScannerEvent(
        TelemetryEventKind kind,
        string label,
        DateTimeOffset timestamp,
        long monotonicTicks) =>
        new(timestamp, monotonicTicks, kind, -1, 0, 0, 0, 0, 0, label, null);

    private static string SanitizeLabel(string? value, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        string cleaned = new(source.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        cleaned = string.Join('-', cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned[..Math.Min(cleaned.Length, 48)];
    }

    private sealed record FighterCapture(int Slot, ulong ActorAddress, IReadOnlyList<RegionCapture> Regions)
    {
        internal RegionCapture? FindRegion(string path) =>
            Regions.FirstOrDefault(region => string.Equals(region.Path, path, StringComparison.Ordinal));
    }

    private sealed record RegionCapture(
        string Path,
        ulong BaseAddress,
        uint StartOffset,
        uint EndOffset,
        byte[] Bytes,
        bool[] Valid)
    {
        internal bool IsValid(uint offset, int size)
        {
            if (offset < StartOffset || offset > EndOffset || size <= 0)
            {
                return false;
            }
            int index = checked((int)(offset - StartOffset));
            if (index + size > Valid.Length)
            {
                return false;
            }
            for (int i = 0; i < size; i++)
            {
                if (!Valid[index + i])
                {
                    return false;
                }
            }
            return true;
        }

        internal bool TryReadUInt64(uint offset, out ulong value)
        {
            if (!IsValid(offset, sizeof(ulong)))
            {
                value = 0;
                return false;
            }
            value = BitConverter.ToUInt64(Bytes, checked((int)(offset - StartOffset)));
            return true;
        }
    }

    private readonly record struct SnapshotEnqueueResult(int Emitted, int Changed, int Stable);

    private readonly record struct DecodedValue(
        uint ObjectOffset,
        ScannerValueType ValueType,
        ulong RawValue,
        double NumericValue,
        bool Plausible,
        string Classification);
}

internal sealed record ScannerFrame(
    ScannerStatusMessage Status,
    IReadOnlyList<ScannerObservationMessage> Observations);
