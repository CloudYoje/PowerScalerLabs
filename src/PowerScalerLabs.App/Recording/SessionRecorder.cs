using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.App.Recording;

internal sealed class SessionRecorder : IDisposable
{
    private const int WriterBufferSize = 64 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MetadataInterval = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _sessionsRoot;
    private readonly HashSet<string> _experimentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _actionLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _candidateKeys = new(StringComparer.OrdinalIgnoreCase);
    private StreamWriter? _frameWriter;
    private StreamWriter? _eventWriter;
    private StreamWriter? _scannerWriter;
    private StreamWriter? _candidateKeyWriter;
    private StreamWriter? _timelineWriter;
    private StreamWriter? _chronologyWriter;
    private StreamWriter? _rawMemoryWriter;
    private SessionMetadata? _metadata;
    private RuntimeAccessStatusMessage? _latestRuntimeAccess;
    private string? _runtimeAccessSnapshotKey;
    private DateTimeOffset _lastFlushUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMetadataUtc = DateTimeOffset.MinValue;

    internal SessionRecorder(string dataRoot)
    {
        _sessionsRoot = Path.Combine(dataRoot, "Sessions");
        Directory.CreateDirectory(_sessionsRoot);
    }

    internal bool IsRecording => _metadata is not null;
    internal string? SessionId => _metadata?.SessionId;
    internal string? SessionFolder { get; private set; }
    internal long FrameCount => _metadata?.FrameCount ?? 0;
    internal long EventCount => _metadata?.EventCount ?? 0;
    internal long ScannerObservationCount => _metadata?.ScannerObservationCount ?? 0;
    internal long ChronologySampleCount => _metadata?.ChronologySampleCount ?? 0;
    internal long RawMemoryObservationCount => _metadata?.RawMemoryObservationCount ?? 0;
    internal string SessionsRoot => _sessionsRoot;

