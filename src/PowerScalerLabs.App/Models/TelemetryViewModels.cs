using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private long _battleInstanceId;
    private long _firstSeenMonotonicTicks;
    private string _identityKey = string.Empty;
    private DateTimeOffset _timestampUtc;

    public FighterRow(int slot) => Slot = slot;

    public int Slot { get; }
    public string ActorAddressText => ActorAddress == 0 ? "—" : $"0x{ActorAddress:X16}";
    public ulong ActorAddress { get => _actorAddress; private set => SetField(ref _actorAddress, value); }
    public float CurrentHealth { get => _currentHealth; private set => SetField(ref _currentHealth, value); }
    public float MaximumHealth { get => _maximumHealth; private set => SetField(ref _maximumHealth, value); }
    public long SlotGeneration { get => _slotGeneration; private set => SetField(ref _slotGeneration, value); }
    public long BattleInstanceId { get => _battleInstanceId; private set => SetField(ref _battleInstanceId, value); }
    public long FirstSeenMonotonicTicks { get => _firstSeenMonotonicTicks; private set => SetField(ref _firstSeenMonotonicTicks, value); }
    public string IdentityKey { get => _identityKey; private set => SetField(ref _identityKey, value); }
    public string IdentityShort => string.IsNullOrWhiteSpace(IdentityKey) ? "—" : IdentityKey.Split(':').LastOrDefault() ?? IdentityKey;
    public double HealthPercent => MaximumHealth > 0 ? CurrentHealth / MaximumHealth : 0;
    public string UpdatedText => TimestampUtc == default ? "—" : TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
    public DateTimeOffset TimestampUtc { get => _timestampUtc; private set => SetField(ref _timestampUtc, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(FighterSnapshot snapshot)
    {
        ActorAddress = snapshot.ActorAddress;
        CurrentHealth = snapshot.CurrentHealth;
        MaximumHealth = snapshot.MaximumHealth;
        SlotGeneration = snapshot.Identity.SlotGeneration;
        BattleInstanceId = snapshot.Identity.BattleInstanceId;
        FirstSeenMonotonicTicks = snapshot.Identity.FirstSeenMonotonicTicks;
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
                ? $"{telemetryEvent.PreviousValue:G8} → {telemetryEvent.CurrentValue:G8}"
                : $"{telemetryEvent.CurrentValue:G8}";
        return new SessionEventRow(
            telemetryEvent.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
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
    long Epoch,
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
            sample.Epoch,
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

public sealed record FindingRow(
    string Subject,
    string Location,
    string Stage,
    string Evidence,
    string Role);

public sealed record ProbeTraceEventRow(
    ulong Sequence, string Type, long Qpc, int NativeThread, ulong TraceSession, ulong WatchId,
    string TrapLocation, string RcxCorrelation, string RdxCorrelation);
