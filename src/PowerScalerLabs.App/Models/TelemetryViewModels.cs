using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.App.Models;


public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class FighterRow : INotifyPropertyChanged
{
    private ulong _actorAddress;
    private float _currentHealth;
    private float _maximumHealth;
    private long _slotGeneration;
    private string _identityKey = string.Empty;
    private DateTimeOffset _timestampUtc;

    public FighterRow(int slot) => Slot = slot;

    public int Slot { get; }
    public string ActorAddressText => $"0x{ActorAddress:X16}";
    public ulong ActorAddress { get => _actorAddress; private set => SetField(ref _actorAddress, value); }
    public float CurrentHealth { get => _currentHealth; private set => SetField(ref _currentHealth, value); }
    public float MaximumHealth { get => _maximumHealth; private set => SetField(ref _maximumHealth, value); }
    public long SlotGeneration { get => _slotGeneration; private set => SetField(ref _slotGeneration, value); }
    public string IdentityKey { get => _identityKey; private set => SetField(ref _identityKey, value); }
    public string IdentityShort => string.IsNullOrWhiteSpace(IdentityKey) ? "—" : IdentityKey.Split(':').LastOrDefault() ?? IdentityKey;
    public double HealthPercent => MaximumHealth > 0 ? CurrentHealth / MaximumHealth : 0;
    public string UpdatedText => TimestampUtc.ToLocalTime().ToString("T");
    public DateTimeOffset TimestampUtc { get => _timestampUtc; private set => SetField(ref _timestampUtc, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(FighterSnapshot snapshot)
    {
        ActorAddress = snapshot.ActorAddress;
        CurrentHealth = snapshot.CurrentHealth;
        MaximumHealth = snapshot.MaximumHealth;
        SlotGeneration = snapshot.Identity.SlotGeneration;
        IdentityKey = snapshot.Identity.IdentityKey;
        TimestampUtc = snapshot.TimestampUtc;
        OnPropertyChanged(nameof(ActorAddressText));
        OnPropertyChanged(nameof(HealthPercent));
        OnPropertyChanged(nameof(IdentityShort));
        OnPropertyChanged(nameof(UpdatedText));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record SessionEventRow(
    string Time,
    string Kind,
    int Slot,
    string Actor,
    string Offset,
    string Value,
    string Label)
{
    public static SessionEventRow FromTelemetry(TelemetryEventMessage telemetryEvent)
    {
        string offset = telemetryEvent.ObjectOffset == 0 ? "—" : $"+0x{telemetryEvent.ObjectOffset:X}";
        string value = telemetryEvent.ObjectOffset == 0
            ? "—"
            : telemetryEvent.Kind == TelemetryEventKind.ValueChanged
                ? $"{telemetryEvent.PreviousValue:N2} → {telemetryEvent.CurrentValue:N2}"
                : $"{telemetryEvent.CurrentValue:N2}";
        return new SessionEventRow(
            telemetryEvent.TimestampUtc.ToLocalTime().ToString("T"),
            telemetryEvent.Kind.ToString(),
            telemetryEvent.FighterSlot,
            telemetryEvent.ActorAddress == 0 ? "—" : $"0x{telemetryEvent.ActorAddress:X16}",
            offset,
            value,
            telemetryEvent.Label);
    }
}


public sealed record ChronologySampleRow(
    string Time,
    long Sequence,
    long Capture,
    int Slot,
    string Offset,
    string Type,
    string Previous,
    string Current,
    string Delta,
    string Validation,
    string Label)
{
    public static ChronologySampleRow FromSample(ChronologySampleMessage sample) =>
        new(
            sample.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
            sample.Sequence,
            sample.CaptureId,
            sample.FighterSlot,
            $"+0x{sample.ObjectOffset:X}",
            sample.ValueType.ToString(),
            FormatValue(sample.PreviousNumericValue),
            FormatValue(sample.NumericValue),
            FormatValue(sample.Delta),
            sample.ValidationStage,
            sample.Initial ? $"{sample.Label} (initial)" : sample.Label);

    private static string FormatValue(double value) =>
        double.IsFinite(value) ? value.ToString("G8") : value.ToString();
}

public sealed record ScannerObservationRow(
    string Time,
    string Phase,
    string Action,
    int Slot,
    string Region,
    string Offset,
    string Type,
    string Previous,
    string Current,
    string Delta,
    string Signal)
{
    public static ScannerObservationRow FromObservation(ScannerObservationMessage observation) =>
        new(
            observation.TimestampUtc.ToLocalTime().ToString("T"),
            observation.Phase.ToString(),
            observation.ActionLabel,
            observation.FighterSlot,
            observation.RegionPath,
            $"+0x{observation.ObjectOffset:X}",
            observation.ValueType.ToString(),
            FormatValue(observation.PreviousNumericValue),
            FormatValue(observation.NumericValue),
            FormatValue(observation.Delta),
            observation.Changed ? "Changed" : observation.Stable ? "Stable" : "Observed");

    private static string FormatValue(double value) =>
        double.IsFinite(value) ? value.ToString("G8") : value.ToString();
}

public static class CandidateTaxonomy
{
    public static IReadOnlyList<string> Families { get; } =
    [
        "Health",
        "Ki",
        "Stamina",
        "Basic Attack",
        "Strike Skills",
        "Ki Blast Skills",
        "Defense / Guard",
        "Transformation",
        "Movement",
        "State / Flags",
        "Timers / Cooldowns",
        "Object / Pointers",
        "Identity / Metadata",
        "Unknown"
    ];

    public static IReadOnlyList<string> Roles { get; } =
    [
        "Current Value",
        "Maximum / Capacity",
        "Cost / Consumption",
        "Regeneration / Recovery",
        "Damage / Output",
        "Multiplier / Scaling",
        "Resistance / Reduction",
        "State / Flag",
        "Timer / Cooldown",
        "Pointer / Reference",
        "Identity / Metadata",
        "Unclassified"
    ];
}


public static class CandidateValidationStages
{
    public const string Observed = "Observed";
    public const string Correlated = "Correlated";
    public const string CodeAnchored = "Code-anchored";
    public const string CausallyValidated = "Causally validated";
    public const string Verified = "Verified";

    public static IReadOnlyList<string> All { get; } =
    [
        Observed,
        Correlated,
        CodeAnchored,
        CausallyValidated,
        Verified
    ];
}

public static class CandidateSignalTiers
{
    public const string KnownEffect = "Known effect";
    public const string HighConfidence = "High-confidence";
    public const string Promising = "Promising";
    public const string NeedsAnotherTrial = "Needs another trial";
    public const string BackgroundNoise = "Background noise";

    public static IReadOnlyList<string> All { get; } =
    [
        KnownEffect,
        HighConfidence,
        Promising,
        NeedsAnotherTrial,
        BackgroundNoise
    ];
}

public sealed class CandidateRecord
{
    public string CandidateId { get; set; } = string.Empty;
    public string ObjectType { get; set; } = "Battle_Mob";
    public string RegionPath { get; set; } = "Battle_Mob";
    public uint ObjectOffset { get; set; }
    public string OffsetText => $"+0x{ObjectOffset:X}";
    public string Label { get; set; } = string.Empty;
    public string ValueType { get; set; } = ScannerValueType.Float32.ToString();
    public string ValueShape { get; set; } = string.Empty;
    public string Status { get; set; } = "Provisional";
    public double Confidence { get; set; }
    public string ConfidenceText => Confidence.ToString("P0");

    // Semantic organization is deliberately evidence-based. Automatic classification is a
    // hypothesis until the user confirms it through repeated controlled experiments.
    public string StatFamily { get; set; } = "Unknown";
    public string StatRole { get; set; } = "Unclassified";
    public string ClassificationSource { get; set; } = "Automatic";
    public double ClassificationConfidence { get; set; }
    public string ClassificationConfidenceText => ClassificationConfidence.ToString("P0");
    public bool ManuallyClassified { get; set; }
    public List<string> ClassificationTags { get; set; } = [];
    public List<ClassificationScoreRecord> ClassificationScores { get; set; } = [];
    public long EvidenceCount { get; set; }
    public long ChangeCount { get; set; }
    public long StableCount { get; set; }
    public long SnapshotCount { get; set; }
    public long BaselineCount { get; set; }
    public long ComparisonCount { get; set; }
    public long ContinuousChangeCount { get; set; }
    public long InvalidCount { get; set; }
    public long ValidValueCount { get; set; }
    public long DeltaSampleCount { get; set; }
    public double MinimumValue { get; set; }
    public double MaximumValue { get; set; }
    public double LastValue { get; set; }
    public double MinimumDelta { get; set; }
    public double MaximumDelta { get; set; }
    public double LastDelta { get; set; }
    public long IncreaseCount { get; set; }
    public long DecreaseCount { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public List<string> ActorAddresses { get; set; } = [];
    public List<string> SessionIds { get; set; } = [];
    public List<string> ExperimentIds { get; set; } = [];
    public List<ActionEvidenceRecord> ActionEvidence { get; set; } = [];
    public List<SlotEvidenceRecord> SlotEvidence { get; set; } = [];
    public bool ManuallyPromoted { get; set; }
    public bool ManuallyRejected { get; set; }
    public string ValidationStage { get; set; } = CandidateValidationStages.Observed;
    public long CodeAnchorCount { get; set; }
    public long CausalValidationCount { get; set; }
    public long ValidationFailureCount { get; set; }
    public List<string> ValidationEvidenceIds { get; set; } = [];
    public string Notes { get; set; } = string.Empty;

    [JsonIgnore]
    public int DistinctActorCount => ActorAddresses.Count;

    [JsonIgnore]
    public int SessionCount => SessionIds.Count;

    [JsonIgnore]
    public int ExperimentCount => ExperimentIds.Count;

    [JsonIgnore]
    public int DistinctSlotCount => SlotEvidence.Count;

    [JsonIgnore]
    public string SlotSummary => string.Join(", ", SlotEvidence
        .OrderBy(slot => slot.Slot)
        .Take(8)
        .Select(slot => $"S{slot.Slot}:{slot.ChangeCount:N0}/{slot.ObservationCount:N0}"));

    [JsonIgnore]
    public string TopActions => string.Join(", ", ActionEvidence
        .OrderByDescending(action => action.ChangeCount)
        .ThenByDescending(action => action.ObservationCount)
        .Take(3)
        .Select(action => action.ActionLabel));

    [JsonIgnore]
    public string ClassificationSummary => $"{StatFamily} · {StatRole}";

    [JsonIgnore]
    public string ClassificationTagsText => string.Join(", ", ClassificationTags);
}

public sealed class CandidateGroupRecord
{
    public string GroupId { get; set; } = string.Empty;
    public string RegionPath { get; set; } = "Battle_Mob";
    public uint ObjectOffset { get; set; }
    public string OffsetText => $"+0x{ObjectOffset:X}";
    public string PreferredCandidateId { get; set; } = string.Empty;
    public string PreferredValueType { get; set; } = string.Empty;
    public string AlternativeTypes { get; set; } = string.Empty;
    [JsonIgnore]
    public string AlternativeTypesText => string.IsNullOrWhiteSpace(AlternativeTypes) ? "None" : AlternativeTypes;
    public int AlternativeCount { get; set; }
    public string Label { get; set; } = string.Empty;
    public string StatFamily { get; set; } = "Unknown";
    public string StatRole { get; set; } = "Unclassified";
    public string Status { get; set; } = "Provisional";
    public string ValidationStage { get; set; } = CandidateValidationStages.Observed;
    public string SignalTier { get; set; } = CandidateSignalTiers.NeedsAnotherTrial;
    public bool IsKnownEffect { get; set; }
    public bool IsExplained { get; set; }
    public double Confidence { get; set; }
    public string ConfidenceText => Confidence.ToString("P0");
    public double ClassificationConfidence { get; set; }
    public string ClassificationConfidenceText => ClassificationConfidence.ToString("P0");
    public long EvidenceCount { get; set; }
    public long ChangeCount { get; set; }
    public long StableCount { get; set; }
    public int SessionCount { get; set; }
    public int ExperimentCount { get; set; }
    public int DistinctActorCount { get; set; }
    public int DistinctSlotCount { get; set; }
    public double LastValue { get; set; }
    public double MinimumValue { get; set; }
    public double MaximumValue { get; set; }
    public string ValueShape { get; set; } = string.Empty;
    public string ClassificationSource { get; set; } = string.Empty;
    public string ClassificationTagsText { get; set; } = string.Empty;
    public string TopActions { get; set; } = string.Empty;
    public string SlotSummary { get; set; } = string.Empty;
    public string PairRelationship { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class ClassificationScoreRecord
{
    public string StatFamily { get; set; } = "Unknown";
    public double Score { get; set; }

    [JsonIgnore]
    public string ScoreText => Score.ToString("N2");
}

public sealed class ActionEvidenceRecord
{
    public string ActionLabel { get; set; } = string.Empty;
    public long ObservationCount { get; set; }
    public long ChangeCount { get; set; }
    public long StableCount { get; set; }
    public long IncreaseCount { get; set; }
    public long DecreaseCount { get; set; }
    public double TotalAbsoluteDelta { get; set; }
    public double MaximumAbsoluteDelta { get; set; }
    public List<string> SessionIds { get; set; } = [];
    public List<string> ExperimentIds { get; set; } = [];
    public List<ActionSlotEvidenceRecord> SlotEvidence { get; set; } = [];

    [JsonIgnore]
    public double AverageAbsoluteDelta => ChangeCount > 0 ? TotalAbsoluteDelta / ChangeCount : 0;
}


public sealed class SlotEvidenceRecord
{
    public int Slot { get; set; }
    public long ObservationCount { get; set; }
    public long ChangeCount { get; set; }
    public long StableCount { get; set; }
    public long IncreaseCount { get; set; }
    public long DecreaseCount { get; set; }
    public List<string> ActorAddresses { get; set; } = [];
    public List<string> SessionIds { get; set; } = [];
}


public sealed class ActionSlotEvidenceRecord
{
    public int Slot { get; set; }
    public long ObservationCount { get; set; }
    public long ChangeCount { get; set; }
    public long StableCount { get; set; }
    public long IncreaseCount { get; set; }
    public long DecreaseCount { get; set; }
    public List<string> ActorAddresses { get; set; } = [];
    public List<string> SessionIds { get; set; } = [];
}