    internal string Start(string requestedName, RuntimeStatusMessage? latestStatus)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("A capability-scanner session is already recording.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string displayName = string.IsNullOrWhiteSpace(requestedName) ? "Capability Scan" : requestedName.Trim();
        string safeName = Sanitize(displayName);
        string baseSessionId = $"{now:yyyyMMdd-HHmmss}-{safeName}";
        string sessionId = baseSessionId;
        int suffix = 2;
        while (Directory.Exists(Path.Combine(_sessionsRoot, sessionId)))
        {
            sessionId = $"{baseSessionId}-{suffix:00}";
            suffix++;
        }
        SessionFolder = Path.Combine(_sessionsRoot, sessionId);
        Directory.CreateDirectory(SessionFolder);

        _experimentIds.Clear();
        _actionLabels.Clear();
        _candidateKeys.Clear();
        _latestRuntimeAccess = latestStatus?.RuntimeAccess;
        _runtimeAccessSnapshotKey = null;
        _metadata = new SessionMetadata
        {
            SchemaVersion = 7,
            SessionId = sessionId,
            Name = displayName,
            StartedUtc = now,
            GameProcessId = latestStatus?.GameProcessId,
            GameVersion = latestStatus?.GameVersion,
            RuntimeProcessId = latestStatus?.RuntimeProcessId,
            ProtocolVersion = RuntimeProtocol.ProtocolVersion,
            PatcherImageSize = latestStatus?.PatcherImageSize ?? 0,
            HealthScalerBoundaryPreserved = true,
            ReadOnlyExternalRuntime = true,
            ScannerConfiguration = latestStatus?.Scanner.Configuration ?? ScannerConfiguration.Default,
            ChronologyConfiguration = latestStatus?.Chronology.Configuration ?? ChronologyConfiguration.Default,
            ArchitectureGate = latestStatus?.RuntimeAccess.ArchitectureGate ?? "Runtime Access Architecture Gate 0",
            ComparisonPolicy = latestStatus?.RuntimeAccess.ComparisonPolicy ?? new ComparisonPolicyMessage(
                "compressed-scale-v1", 1.0e-6, 1.0e-6,
                "Chronology uses exact raw-bit equality.",
                "Recognize compressed combat-scale changes."),
            ChronologyInvalidatedAtStart = latestStatus?.Chronology.InvalidatedSampleCount ?? 0,
            MonotonicFrequency = latestStatus is { MonotonicFrequency: > 0 } status
                ? status.MonotonicFrequency
                : Stopwatch.Frequency,
            StartMonotonicTicks = latestStatus?.MonotonicTicks ?? Stopwatch.GetTimestamp()
        };

        try
        {
            _frameWriter = CreateWriter(Path.Combine(SessionFolder, "frames.jsonl"));
            _eventWriter = CreateWriter(Path.Combine(SessionFolder, "events.jsonl"));
            _scannerWriter = CreateWriter(Path.Combine(SessionFolder, "scanner-observations.jsonl"));
            _candidateKeyWriter = CreateWriter(Path.Combine(SessionFolder, "candidate-keys.jsonl"));
            _timelineWriter = CreateWriter(Path.Combine(SessionFolder, "timeline.jsonl"));
            _chronologyWriter = CreateWriter(Path.Combine(SessionFolder, "chronology-samples.jsonl"));
            _rawMemoryWriter = CreateWriter(Path.Combine(SessionFolder, "raw-memory-observations.jsonl"));
            WriteTimelineRecord("session-start", now, _metadata.StartMonotonicTicks, new
            {
                _metadata.SessionId,
                _metadata.Name,
                _metadata.GameProcessId,
                _metadata.GameVersion,
                _metadata.ProtocolVersion,
                _metadata.MonotonicFrequency,
                ChronologyIntervalMs = _metadata.ChronologyConfiguration.IntervalMs,
                ChronologyTargetCount = _metadata.ChronologyConfiguration.Targets.Count
            });
            _lastFlushUtc = now;
            _lastMetadataUtc = now;
            WriteChronologyWatchlist();
            WriteRuntimeAccessArchitecture(_latestRuntimeAccess);
            _runtimeAccessSnapshotKey = _latestRuntimeAccess is null
                ? null
                : CreateRuntimeAccessSnapshotKey(_latestRuntimeAccess);
            WriteMetadata();
            return sessionId;
        }
        catch
        {
            DisposeWriters();
            _metadata = null;
            _latestRuntimeAccess = null;
            _runtimeAccessSnapshotKey = null;
            SessionFolder = null;
            throw;
        }
    }

