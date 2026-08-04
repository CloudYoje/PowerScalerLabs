using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace PowerScalerLabs.Protocol;

public static class RuntimeProtocol
{
    public const string PipeName = "PowerScalerLabs.Runtime.CapabilityScannerGate";
    public const int ProtocolVersion = 8;
    public const int ObservedFighterSlotCount = 14;
    public const uint CurrentHealthOffset = 0x100;
    public const uint MaximumHealthOffset = 0x104;
    public const uint CurrentKiOffset = 0x10C;
    public const uint MaximumKiOffset = 0x110;
    public const uint CurrentStaminaOffset = 0x16C;
    public const uint MaximumStaminaOffset = 0x170;
    public const uint DefaultScanStartOffset = 0x000;
    public const uint DefaultScanEndOffset = 0x1000;
    public const uint MaximumScanEndOffset = 0x8000;
    public const int MaximumCompleteCaptureObservations = 200_000;
    public const int MaximumContinuousObservations = 50_000;
    public const int MaximumObservationBatch = 2_000;
    public const int MaximumPointerDepth = 2;
    public const uint MaximumChildScanSize = 0x1000;
    public const int MaximumChildObjects = 16;
    public const int MaximumChronologyTargets = 64;
    public const int MaximumChronologyBatch = 4_096;
    public const int MaximumRawObservationBatch = 2_000;
    public static long MonotonicFrequency => Stopwatch.Frequency;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuntimeState
{
    Starting,
    WaitingForApp,
    WaitingForGame,
    GameDetected,
    ReadPermissionGranted,
    WaitingForPatcher,
    WaitingForBattleCore,
    WaitingForFighters,
    ObservingFighters,
    ScanningCapabilities,
    ReadPermissionDenied,
    Error,
    Stopping
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TelemetryEventKind
{
    FighterAcquired,
    FighterReleased,
    ValueObserved,
    ValueChanged,
    Snapshot,
    ScannerConfigured,
    ScannerBaselineCaptured,
    ScannerComparisonCompleted,
    ScannerSnapshotCaptured,
    ScannerBaselineCleared,
    ScannerWarning
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScannerValueType
{
    Float32,
    Int32,
    UInt32,
    Float64,
    Int64,
    UInt64,
    Int16,
    UInt16,
    Byte,
    Pointer64
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanObservationPhase
{
    Baseline,
    Comparison,
    Snapshot,
    ContinuousDelta
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocatorOutcome
{
    Unavailable,
    Unsupported,
    NoCandidate,
    Candidate,
    Resolved,
    Conflict,
    Error
}

public sealed record ScannerConfiguration(
    uint StartOffset,
    uint EndOffset,
    int Stride,
    IReadOnlyList<ScannerValueType> ValueTypes,
    int MaximumFighters,
    bool ContinuousTracking,
    int ContinuousIntervalMs,
    int MaximumObservationsPerFrame,
    bool FollowPointers,
    int PointerDepth,
    uint ChildScanSize,
    int MaximumChildObjects)
{
    public static ScannerConfiguration Default { get; } = new(
        RuntimeProtocol.DefaultScanStartOffset,
        RuntimeProtocol.DefaultScanEndOffset,
        4,
        [ScannerValueType.Float32, ScannerValueType.Int32, ScannerValueType.UInt32, ScannerValueType.Pointer64],
        2,
        true,
        250,
        400,
        true,
        1,
        0x200,
        4);
}

public sealed record ChronologyWatchTarget(
    string RegionPath,
    uint ObjectOffset,
    ScannerValueType ValueType,
    string Label,
    string ValidationStage);

public sealed record ChronologyConfiguration(
    bool Enabled,
    int IntervalMs,
    int MaximumFighters,
    IReadOnlyList<ChronologyWatchTarget> Targets)
{
    public static ChronologyConfiguration Default { get; } = new(
        true,
        25,
        2,
        [
            new("Battle_Mob", RuntimeProtocol.CurrentHealthOffset, ScannerValueType.Float32, "Current health", "Verified"),
            new("Battle_Mob", RuntimeProtocol.MaximumHealthOffset, ScannerValueType.Float32, "Maximum health", "Verified"),
            new("Battle_Mob", RuntimeProtocol.CurrentKiOffset, ScannerValueType.Float32, "Current Ki source-backed candidate", "SourceBacked"),
            new("Battle_Mob", RuntimeProtocol.MaximumKiOffset, ScannerValueType.Float32, "Maximum Ki candidate", "Correlated"),
            new("Battle_Mob", RuntimeProtocol.CurrentStaminaOffset, ScannerValueType.Float32, "Current stamina source-backed candidate", "SourceBacked"),
            new("Battle_Mob", RuntimeProtocol.MaximumStaminaOffset, ScannerValueType.Float32, "Maximum stamina candidate", "Correlated")
        ]);
}

public sealed record FighterIdentityMessage(
    string IdentityKey,
    string ProcessInstanceId,
    long BattleInstanceId,
    long SlotGeneration,
    int Slot,
    ulong ActorAddress,
    ulong VtableAddress,
    DateTimeOffset FirstSeenUtc,
    long FirstSeenMonotonicTicks);

public sealed record FighterSnapshot(
    int Slot,
    ulong ActorAddress,
    float CurrentHealth,
    float MaximumHealth,
    DateTimeOffset TimestampUtc,
    long MonotonicTicks,
    FighterIdentityMessage Identity);

public sealed record TelemetryEventMessage(
    DateTimeOffset TimestampUtc,
    long MonotonicTicks,
    TelemetryEventKind Kind,
    int FighterSlot,
    ulong ActorAddress,
    ulong ObservedAddress,
    uint ObjectOffset,
    double PreviousValue,
    double CurrentValue,
    string Label,
    string? FighterIdentityKey);

public sealed record RawMemoryObservationMessage(
    long Sequence,
    DateTimeOffset TimestampUtc,
    long MonotonicTicks,
    string ProvenanceKey,
    string RegionPath,
    int FighterSlot,
    string? FighterIdentityKey,
    ulong BaseAddress,
    uint ObjectOffset,
    ulong ObservedAddress,
    ScannerValueType ValueType,
    int ByteCount,
    ulong RawValue,
    double NumericValue,
    bool ReadSucceeded);

public sealed record ScannerObservationMessage(
    DateTimeOffset TimestampUtc,
    long MonotonicTicks,
    string ExperimentId,
    ScanObservationPhase Phase,
    string ActionLabel,
    int FighterSlot,
    ulong ActorAddress,
    string RegionPath,
    ulong RegionBaseAddress,
    uint ObjectOffset,
    ulong ObservedAddress,
    ScannerValueType ValueType,
    ulong RawValue,
    ulong PreviousRawValue,
    double NumericValue,
    double PreviousNumericValue,
    double Delta,
    bool Changed,
    bool Stable,
    bool Plausible,
    string Classification);

public sealed record ChronologySampleMessage(
    long Sequence,
    long CaptureId,
    long Epoch,
    DateTimeOffset TimestampUtc,
    long MonotonicTicks,
    long PollStartedMonotonicTicks,
    long PollCompletedMonotonicTicks,
    int FighterSlot,
    ulong ActorAddress,
    string FighterIdentityKey,
    long FighterSlotGeneration,
    string RegionPath,
    uint ObjectOffset,
    ulong ObservedAddress,
    ScannerValueType ValueType,
    ulong RawValue,
    ulong PreviousRawValue,
    double NumericValue,
    double PreviousNumericValue,
    double Delta,
    bool Changed,
    bool Initial,
    string Label,
    string ValidationStage);

public sealed record ScannerStatusMessage(
    bool Configured,
    bool HasBaseline,
    string? ExperimentId,
    string? BaselineLabel,
    string Detail,
    ScannerConfiguration Configuration,
    int BaselineFighterCount,
    int BaselineRegionCount,
    int BaselineValueCount,
    int LastObservationCount,
    int LastChangedCount,
    int LastStableCount,
    int PendingObservationCount,
    int DroppedObservationCount,
    DateTimeOffset? LastCaptureUtc,
    long? LastCaptureMonotonicTicks);

public sealed record ChronologyStatusMessage(
    bool Configured,
    bool Enabled,
    bool SamplingActive,
    string Detail,
    ChronologyConfiguration Configuration,
    int ActiveFighterCount,
    int WatchedTargetCount,
    long Epoch,
    long EpochEmittedSampleCount,
    long EpochInitialSampleCount,
    long EpochChangedSampleCount,
    long EpochPollCount,
    long EpochReadCount,
    long EpochUnreadableReadCount,
    long EpochDroppedSampleCount,
    long EpochPollOverrunCount,
    double EpochMaximumPollDurationMilliseconds,
    long PollCount,
    long ReadCount,
    long EmittedSampleCount,
    long ChangedSampleCount,
    long UnreadableReadCount,
    int PendingSampleCount,
    long DroppedSampleCount,
    long InvalidatedSampleCount,
    long PollOverrunCount,
    double LastPollDurationMilliseconds,
    double MaximumPollDurationMilliseconds,
    DateTimeOffset? LastSampleUtc,
    long? LastSampleMonotonicTicks);

public sealed record AddressProvenanceEntry(
    string Key,
    string Owner,
    string ModuleName,
    string RegionPath,
    ulong OffsetOrRva,
    string AddressKind,
    ScannerValueType ValueType,
    int ByteCount,
    string Meaning,
    string Source,
    string CompatibilityPolicy,
    string ProviderId,
    string ValidationStage,
    string ReadCadence,
    string FailureBehavior);

public sealed record BattleCoreLocatorReport(
    string ProviderId,
    string DisplayName,
    LocatorOutcome Outcome,
    string Detail,
    string? RequiredModule,
    uint RequiredImageSize,
    uint ObservedImageSize,
    ulong? CandidateAddress,
    int CandidateScore,
    IReadOnlyList<string> EvidenceKeys);

public sealed record MemoryAccessMetricsMessage(
    string Lane,
    long ReadRequests,
    long ReadProcessMemoryCalls,
    long RequestedBytes,
    long CompletedBytes,
    long FailedReadCalls,
    long RejectedReadRequests,
    long VirtualQueryCalls,
    long FailedVirtualQueryCalls,
    long ModuleRefreshCount);

public sealed record ComparisonPolicyMessage(
    string PolicyId,
    double AbsoluteTolerance,
    double RelativeTolerance,
    string RawChronologyPolicy,
    string Purpose);

public sealed record RuntimeAccessStatusMessage(
    string ArchitectureGate,
    bool ExternalReadOnly,
    bool InjectionUsed,
    bool HooksUsed,
    bool GameWritesUsed,
    string? ActiveLocatorId,
    string LocatorDetail,
    IReadOnlyList<BattleCoreLocatorReport> LocatorReports,
    IReadOnlyList<AddressProvenanceEntry> AddressProvenance,
    MemoryAccessMetricsMessage ObserverMetrics,
    MemoryAccessMetricsMessage ChronologyMetrics,
    ComparisonPolicyMessage ComparisonPolicy);

public sealed record RuntimeStatusMessage(
    int ProtocolVersion,
    DateTimeOffset TimestampUtc,
    long MonotonicTicks,
    long MonotonicFrequency,
    int RuntimeProcessId,
    RuntimeState State,
    string Detail,
    int? GameProcessId,
    bool CanQueryGame,
    bool CanReadGame,
    long HeartbeatSequence,
    bool HealthScalerBoundaryPreserved,
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
    ChronologyStatusMessage Chronology,
    IReadOnlyList<ChronologySampleMessage> ChronologySamples,
    RuntimeAccessStatusMessage RuntimeAccess);

public sealed record RuntimeCommand(
    string Command,
    string? Label = null,
    ScannerConfiguration? ScannerConfiguration = null,
    ChronologyConfiguration? ChronologyConfiguration = null);
