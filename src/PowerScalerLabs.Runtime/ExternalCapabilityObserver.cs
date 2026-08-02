using System.Diagnostics;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal sealed class ExternalCapabilityObserver : IDisposable
{
    private const int RequiredStableCoreSamples = 4;
    private const int ActiveLocatorRefreshHeartbeats = 10;

    private readonly SlotState[] _slots = Enumerable.Range(0, RuntimeProtocol.ObservedFighterSlotCount)
        .Select(_ => new SlotState())
        .ToArray();
    private readonly ObjectCapabilityScanner _scanner = new();
    private readonly BattleCoreLocatorCoordinator _locatorCoordinator = new();

    private GameMemoryReader? _reader;
    private int? _processId;
    private string _processInstanceId = string.Empty;
    private long _battleInstanceId;
    private ulong _candidateCore;
    private ulong _activeCore;
    private int _stableCoreSamples;
    private int _moduleRefreshCountdown;
    private int _locatorRefreshCountdown;
    private BattleCoreResolution? _cachedResolution;
    private long _rawObservationSequence;
    private string? _activeLocatorId;
    private string _locatorDetail = "No BattleCore provider has run.";
    private IReadOnlyList<BattleCoreLocatorReport> _locatorReports = [];

    internal ObserverFrame Observe(
        int processId,
        IReadOnlyList<RuntimeCommand> commands,
        DateTimeOffset now,
        long monotonicTicks)
    {
        List<TelemetryEventMessage> events = [];
        List<RawMemoryObservationMessage> rawObservations = [];

        try
        {
            EnsureReader(processId, events, now, monotonicTicks);
            if (_reader is null)
            {
                return ErrorFrame("The read-only game reader could not be created.", events, rawObservations);
            }

            if (_moduleRefreshCountdown-- <= 0)
            {
                _reader.RefreshModules();
                _moduleRefreshCountdown = 10;
            }

            BattleCoreResolution resolution = ResolveBattleCore(_reader);
            _locatorReports = resolution.Reports;
            _locatorDetail = resolution.Detail;
            BattleCoreLocatorReport? patcherReport = resolution.Reports.FirstOrDefault(report =>
                string.Equals(report.ProviderId, AddressProvenanceCatalog.PatcherProviderId, StringComparison.Ordinal));
            bool patcherDetected = patcherReport is not null && patcherReport.Outcome != LocatorOutcome.Unavailable;
            uint patcherImageSize = patcherReport?.ObservedImageSize ?? 0;

            if (resolution.SelectedCore == 0 || resolution.SelectedScore < 0)
            {
                ResetCore(events, now, monotonicTicks);
                _activeLocatorId = null;
                ScannerFrame battleCoreScannerFrame = _scanner.Process(_reader, [], commands, now, monotonicTicks, events);
                RuntimeState waitingState = patcherReport?.Outcome == LocatorOutcome.Unavailable
                    ? RuntimeState.WaitingForPatcher
                    : RuntimeState.WaitingForBattleCore;
                return WaitingFrame(
                    waitingState,
                    resolution.Detail,
                    patcherDetected,
                    patcherImageSize,
                    events,
                    rawObservations,
                    battleCoreScannerFrame);
            }

            if (_candidateCore != resolution.SelectedCore)
            {
                _candidateCore = resolution.SelectedCore;
                _stableCoreSamples = 1;
            }
            else if (_stableCoreSamples < RequiredStableCoreSamples)
            {
                _stableCoreSamples++;
            }

            if (_stableCoreSamples < RequiredStableCoreSamples)
            {
                if (_activeCore != 0 && _activeCore != _candidateCore)
                {
                    ReleaseAll(events, now, monotonicTicks);
                    _scanner.ResetForDetach();
                    _activeCore = 0;
                    _activeLocatorId = null;
                }

                ScannerFrame stabilizingScannerFrame = _scanner.Process(_reader, [], commands, now, monotonicTicks, events);
                return CreateFrame(
                    RuntimeState.WaitingForBattleCore,
                    $"BattleCore candidate 0x{_candidateCore:X16} from {resolution.ProviderId} is stabilizing ({_stableCoreSamples}/{RequiredStableCoreSamples}).",
                    patcherDetected,
                    patcherImageSize,
                    null,
                    [],
                    events,
                    rawObservations,
                    stabilizingScannerFrame);
            }

            if (_activeCore != resolution.SelectedCore)
            {
                ReleaseAll(events, now, monotonicTicks);
                _scanner.ResetForDetach();
                _activeCore = resolution.SelectedCore;
                _activeLocatorId = resolution.ProviderId;
                _battleInstanceId++;
            }

            List<FighterSnapshot> fighters = ObserveFighters(events, rawObservations, now, monotonicTicks);
            ScannerFrame activeScannerFrame = _scanner.Process(_reader, fighters, commands, now, monotonicTicks, events);

            RuntimeState observerState;
            string detail;
            if (fighters.Count == 0)
            {
                observerState = RuntimeState.WaitingForFighters;
                detail = $"BattleCore 0x{_activeCore:X16} is stable through {_activeLocatorId}. Waiting for fighter objects.";
            }
            else if (activeScannerFrame.Status.HasBaseline || activeScannerFrame.Observations.Count > 0)
            {
                observerState = RuntimeState.ScanningCapabilities;
                detail = $"Capability scanner active on {fighters.Count} fighter object(s). {activeScannerFrame.Status.Detail}";
            }
            else
            {
                observerState = RuntimeState.ObservingFighters;
                detail = $"Observing {fighters.Count} validated fighter generation(s) through {_activeLocatorId}.";
            }

            return CreateFrame(
                observerState,
                detail,
                patcherDetected,
                patcherImageSize,
                _activeCore,
                fighters,
                events,
                rawObservations,
                activeScannerFrame);
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"External capability observer error: {exception}");
            ResetCore(events, now, monotonicTicks);
            return ErrorFrame(exception.Message, events, rawObservations);
        }
    }

    internal IReadOnlyList<TelemetryEventMessage> Detach()
    {
        List<TelemetryEventMessage> events = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long monotonicTicks = Stopwatch.GetTimestamp();
        ReleaseAll(events, now, monotonicTicks);
        _scanner.ResetForDetach();
        _reader?.Dispose();
        _reader = null;
        _processId = null;
        _processInstanceId = string.Empty;
        _candidateCore = 0;
        _activeCore = 0;
        _stableCoreSamples = 0;
        _activeLocatorId = null;
        _locatorRefreshCountdown = 0;
        _cachedResolution = null;
        _locatorDetail = "Detached from DBXV2.exe.";
        _locatorReports = [];
        return events;
    }

    public void Dispose() => Detach();

    private List<FighterSnapshot> ObserveFighters(
        List<TelemetryEventMessage> events,
        List<RawMemoryObservationMessage> rawObservations,
        DateTimeOffset now,
        long monotonicTicks)
    {
        List<FighterSnapshot> fighters = [];
        if (_reader is null)
        {
            return fighters;
        }

        for (int slot = 0; slot < RuntimeProtocol.ObservedFighterSlotCount; slot++)
        {
            ulong slotAddress = _activeCore + ValidatedRuntimeLayout.BattleCoreMobArrayOffset + checked((ulong)(slot * sizeof(ulong)));
            SlotState state = _slots[slot];

            bool slotRead = _reader.TryReadUInt64(slotAddress, out ulong mob);
            string? pointerIdentityKey = slotRead && state.Valid && mob == state.ActorAddress
                ? state.Identity?.IdentityKey
                : null;
            rawObservations.Add(RawObservation(
                AddressProvenanceCatalog.BattleCoreMobArrayKey,
                "BattleCore",
                slot,
                pointerIdentityKey,
                _activeCore,
                checked((uint)(ValidatedRuntimeLayout.BattleCoreMobArrayOffset + (ulong)(slot * sizeof(ulong)))),
                slotAddress,
                ScannerValueType.Pointer64,
                sizeof(ulong),
                mob,
                mob,
                slotRead,
                now,
                monotonicTicks));

            if (!slotRead || mob == 0 ||
                !TryReadMobHealth(mob, out float currentHealth, out float maximumHealth, out ulong vtable))
            {
                ReleaseSlot(slot, events, now, monotonicTicks);
                continue;
            }

            if (!state.Valid || state.ActorAddress != mob || state.VtableAddress != vtable)
            {
                ReleaseSlot(slot, events, now, monotonicTicks);
                state.SlotGeneration++;
                state.ActorAddress = mob;
                state.VtableAddress = vtable;
                state.CurrentHealth = currentHealth;
                state.MaximumHealth = maximumHealth;
                state.Valid = true;
                state.Identity = CreateIdentity(slot, state.SlotGeneration, mob, vtable, now, monotonicTicks);
                events.Add(FighterEvent(TelemetryEventKind.FighterAcquired, state, now, monotonicTicks));
                events.Add(ValueEvent(TelemetryEventKind.ValueObserved, state,
                    RuntimeProtocol.CurrentHealthOffset, currentHealth, currentHealth, "Current health", now, monotonicTicks));
                events.Add(ValueEvent(TelemetryEventKind.ValueObserved, state,
                    RuntimeProtocol.MaximumHealthOffset, maximumHealth, maximumHealth, "Maximum health", now, monotonicTicks));
            }
            else
            {
                if (TelemetryComparisonPolicy.Changed(state.CurrentHealth, currentHealth))
                {
                    events.Add(ValueEvent(TelemetryEventKind.ValueChanged, state,
                        RuntimeProtocol.CurrentHealthOffset, state.CurrentHealth, currentHealth, "Current health", now, monotonicTicks));
                }

                if (TelemetryComparisonPolicy.Changed(state.MaximumHealth, maximumHealth))
                {
                    events.Add(ValueEvent(TelemetryEventKind.ValueChanged, state,
                        RuntimeProtocol.MaximumHealthOffset, state.MaximumHealth, maximumHealth, "Maximum health", now, monotonicTicks));
                }

                state.CurrentHealth = currentHealth;
                state.MaximumHealth = maximumHealth;
            }

            FighterIdentityMessage identity = state.Identity!;
            rawObservations.Add(RawFloatObservation(
                AddressProvenanceCatalog.CurrentHealthKey,
                slot,
                identity.IdentityKey,
                mob,
                RuntimeProtocol.CurrentHealthOffset,
                currentHealth,
                now,
                monotonicTicks));
            rawObservations.Add(RawFloatObservation(
                AddressProvenanceCatalog.MaximumHealthKey,
                slot,
                identity.IdentityKey,
                mob,
                RuntimeProtocol.MaximumHealthOffset,
                maximumHealth,
                now,
                monotonicTicks));

            fighters.Add(new FighterSnapshot(
                slot,
                mob,
                currentHealth,
                maximumHealth,
                now,
                monotonicTicks,
                identity));
        }

        return fighters;
    }

    private void EnsureReader(
        int processId,
        List<TelemetryEventMessage> events,
        DateTimeOffset now,
        long monotonicTicks)
    {
        if (_reader is not null && _processId == processId)
        {
            return;
        }

        ReleaseAll(events, now, monotonicTicks);
        _scanner.ResetForDetach();
        _reader?.Dispose();
        _reader = new GameMemoryReader(processId);
        _processId = processId;
        _processInstanceId = Guid.NewGuid().ToString("N");
        _battleInstanceId = 0;
        _candidateCore = 0;
        _activeCore = 0;
        _stableCoreSamples = 0;
        _moduleRefreshCountdown = 0;
        _locatorRefreshCountdown = 0;
        _cachedResolution = null;
        _activeLocatorId = null;
        _locatorReports = [];
        RuntimeLog.Write($"Attached read-only observer to DBXV2 PID {processId}; process-instance={_processInstanceId}; version={_reader.GameVersion ?? "unknown"}.");
    }

    private BattleCoreResolution ResolveBattleCore(GameMemoryReader reader)
    {
        BattleCoreResolution? resolution = _cachedResolution;
        bool candidateTransition = _candidateCore != 0 && _candidateCore != _activeCore;
        bool acquisitionInProgress = _activeCore == 0 || candidateTransition ||
            _stableCoreSamples < RequiredStableCoreSamples;
        bool mustResolve = acquisitionInProgress || resolution is null || _locatorRefreshCountdown-- <= 0;
        if (mustResolve)
        {
            resolution = _locatorCoordinator.Resolve(reader);
            _cachedResolution = resolution;
            _locatorRefreshCountdown = acquisitionInProgress ? 0 : ActiveLocatorRefreshHeartbeats;
        }

        return resolution ?? throw new InvalidOperationException("BattleCore locator returned no resolution object.");
    }

    private bool TryReadMobHealth(
        ulong mob,
        out float currentHealth,
        out float maximumHealth,
        out ulong vtable)
    {
        currentHealth = 0;
        maximumHealth = 0;
        vtable = 0;
        if (_reader is null || !_reader.IsPrivateWritableObject(mob) ||
            !_reader.TryReadUInt64(mob, out vtable) ||
            !_reader.IsGameImageAddress(vtable) ||
            !_reader.TryReadSingle(mob + RuntimeProtocol.CurrentHealthOffset, out currentHealth) ||
            !_reader.TryReadSingle(mob + RuntimeProtocol.MaximumHealthOffset, out maximumHealth))
        {
            return false;
        }

        if (!float.IsFinite(currentHealth) || !float.IsFinite(maximumHealth) ||
            maximumHealth < ValidatedRuntimeLayout.MinimumPlausibleMaximumHealth || currentHealth < -1.0f)
        {
            return false;
        }

        return currentHealth <= maximumHealth * 8.0f || currentHealth <= 1000.0f;
    }

    private FighterIdentityMessage CreateIdentity(
        int slot,
        long slotGeneration,
        ulong actorAddress,
        ulong vtableAddress,
        DateTimeOffset now,
        long monotonicTicks)
    {
        string key = $"{_processInstanceId}:battle-{_battleInstanceId}:slot-{slot}:generation-{slotGeneration}";
        return new FighterIdentityMessage(
            key,
            _processInstanceId,
            _battleInstanceId,
            slotGeneration,
            slot,
            actorAddress,
            vtableAddress,
            now,
            monotonicTicks);
    }

    private void ResetCore(
        List<TelemetryEventMessage> events,
        DateTimeOffset now,
        long monotonicTicks)
    {
        ReleaseAll(events, now, monotonicTicks);
        _scanner.ResetForDetach();
        _candidateCore = 0;
        _activeCore = 0;
        _stableCoreSamples = 0;
        _activeLocatorId = null;
        _locatorRefreshCountdown = 0;
        _cachedResolution = null;
    }

    private void ReleaseAll(
        List<TelemetryEventMessage> events,
        DateTimeOffset now,
        long monotonicTicks)
    {
        for (int slot = 0; slot < _slots.Length; slot++)
        {
            ReleaseSlot(slot, events, now, monotonicTicks);
        }
    }

    private void ReleaseSlot(
        int slot,
        List<TelemetryEventMessage> events,
        DateTimeOffset now,
        long monotonicTicks)
    {
        SlotState state = _slots[slot];
        if (!state.Valid)
        {
            return;
        }

        events.Add(FighterEvent(TelemetryEventKind.FighterReleased, state, now, monotonicTicks));
        state.ActorAddress = 0;
        state.VtableAddress = 0;
        state.CurrentHealth = 0;
        state.MaximumHealth = 0;
        state.Valid = false;
        state.Identity = null;
    }

    private ObserverFrame WaitingFrame(
        RuntimeState state,
        string detail,
        bool patcherDetected,
        uint patcherImageSize,
        IReadOnlyList<TelemetryEventMessage> events,
        IReadOnlyList<RawMemoryObservationMessage> rawObservations,
        ScannerFrame scannerFrame) =>
        CreateFrame(
            state,
            detail,
            patcherDetected,
            patcherImageSize,
            null,
            [],
            events,
            rawObservations,
            scannerFrame);

    private ObserverFrame ErrorFrame(
        string detail,
        IReadOnlyList<TelemetryEventMessage> events,
        IReadOnlyList<RawMemoryObservationMessage> rawObservations) =>
        new(
            RuntimeState.Error,
            $"Read-only capability observer error: {detail}",
            _reader?.GameVersion,
            false,
            0,
            null,
            0,
            [],
            events,
            rawObservations,
            _scanner.OfflineStatus(detail),
            [],
            _activeLocatorId,
            _locatorDetail,
            _locatorReports,
            _reader?.SnapshotMetrics("observer") ?? EmptyMetrics("observer"));

    private ObserverFrame CreateFrame(
        RuntimeState state,
        string detail,
        bool patcherDetected,
        uint patcherImageSize,
        ulong? battleCoreAddress,
        IReadOnlyList<FighterSnapshot> fighters,
        IReadOnlyList<TelemetryEventMessage> events,
        IReadOnlyList<RawMemoryObservationMessage> rawObservations,
        ScannerFrame scannerFrame) =>
        new(
            state,
            detail,
            _reader?.GameVersion,
            patcherDetected,
            patcherImageSize,
            battleCoreAddress,
            _stableCoreSamples,
            fighters,
            events,
            rawObservations.Take(RuntimeProtocol.MaximumRawObservationBatch).ToArray(),
            scannerFrame.Status,
            scannerFrame.Observations,
            _activeLocatorId,
            _locatorDetail,
            _locatorReports,
            _reader?.SnapshotMetrics("observer") ?? EmptyMetrics("observer"));

    private static TelemetryEventMessage FighterEvent(
        TelemetryEventKind kind,
        SlotState state,
        DateTimeOffset timestamp,
        long monotonicTicks) =>
        new(
            timestamp,
            monotonicTicks,
            kind,
            state.Identity?.Slot ?? -1,
            state.ActorAddress,
            0,
            0,
            0,
            0,
            kind == TelemetryEventKind.FighterAcquired ? "Fighter acquired" : "Fighter released",
            state.Identity?.IdentityKey);

    private static TelemetryEventMessage ValueEvent(
        TelemetryEventKind kind,
        SlotState state,
        uint offset,
        double previous,
        double current,
        string label,
        DateTimeOffset timestamp,
        long monotonicTicks) =>
        new(
            timestamp,
            monotonicTicks,
            kind,
            state.Identity?.Slot ?? -1,
            state.ActorAddress,
            state.ActorAddress + offset,
            offset,
            previous,
            current,
            label,
            state.Identity?.IdentityKey);

    private RawMemoryObservationMessage RawFloatObservation(
        string provenanceKey,
        int slot,
        string identityKey,
        ulong actorAddress,
        uint offset,
        float value,
        DateTimeOffset now,
        long monotonicTicks) =>
        RawObservation(
            provenanceKey,
            "Battle_Mob",
            slot,
            identityKey,
            actorAddress,
            offset,
            actorAddress + offset,
            ScannerValueType.Float32,
            sizeof(float),
            BitConverter.SingleToUInt32Bits(value),
            value,
            true,
            now,
            monotonicTicks);

    private RawMemoryObservationMessage RawObservation(
        string provenanceKey,
        string regionPath,
        int slot,
        string? identityKey,
        ulong baseAddress,
        uint offset,
        ulong observedAddress,
        ScannerValueType valueType,
        int byteCount,
        ulong rawValue,
        double numericValue,
        bool succeeded,
        DateTimeOffset now,
        long monotonicTicks) =>
        new(
            Interlocked.Increment(ref _rawObservationSequence),
            now,
            monotonicTicks,
            provenanceKey,
            regionPath,
            slot,
            identityKey,
            baseAddress,
            offset,
            observedAddress,
            valueType,
            byteCount,
            rawValue,
            numericValue,
            succeeded);

    private static MemoryAccessMetricsMessage EmptyMetrics(string lane) => new(lane, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class SlotState
    {
        internal ulong ActorAddress { get; set; }
        internal ulong VtableAddress { get; set; }
        internal float CurrentHealth { get; set; }
        internal float MaximumHealth { get; set; }
        internal long SlotGeneration { get; set; }
        internal bool Valid { get; set; }
        internal FighterIdentityMessage? Identity { get; set; }
    }
}

internal sealed record ObserverFrame(
    RuntimeState State,
    string Detail,
    string? GameVersion,
    bool PatcherDetected,
    uint PatcherImageSize,
    ulong? BattleCoreAddress,
    int StableCoreSamples,
    IReadOnlyList<FighterSnapshot> Fighters,
    IReadOnlyList<TelemetryEventMessage> Events,
    IReadOnlyList<RawMemoryObservationMessage> RawMemoryObservations,
    ScannerStatusMessage Scanner,
    IReadOnlyList<ScannerObservationMessage> ScanObservations,
    string? ActiveLocatorId,
    string LocatorDetail,
    IReadOnlyList<BattleCoreLocatorReport> LocatorReports,
    MemoryAccessMetricsMessage MemoryMetrics);