    internal void RecordFrame(RuntimeStatusMessage message)
    {
        if (_metadata is null || _frameWriter is null || _eventWriter is null || _scannerWriter is null ||
            _candidateKeyWriter is null || _timelineWriter is null || _chronologyWriter is null || _rawMemoryWriter is null)
        {
            return;
        }

        object compactFrame = new
        {
            message.ProtocolVersion,
            message.TimestampUtc,
            message.MonotonicTicks,
            message.MonotonicFrequency,
            RelativeMilliseconds = RelativeMilliseconds(message.MonotonicTicks),
            message.RuntimeProcessId,
            message.State,
            message.Detail,
            message.GameProcessId,
            message.CanQueryGame,
            message.CanReadGame,
            message.HeartbeatSequence,
            message.HealthScalerBoundaryPreserved,
            message.GameVersion,
            message.PatcherDetected,
            message.PatcherImageSize,
            message.BattleCoreAddress,
            message.StableCoreSamples,
            message.Fighters,
            EventCount = message.Events.Count,
            ScannerObservationCount = message.ScanObservations.Count,
            ChronologySampleCount = message.ChronologySamples.Count,
            RawMemoryObservationCount = message.RawMemoryObservations.Count,
            message.Scanner,
            message.Chronology,
            RuntimeAccess = new
            {
                message.RuntimeAccess.ArchitectureGate,
                message.RuntimeAccess.ExternalReadOnly,
                message.RuntimeAccess.InjectionUsed,
                message.RuntimeAccess.HooksUsed,
                message.RuntimeAccess.GameWritesUsed,
                message.RuntimeAccess.ActiveLocatorId,
                message.RuntimeAccess.LocatorDetail,
                message.RuntimeAccess.LocatorReports,
                AddressProvenanceCount = message.RuntimeAccess.AddressProvenance.Count,
                message.RuntimeAccess.ObserverMetrics,
                message.RuntimeAccess.ChronologyMetrics,
                ComparisonPolicyId = message.RuntimeAccess.ComparisonPolicy.PolicyId
            }
        };
        _frameWriter.WriteLine(JsonSerializer.Serialize(compactFrame, JsonOptions));
        WriteTimelineRecord("runtime-frame", message.TimestampUtc, message.MonotonicTicks, new
        {
            message.HeartbeatSequence,
            State = message.State.ToString(),
            message.GameProcessId,
            FighterCount = message.Fighters.Count,
            EventCount = message.Events.Count,
            ScannerObservationCount = message.ScanObservations.Count,
            RawMemoryObservationCount = message.RawMemoryObservations.Count
        });
        _metadata.FrameCount++;
        _metadata.LastMonotonicTicks = Math.Max(_metadata.LastMonotonicTicks, message.MonotonicTicks);
        _metadata.LastState = message.State.ToString();
        _metadata.LastHeartbeat = message.HeartbeatSequence;
        _metadata.LastBattleCoreAddress = message.BattleCoreAddress;
        _metadata.MaximumConcurrentFighters = Math.Max(_metadata.MaximumConcurrentFighters, message.Fighters.Count);
        _metadata.LastScannerDetail = message.Scanner.Detail;
        _metadata.ScannerConfiguration = message.Scanner.Configuration;
        _metadata.DroppedScannerObservationCount = message.Scanner.DroppedObservationCount;
        _latestRuntimeAccess = message.RuntimeAccess;
        _metadata.ActiveLocatorId = message.RuntimeAccess.ActiveLocatorId;
        _metadata.LastLocatorDetail = message.RuntimeAccess.LocatorDetail;
        _metadata.ObserverReadRequests = message.RuntimeAccess.ObserverMetrics.ReadRequests;
        _metadata.ObserverReadProcessMemoryCalls = message.RuntimeAccess.ObserverMetrics.ReadProcessMemoryCalls;
        _metadata.ObserverRequestedBytes = message.RuntimeAccess.ObserverMetrics.RequestedBytes;
        _metadata.ObserverCompletedBytes = message.RuntimeAccess.ObserverMetrics.CompletedBytes;
        _metadata.ObserverFailedReadCalls = message.RuntimeAccess.ObserverMetrics.FailedReadCalls;
        _metadata.ObserverRejectedReadRequests = message.RuntimeAccess.ObserverMetrics.RejectedReadRequests;
        _metadata.ObserverVirtualQueryCalls = message.RuntimeAccess.ObserverMetrics.VirtualQueryCalls;
        _metadata.ChronologyReadRequests = message.RuntimeAccess.ChronologyMetrics.ReadRequests;
        _metadata.ChronologyReadProcessMemoryCalls = message.RuntimeAccess.ChronologyMetrics.ReadProcessMemoryCalls;
        _metadata.ChronologyRequestedBytes = message.RuntimeAccess.ChronologyMetrics.RequestedBytes;
        _metadata.ChronologyCompletedBytes = message.RuntimeAccess.ChronologyMetrics.CompletedBytes;
        _metadata.ChronologyFailedReadCalls = message.RuntimeAccess.ChronologyMetrics.FailedReadCalls;
        _metadata.ChronologyRejectedReadRequests = message.RuntimeAccess.ChronologyMetrics.RejectedReadRequests;
        _metadata.ChronologyVirtualQueryCalls = message.RuntimeAccess.ChronologyMetrics.VirtualQueryCalls;
        string runtimeAccessSnapshotKey = CreateRuntimeAccessSnapshotKey(message.RuntimeAccess);
        if (!string.Equals(_runtimeAccessSnapshotKey, runtimeAccessSnapshotKey, StringComparison.Ordinal))
        {
            WriteRuntimeAccessArchitecture(message.RuntimeAccess);
            _runtimeAccessSnapshotKey = runtimeAccessSnapshotKey;
        }

        foreach (RawMemoryObservationMessage observation in message.RawMemoryObservations)
        {
            _rawMemoryWriter.WriteLine(JsonSerializer.Serialize(observation, JsonOptions));
            _metadata.RawMemoryObservationCount++;
            if (!observation.ReadSucceeded)
            {
                _metadata.RawMemoryReadFailureCount++;
            }
            _metadata.LastMonotonicTicks = Math.Max(_metadata.LastMonotonicTicks, observation.MonotonicTicks);
        }

        bool importantEvent = false;
        foreach (TelemetryEventMessage telemetryEvent in message.Events)
        {
            _eventWriter.WriteLine(JsonSerializer.Serialize(telemetryEvent, JsonOptions));
            WriteTimelineRecord("telemetry-event", telemetryEvent.TimestampUtc, telemetryEvent.MonotonicTicks, new
            {
                Kind = telemetryEvent.Kind.ToString(),
                telemetryEvent.FighterSlot,
                telemetryEvent.ActorAddress,
                telemetryEvent.ObjectOffset,
                telemetryEvent.PreviousValue,
                telemetryEvent.CurrentValue,
                telemetryEvent.Label,
                telemetryEvent.FighterIdentityKey
            });
            _metadata.EventCount++;
            _metadata.LastMonotonicTicks = Math.Max(_metadata.LastMonotonicTicks, telemetryEvent.MonotonicTicks);
            switch (telemetryEvent.Kind)
            {
                case TelemetryEventKind.FighterAcquired:
                    _metadata.FighterAcquireCount++;
                    importantEvent = true;
                    break;
                case TelemetryEventKind.FighterReleased:
                    _metadata.FighterReleaseCount++;
                    importantEvent = true;
                    break;
                case TelemetryEventKind.ValueChanged:
                    _metadata.ValueChangeCount++;
                    break;
                case TelemetryEventKind.Snapshot:
                    _metadata.SnapshotEventCount++;
                    break;
                case TelemetryEventKind.ScannerBaselineCaptured:
                    _metadata.BaselineCaptureCount++;
                    importantEvent = true;
                    break;
                case TelemetryEventKind.ScannerComparisonCompleted:
                    _metadata.ComparisonCaptureCount++;
                    importantEvent = true;
                    break;
                case TelemetryEventKind.ScannerSnapshotCaptured:
                    _metadata.FullSnapshotCaptureCount++;
                    importantEvent = true;
                    break;
            }
        }

        foreach (ScannerObservationMessage observation in message.ScanObservations)
        {
            _scannerWriter.WriteLine(JsonSerializer.Serialize(observation, JsonOptions));
            if (observation.Changed)
            {
                WriteTimelineRecord("scanner-change", observation.TimestampUtc, observation.MonotonicTicks, new
                {
                    observation.ExperimentId,
                    Phase = observation.Phase.ToString(),
                    observation.ActionLabel,
                    observation.FighterSlot,
                    observation.ActorAddress,
                    observation.RegionPath,
                    observation.ObjectOffset,
                    ValueType = observation.ValueType.ToString(),
                    observation.PreviousNumericValue,
                    observation.NumericValue,
                    observation.Delta
                });
            }
            _metadata.ScannerObservationCount++;
            _metadata.LastMonotonicTicks = Math.Max(_metadata.LastMonotonicTicks, observation.MonotonicTicks);
            switch (observation.Phase)
            {
                case ScanObservationPhase.Baseline:
                    _metadata.BaselineObservationCount++;
                    break;
                case ScanObservationPhase.Comparison:
                    _metadata.ComparisonObservationCount++;
                    break;
                case ScanObservationPhase.Snapshot:
                    _metadata.SnapshotObservationCount++;
                    break;
                case ScanObservationPhase.ContinuousDelta:
                    _metadata.ContinuousDeltaObservationCount++;
                    break;
            }
            if (observation.Changed) _metadata.ScannerChangedObservationCount++;
            if (observation.Stable) _metadata.ScannerStableObservationCount++;
            _experimentIds.Add(observation.ExperimentId);
            _actionLabels.Add(observation.ActionLabel);
            string candidateKey = $"{observation.RegionPath}:+0x{observation.ObjectOffset:X}:{observation.ValueType}";
            if (_candidateKeys.Add(candidateKey))
            {
                _candidateKeyWriter.WriteLine(JsonSerializer.Serialize(candidateKey, JsonOptions));
            }
        }

        long receiptMonotonicTicks = Stopwatch.GetTimestamp();
        if (_metadata.LastChronologyRuntimeProcessId != message.RuntimeProcessId)
        {
            if (_metadata.LastChronologyRuntimeProcessId != 0)
            {
                _metadata.ChronologyRuntimeRestartCount++;
            }
            _metadata.LastChronologyRuntimeProcessId = message.RuntimeProcessId;
            _metadata.LastChronologySequence = 0;
            _metadata.LastChronologyEpoch = -1;
        }

        foreach (ChronologySampleMessage sample in message.ChronologySamples.OrderBy(sample => sample.Sequence))
        {
            if (sample.Epoch != _metadata.LastChronologyEpoch)
            {
                _metadata.LastChronologyEpoch = sample.Epoch;
                _metadata.ChronologyEpochCount++;
            }
            double receiptLatencyMilliseconds = message.MonotonicFrequency > 0
                ? Math.Max(0, (receiptMonotonicTicks - sample.MonotonicTicks) * 1000.0 / message.MonotonicFrequency)
                : 0;
            object chronologyRecord = new
            {
                Sample = sample,
                RelativeMilliseconds = RelativeMilliseconds(sample.MonotonicTicks),
                ReceiptMonotonicTicks = receiptMonotonicTicks,
                ReceiptLatencyMilliseconds = receiptLatencyMilliseconds
            };
            _chronologyWriter.WriteLine(JsonSerializer.Serialize(chronologyRecord, JsonOptions));
            WriteTimelineRecord(sample.Initial ? "chronology-baseline" : "chronology-change", sample.TimestampUtc, sample.MonotonicTicks, new
            {
                sample.Sequence,
                sample.CaptureId,
                sample.Epoch,
                sample.FighterSlot,
                sample.ActorAddress,
                sample.FighterIdentityKey,
                sample.FighterSlotGeneration,
                sample.RegionPath,
                sample.ObjectOffset,
                ValueType = sample.ValueType.ToString(),
                sample.PreviousNumericValue,
                sample.NumericValue,
                sample.Delta,
                sample.Label,
                sample.ValidationStage,
                ReceiptLatencyMilliseconds = receiptLatencyMilliseconds
            });

            if (_metadata.LastChronologySequence > 0)
            {
                if (sample.Sequence <= _metadata.LastChronologySequence)
                {
                    _metadata.ChronologyOutOfOrderCount++;
                }
                else if (sample.Sequence > _metadata.LastChronologySequence + 1)
                {
                    _metadata.ChronologySequenceGapCount += sample.Sequence - _metadata.LastChronologySequence - 1;
                }
            }
            _metadata.LastChronologySequence = Math.Max(_metadata.LastChronologySequence, sample.Sequence);
            _metadata.ChronologySampleCount++;
            if (sample.Changed) _metadata.ChronologyChangedSampleCount++;
            if (sample.Initial) _metadata.ChronologyInitialSampleCount++;
            _metadata.MaximumChronologyReceiptLatencyMilliseconds = Math.Max(
                _metadata.MaximumChronologyReceiptLatencyMilliseconds, receiptLatencyMilliseconds);
            _metadata.LastMonotonicTicks = Math.Max(_metadata.LastMonotonicTicks, sample.MonotonicTicks);
        }
        _metadata.ChronologyConfiguration = message.Chronology.Configuration;
        _metadata.DroppedChronologySampleCount = message.Chronology.EpochDroppedSampleCount;
        _metadata.InvalidatedChronologySampleCount = Math.Max(
            0, message.Chronology.InvalidatedSampleCount - _metadata.ChronologyInvalidatedAtStart);
        _metadata.ChronologyPollCount = message.Chronology.EpochPollCount;
        _metadata.ChronologyReadCount = message.Chronology.EpochReadCount;
        _metadata.ChronologyUnreadableReadCount = message.Chronology.EpochUnreadableReadCount;
        _metadata.ChronologyPollOverrunCount = message.Chronology.EpochPollOverrunCount;
        _metadata.MaximumChronologyPollDurationMilliseconds = Math.Max(
            _metadata.MaximumChronologyPollDurationMilliseconds,
            message.Chronology.EpochMaximumPollDurationMilliseconds);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (importantEvent || now - _lastFlushUtc >= FlushInterval)
        {
            FlushWriters();
            _lastFlushUtc = now;
        }
        if (importantEvent || now - _lastMetadataUtc >= MetadataInterval)
        {
            WriteMetadata();
            _lastMetadataUtc = now;
        }
    }

    internal string Stop()
    {
        if (_metadata is null)
        {
            throw new InvalidOperationException("No capability-scanner session is recording.");
        }

        DateTimeOffset endedUtc = DateTimeOffset.UtcNow;
        long endedMonotonicTicks = Stopwatch.GetTimestamp();
        _metadata.EndedUtc = endedUtc;
        _metadata.EndedMonotonicTicks = endedMonotonicTicks;
        _metadata.LastMonotonicTicks = Math.Max(_metadata.LastMonotonicTicks, endedMonotonicTicks);
        WriteTimelineRecord("session-stop", endedUtc, endedMonotonicTicks, new
        {
            _metadata.SessionId,
            _metadata.FrameCount,
            _metadata.EventCount,
            _metadata.ScannerObservationCount,
            _metadata.ChronologySampleCount,
            _metadata.DroppedScannerObservationCount,
            _metadata.DroppedChronologySampleCount,
            _metadata.InvalidatedChronologySampleCount
        });
        string completedFolder = SessionFolder ?? _sessionsRoot;
        try
        {
            FlushWriters();
            WriteCandidateIndex();
            WriteChronologyWatchlist();
            WriteRuntimeAccessArchitecture(_latestRuntimeAccess);
            WriteMetadata();
            return completedFolder;
        }
        finally
        {
            DisposeWriters();
            _metadata = null;
            _latestRuntimeAccess = null;
            _runtimeAccessSnapshotKey = null;
            SessionFolder = null;
            _experimentIds.Clear();
            _actionLabels.Clear();
            _candidateKeys.Clear();
        }
    }

    public void Dispose()
    {
        if (_metadata is not null)
        {
            try
            {
                Stop();
            }
            catch
            {
                DisposeWriters();
            }
        }
        else
        {
            DisposeWriters();
        }
    }

    private void FlushWriters()
    {
        _frameWriter?.Flush();
        _eventWriter?.Flush();
        _scannerWriter?.Flush();
        _candidateKeyWriter?.Flush();
        _timelineWriter?.Flush();
        _chronologyWriter?.Flush();
        _rawMemoryWriter?.Flush();
    }

    private void DisposeWriters()
    {
        _frameWriter?.Dispose();
        _eventWriter?.Dispose();
        _scannerWriter?.Dispose();
        _candidateKeyWriter?.Dispose();
        _timelineWriter?.Dispose();
        _chronologyWriter?.Dispose();
        _rawMemoryWriter?.Dispose();
        _frameWriter = null;
        _eventWriter = null;
        _scannerWriter = null;
        _candidateKeyWriter = null;
        _timelineWriter = null;
        _chronologyWriter = null;
        _rawMemoryWriter = null;
    }

    private void WriteTimelineRecord(string stream, DateTimeOffset timestampUtc, long monotonicTicks, object payload)
    {
        if (_metadata is null || _timelineWriter is null)
        {
            return;
        }

        object record = new
        {
            Stream = stream,
            TimestampUtc = timestampUtc,
            MonotonicTicks = monotonicTicks,
            RelativeMilliseconds = RelativeMilliseconds(monotonicTicks),
            Payload = payload
        };
        _timelineWriter.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
        _metadata.TimelineRecordCount++;
    }

    private double RelativeMilliseconds(long monotonicTicks)
    {
        if (_metadata is null || _metadata.MonotonicFrequency <= 0)
        {
            return 0;
        }

        return (monotonicTicks - _metadata.StartMonotonicTicks) * 1000.0 / _metadata.MonotonicFrequency;
    }

    private void WriteMetadata()
    {
        if (_metadata is null || SessionFolder is null)
        {
            return;
        }

        _metadata.ExperimentIds = _experimentIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        _metadata.ActionLabels = _actionLabels.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        _metadata.DistinctCandidateCount = _candidateKeys.Count;
        string path = Path.Combine(SessionFolder, "session.json");
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_metadata, MetadataJsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }


    private void WriteChronologyWatchlist()
    {
        if (_metadata is null || SessionFolder is null)
        {
            return;
        }

        string path = Path.Combine(SessionFolder, "chronology-watchlist.json");
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath,
            JsonSerializer.Serialize(_metadata.ChronologyConfiguration, MetadataJsonOptions),
            new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void WriteRuntimeAccessArchitecture(RuntimeAccessStatusMessage? access)
    {
        if (SessionFolder is null)
        {
            return;
        }

        RuntimeAccessStatusMessage snapshot = access ?? new RuntimeAccessStatusMessage(
            "Runtime Access Architecture Gate 0",
            true,
            false,
            false,
            false,
            null,
            "Runtime had not connected when recording started.",
            [],
            [],
            new MemoryAccessMetricsMessage("observer", 0, 0, 0, 0, 0, 0, 0, 0, 0),
            new MemoryAccessMetricsMessage("chronology", 0, 0, 0, 0, 0, 0, 0, 0, 0),
            new ComparisonPolicyMessage(
                "compressed-scale-v1", 1.0e-6, 1.0e-6,
                "Chronology uses exact raw-bit equality.",
                "Recognize compressed combat-scale changes."));
        string path = Path.Combine(SessionFolder, "runtime-access-architecture.json");
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, MetadataJsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string CreateRuntimeAccessSnapshotKey(RuntimeAccessStatusMessage access)
    {
        string reports = string.Join(";", access.LocatorReports.Select(report =>
            $"{report.ProviderId}:{report.Outcome}:{report.CandidateAddress?.ToString("X16") ?? "none"}:{report.CandidateScore}"));
        return $"{access.ArchitectureGate}|{access.ExternalReadOnly}|{access.InjectionUsed}|{access.HooksUsed}|" +
            $"{access.GameWritesUsed}|{access.ActiveLocatorId}|{access.LocatorDetail}|{reports}|" +
            $"{access.AddressProvenance.Count}|{access.ComparisonPolicy.PolicyId}";
    }

    private void WriteCandidateIndex()
    {
        if (SessionFolder is null)
        {
            return;
        }

        string path = Path.Combine(SessionFolder, "candidate-index.json");
        string temporaryPath = path + ".tmp";
        string[] ordered = _candidateKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(ordered, MetadataJsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static StreamWriter CreateWriter(string path)
    {
        FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read, WriterBufferSize, FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(false), WriterBufferSize, leaveOpen: false)
        {
            AutoFlush = false
        };
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        cleaned = string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "Capability-Scan" : cleaned[..Math.Min(cleaned.Length, 48)];
    }

    private sealed class SessionMetadata
    {
        public int SchemaVersion { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? EndedUtc { get; set; }
        public int? GameProcessId { get; set; }
        public string? GameVersion { get; set; }
        public int? RuntimeProcessId { get; set; }
        public int ProtocolVersion { get; set; }
        public uint PatcherImageSize { get; set; }
        public bool HealthScalerBoundaryPreserved { get; set; }
        public bool ReadOnlyExternalRuntime { get; set; }
        public string ArchitectureGate { get; set; } = string.Empty;
        public ComparisonPolicyMessage? ComparisonPolicy { get; set; }
        public string? ActiveLocatorId { get; set; }
        public string LastLocatorDetail { get; set; } = string.Empty;
        public long RawMemoryObservationCount { get; set; }
        public long RawMemoryReadFailureCount { get; set; }
        public long ObserverReadRequests { get; set; }
        public long ObserverReadProcessMemoryCalls { get; set; }
        public long ObserverRequestedBytes { get; set; }
        public long ObserverCompletedBytes { get; set; }
        public long ObserverFailedReadCalls { get; set; }
        public long ObserverRejectedReadRequests { get; set; }
        public long ObserverVirtualQueryCalls { get; set; }
        public long ChronologyReadRequests { get; set; }
        public long ChronologyReadProcessMemoryCalls { get; set; }
        public long ChronologyRequestedBytes { get; set; }
        public long ChronologyCompletedBytes { get; set; }
        public long ChronologyFailedReadCalls { get; set; }
        public long ChronologyRejectedReadRequests { get; set; }
        public long ChronologyVirtualQueryCalls { get; set; }
        public ScannerConfiguration ScannerConfiguration { get; set; } = ScannerConfiguration.Default;
        public ChronologyConfiguration ChronologyConfiguration { get; set; } = ChronologyConfiguration.Default;
        public long MonotonicFrequency { get; set; }
        public long StartMonotonicTicks { get; set; }
        public long? EndedMonotonicTicks { get; set; }
        public long LastMonotonicTicks { get; set; }
        public long TimelineRecordCount { get; set; }
        public long ChronologySampleCount { get; set; }
        public long ChronologyChangedSampleCount { get; set; }
        public long ChronologyInitialSampleCount { get; set; }
        public long ChronologyOutOfOrderCount { get; set; }
        public long ChronologySequenceGapCount { get; set; }
        public long DroppedChronologySampleCount { get; set; }
        public long InvalidatedChronologySampleCount { get; set; }
        public long ChronologyInvalidatedAtStart { get; set; }
        public long ChronologyPollCount { get; set; }
        public long ChronologyReadCount { get; set; }
        public long ChronologyUnreadableReadCount { get; set; }
        public long ChronologyPollOverrunCount { get; set; }
        public long LastChronologySequence { get; set; }
        public int LastChronologyRuntimeProcessId { get; set; }
        public long LastChronologyEpoch { get; set; } = -1;
        public int ChronologyEpochCount { get; set; }
        public int ChronologyRuntimeRestartCount { get; set; }
        public double MaximumChronologyReceiptLatencyMilliseconds { get; set; }
        public double MaximumChronologyPollDurationMilliseconds { get; set; }
        public long FrameCount { get; set; }
        public long EventCount { get; set; }
        public long FighterAcquireCount { get; set; }
        public long FighterReleaseCount { get; set; }
        public long ValueChangeCount { get; set; }
        public long SnapshotEventCount { get; set; }
        public long ScannerObservationCount { get; set; }
        public long ScannerChangedObservationCount { get; set; }
        public long ScannerStableObservationCount { get; set; }
        public long BaselineObservationCount { get; set; }
        public long ComparisonObservationCount { get; set; }
        public long SnapshotObservationCount { get; set; }
        public long ContinuousDeltaObservationCount { get; set; }
        public long BaselineCaptureCount { get; set; }
        public long ComparisonCaptureCount { get; set; }
        public long FullSnapshotCaptureCount { get; set; }
        public int DroppedScannerObservationCount { get; set; }
        public int MaximumConcurrentFighters { get; set; }
        public string LastState { get; set; } = string.Empty;
        public string LastScannerDetail { get; set; } = string.Empty;
        public long LastHeartbeat { get; set; }
        public ulong? LastBattleCoreAddress { get; set; }
        public List<string> ExperimentIds { get; set; } = [];
        public List<string> ActionLabels { get; set; } = [];
        public int DistinctCandidateCount { get; set; }
    }
}
