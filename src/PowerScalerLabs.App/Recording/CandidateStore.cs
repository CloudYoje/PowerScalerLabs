using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PowerScalerLabs.App.Models;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.App.Recording;

internal sealed class CandidateStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly string _candidateGroupsDirectory;
    private readonly string _candidateRolesDirectory;
    private readonly string _candidateStatusesDirectory;
    private readonly string _classificationIndexPath;
    private readonly string _physicalGroupsPath;
    private readonly string _unresolvedIndexPath;
    private readonly string _candidateTiersDirectory;
    private readonly string _candidateValidationDirectory;
    private readonly string _findingsDirectory;
    private readonly string _findingsPath;
    private readonly string _findingGroupsDirectory;
    private readonly Dictionary<string, CandidateRecord> _records;
    private bool _dirty;
    private bool _exportsDirty;
    private IReadOnlyList<CandidateRecord>? _orderedSnapshot;
    private IReadOnlyList<CandidateGroupRecord>? _groupSnapshot;
    private DateTimeOffset _lastCheckpointUtc = DateTimeOffset.MinValue;
    private string? _persistenceWarning;

    internal CandidateStore(string dataRoot)
    {
        _directory = Path.Combine(dataRoot, "Candidates");
        _path = Path.Combine(_directory, "candidates.json");
        _candidateGroupsDirectory = Path.Combine(_directory, "ByStat");
        _candidateRolesDirectory = Path.Combine(_directory, "ByRole");
        _candidateStatusesDirectory = Path.Combine(_directory, "ByStatus");
        _classificationIndexPath = Path.Combine(_directory, "classification-index.json");
        _physicalGroupsPath = Path.Combine(_directory, "physical-groups.json");
        _unresolvedIndexPath = Path.Combine(_directory, "unresolved-index.json");
        _candidateTiersDirectory = Path.Combine(_directory, "ByTier");
        _candidateValidationDirectory = Path.Combine(_directory, "ByValidation");
        _findingsDirectory = Path.Combine(dataRoot, "Findings");
        _findingsPath = Path.Combine(_findingsDirectory, "findings.json");
        _findingGroupsDirectory = Path.Combine(_findingsDirectory, "ByStat");
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_candidateGroupsDirectory);
        Directory.CreateDirectory(_candidateRolesDirectory);
        Directory.CreateDirectory(_candidateStatusesDirectory);
        Directory.CreateDirectory(_candidateTiersDirectory);
        Directory.CreateDirectory(_candidateValidationDirectory);
        Directory.CreateDirectory(_findingsDirectory);
        Directory.CreateDirectory(_findingGroupsDirectory);

        _records = Load().ToDictionary(record => record.CandidateId, StringComparer.OrdinalIgnoreCase);
        bool migrated = false;
        foreach (CandidateRecord record in _records.Values)
        {
            NormalizeLoadedRecord(record);
            bool classificationMissing = string.IsNullOrWhiteSpace(record.StatFamily) ||
                string.IsNullOrWhiteSpace(record.StatRole) ||
                string.IsNullOrWhiteSpace(record.ClassificationSource) ||
                record.ClassificationTags.Count == 0;
            if (classificationMissing || IsKnownCurrentHealth(record) || IsKnownMaximumHealth(record))
            {
                Classify(record);
                migrated = true;
            }
        }
        if (migrated)
        {
            MarkDirty(true, forceSave: true);
        }
    }

    internal IReadOnlyList<CandidateRecord> Records => _orderedSnapshot ??= _records.Values
        .OrderBy(record => FamilyRank(record.StatFamily))
        .ThenBy(record => StatusRank(record.Status))
        .ThenByDescending(record => record.Confidence)
        .ThenBy(record => record.RegionPath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(record => record.ObjectOffset)
        .ThenBy(record => record.ValueType, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal IReadOnlyList<CandidateGroupRecord> Groups =>
        _groupSnapshot ??= CandidateGroupBuilder.Build(Records);

    internal bool ObserveTelemetry(string sessionId, IReadOnlyList<TelemetryEventMessage> events)
    {
        bool changed = false;
        HashSet<CandidateRecord> touched = [];
        HashSet<CandidateRecord> classificationTouched = [];
        foreach (TelemetryEventMessage telemetryEvent in events)
        {
            if (telemetryEvent.ObjectOffset == 0 ||
                telemetryEvent.Kind is TelemetryEventKind.FighterAcquired or TelemetryEventKind.FighterReleased)
            {
                continue;
            }

            CandidateRecord record = GetOrCreate(
                "Battle_Mob",
                telemetryEvent.ObjectOffset,
                ScannerValueType.Float32.ToString(),
                telemetryEvent.TimestampUtc,
                LabelForKnownOffset("Battle_Mob", telemetryEvent.ObjectOffset, ScannerValueType.Float32),
                out bool isNew);

            record.EvidenceCount++;
            if (telemetryEvent.Kind == TelemetryEventKind.ValueChanged)
            {
                record.ChangeCount++;
                ApplyDelta(record, telemetryEvent.CurrentValue - telemetryEvent.PreviousValue);
            }
            if (telemetryEvent.Kind == TelemetryEventKind.Snapshot)
            {
                record.SnapshotCount++;
            }

            ApplyValue(record, telemetryEvent.CurrentValue);
            record.LastSeenUtc = telemetryEvent.TimestampUtc;
            AddUnique(record.ActorAddresses, $"0x{telemetryEvent.ActorAddress:X16}");
            AddUnique(record.SessionIds, sessionId);
            ApplySlotEvidence(record, sessionId, telemetryEvent.FighterSlot, telemetryEvent.ActorAddress,
                telemetryEvent.Kind == TelemetryEventKind.ValueChanged, telemetryEvent.CurrentValue - telemetryEvent.PreviousValue, stable: false);
            touched.Add(record);
            if (isNew || telemetryEvent.Kind == TelemetryEventKind.ValueChanged) classificationTouched.Add(record);
            changed = true;
        }

        FinalizeTouched(touched, classificationTouched);
        MarkDirty(changed);
        return changed;
    }

    internal bool ObserveScanner(string sessionId, IReadOnlyList<ScannerObservationMessage> observations)
    {
        bool changed = false;
        HashSet<CandidateRecord> touched = [];
        HashSet<CandidateRecord> classificationTouched = [];
        foreach (ScannerObservationMessage observation in observations)
        {
            if (!observation.Plausible || !double.IsFinite(observation.NumericValue))
            {
                continue;
            }

            CandidateRecord record = GetOrCreate(
                observation.RegionPath,
                observation.ObjectOffset,
                observation.ValueType.ToString(),
                observation.TimestampUtc,
                LabelForKnownOffset(observation.RegionPath, observation.ObjectOffset, observation.ValueType),
                out bool isNew);

            record.EvidenceCount++;
            if (!string.IsNullOrWhiteSpace(observation.Classification))
            {
                record.ValueShape = observation.Classification;
            }
            switch (observation.Phase)
            {
                case ScanObservationPhase.Baseline:
                    record.BaselineCount++;
                    record.SnapshotCount++;
                    break;
                case ScanObservationPhase.Comparison:
                    record.ComparisonCount++;
                    break;
                case ScanObservationPhase.Snapshot:
                    record.SnapshotCount++;
                    break;
                case ScanObservationPhase.ContinuousDelta:
                    record.ContinuousChangeCount++;
                    break;
            }

            if (observation.Changed)
            {
                record.ChangeCount++;
                ApplyDelta(record, observation.Delta);
            }
            if (observation.Stable)
            {
                record.StableCount++;
            }
            ApplyValue(record, observation.NumericValue);

            record.LastSeenUtc = observation.TimestampUtc;
            AddUnique(record.ActorAddresses, $"0x{observation.ActorAddress:X16}");
            AddUnique(record.SessionIds, sessionId);
            AddUnique(record.ExperimentIds, observation.ExperimentId);
            ApplyActionEvidence(record, sessionId, observation);
            ApplySlotEvidence(record, sessionId, observation.FighterSlot, observation.ActorAddress,
                observation.Changed, observation.Delta, observation.Stable);
            touched.Add(record);
            if (isNew || observation.Changed) classificationTouched.Add(record);
            changed = true;
        }

        FinalizeTouched(touched, classificationTouched);
        MarkDirty(changed);
        return changed;
    }

    internal void PromoteToSolid(string candidateId)
    {
        if (!_records.TryGetValue(candidateId, out CandidateRecord? record))
        {
            return;
        }

        if (HasProtectedValidation(record))
        {
            return;
        }

        record.ManuallyPromoted = true;
        record.ManuallyRejected = false;
        record.Status = "Solid";
        record.ValidationStage = PromoteValidationStage(record.ValidationStage, CandidateValidationStages.Correlated);
        record.Confidence = Math.Max(record.Confidence, 0.95);
        AppendNote(record, "Manually promoted to Correlated after controlled testing. This is not yet code-anchored or causally validated.");
        MarkDirty(true, forceSave: true);
    }

    internal void RejectAsNoise(string candidateId)
    {
        if (!_records.TryGetValue(candidateId, out CandidateRecord? record))
        {
            return;
        }

        if (HasProtectedValidation(record))
        {
            return;
        }

        record.ManuallyRejected = true;
        record.ManuallyPromoted = false;
        record.Status = "Noise";
        record.ValidationStage = CandidateValidationStages.Observed;
        record.Confidence = Math.Min(record.Confidence, 0.10);
        AppendNote(record, "Manually rejected as noise or an unrelated field.");
        MarkDirty(true, forceSave: true);
    }

    internal void AssignLabel(string candidateId, string label)
    {
        if (!_records.TryGetValue(candidateId, out CandidateRecord? record) || string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        if (IsKnownCurrentHealth(record) || IsKnownMaximumHealth(record))
        {
            record.Label = IsKnownCurrentHealth(record) ? "Current health" : "Maximum health";
            Classify(record);
            MarkDirty(true, forceSave: true);
            return;
        }

        record.Label = label.Trim();
        AppendNote(record, $"Assigned label '{record.Label}'.");
        Classify(record);
        MarkDirty(true, forceSave: true);
    }

    internal void AssignClassification(string candidateId, string statFamily, string statRole)
    {
        if (!_records.TryGetValue(candidateId, out CandidateRecord? record) ||
            !CandidateTaxonomy.Families.Contains(statFamily, StringComparer.OrdinalIgnoreCase) ||
            !CandidateTaxonomy.Roles.Contains(statRole, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsKnownCurrentHealth(record) || IsKnownMaximumHealth(record))
        {
            Classify(record);
            MarkDirty(true, forceSave: true);
            return;
        }

        record.StatFamily = CandidateTaxonomy.Families.First(value =>
            string.Equals(value, statFamily, StringComparison.OrdinalIgnoreCase));
        record.StatRole = CandidateTaxonomy.Roles.First(value =>
            string.Equals(value, statRole, StringComparison.OrdinalIgnoreCase));
        record.ManuallyClassified = true;
        record.ClassificationSource = "Manual";
        record.ClassificationConfidence = 1.0;
        record.ClassificationTags = ["Manual classification", $"Family: {record.StatFamily}", $"Role: {record.StatRole}"];
        AppendNote(record, $"Manually classified as {record.StatFamily} / {record.StatRole}.");
        MarkDirty(true, forceSave: true);
    }

    internal void RestoreAutomaticClassification(string candidateId)
    {
        if (!_records.TryGetValue(candidateId, out CandidateRecord? record))
        {
            return;
        }

        record.ManuallyClassified = false;
        record.ClassificationSource = "Automatic";
        Classify(record);
        AppendNote(record, "Restored evidence-based automatic classification.");
        MarkDirty(true, forceSave: true);
    }

    internal string DirectoryPath => _directory;
    internal void RecordCodeAnchor(string candidateId, string evidenceId)
    {
        if (!_records.TryGetValue(candidateId, out CandidateRecord? record) || string.IsNullOrWhiteSpace(evidenceId))
        {
            return;
        }

        string normalizedEvidenceId = evidenceId.Trim();
        if (!AddUniqueValidationEvidence(record, normalizedEvidenceId))
        {
            return;
        }

        record.CodeAnchorCount++;
        record.ValidationStage = PromoteValidationStage(record.ValidationStage, CandidateValidationStages.CodeAnchored);
        record.Status = record.Status == "Known" ? "Known" : "Solid";
        record.Confidence = Math.Max(record.Confidence, 0.97);
        AppendNote(record, $"Code access anchored by validation evidence '{normalizedEvidenceId}'.");
        MarkDirty(true, forceSave: true);
    }

    internal void RecordCausalValidation(string candidateId, string evidenceId, bool passed)
    {
        if (!_records.TryGetValue(candidateId, out CandidateRecord? record) || string.IsNullOrWhiteSpace(evidenceId))
        {
            return;
        }

        string normalizedEvidenceId = evidenceId.Trim();
        if (!AddUniqueValidationEvidence(record, normalizedEvidenceId))
        {
            return;
        }

        if (passed && record.CodeAnchorCount <= 0 &&
            CandidateGroupBuilder.ValidationStageRank(record.ValidationStage) <
            CandidateGroupBuilder.ValidationStageRank(CandidateValidationStages.CodeAnchored))
        {
            record.ValidationFailureCount++;
            AppendNote(record,
                $"Causal validation evidence '{normalizedEvidenceId}' was quarantined because no code anchor exists yet.");
            MarkDirty(true, forceSave: true);
            return;
        }

        if (passed)
        {
            record.CausalValidationCount++;
            record.ValidationStage = PromoteValidationStage(record.ValidationStage, CandidateValidationStages.CausallyValidated);
            record.Status = record.Status == "Known" ? "Known" : "Solid";
            record.Confidence = Math.Max(record.Confidence, 0.985);
            AppendNote(record, $"Controlled reversible causal validation passed: '{normalizedEvidenceId}'.");
        }
        else
        {
            record.ValidationFailureCount++;
            AppendNote(record, $"Controlled validation did not produce the predicted result: '{normalizedEvidenceId}'.");
        }
        MarkDirty(true, forceSave: true);
    }

    internal void MarkVerified(string candidateId, string evidenceId)
    {
        if (!_records.TryGetValue(candidateId, out CandidateRecord? record) || string.IsNullOrWhiteSpace(evidenceId))
        {
            return;
        }

        bool hasRequiredEvidence = record.CodeAnchorCount > 0 &&
            record.CausalValidationCount >= 2 &&
            record.DistinctActorCount >= 2 &&
            (record.SessionCount >= 2 || record.ExperimentCount >= 2) &&
            CandidateGroupBuilder.ValidationStageRank(record.ValidationStage) >=
            CandidateGroupBuilder.ValidationStageRank(CandidateValidationStages.CausallyValidated);
        if (!hasRequiredEvidence)
        {
            AppendNote(record,
                "Verification request rejected: requires a code anchor, two causal passes, two actors, and repeated session or experiment coverage.");
            MarkDirty(true, forceSave: true);
            return;
        }

        string normalizedEvidenceId = evidenceId.Trim();
        if (!AddUniqueValidationEvidence(record, normalizedEvidenceId))
        {
            return;
        }

        record.ValidationStage = CandidateValidationStages.Verified;
        record.Status = "Known";
        record.Confidence = 1.0;
        AppendNote(record, $"Promoted to Verified finding: '{normalizedEvidenceId}'.");
        MarkDirty(true, forceSave: true);
    }

    internal string FindingsDirectoryPath => _findingsDirectory;

    internal string? TakePersistenceWarning()
    {
        string? warning = _persistenceWarning;
        _persistenceWarning = null;
        return warning;
    }

    internal void Flush()
    {
        if (_dirty || _exportsDirty)
        {
            Save(includeExports: true);
        }
    }

    public void Dispose() => Flush();

    private CandidateRecord GetOrCreate(
        string regionPath,
        uint offset,
        string valueType,
        DateTimeOffset timestamp,
        string label,
        out bool created)
    {
        string candidateId = $"{regionPath}:+0x{offset:X}:{valueType}";
        if (_records.TryGetValue(candidateId, out CandidateRecord? existing))
        {
            created = false;
            return existing;
        }

        CandidateRecord createdRecord = new()
        {
            CandidateId = candidateId,
            ObjectType = "Battle_Mob",
            RegionPath = regionPath,
            ObjectOffset = offset,
            ValueType = valueType,
            Label = label,
            FirstSeenUtc = timestamp,
            LastSeenUtc = timestamp,
            Notes = "Captured by the external, read-only PowerScaler Labs Capability Scanner."
        };
        _records.Add(candidateId, createdRecord);
        created = true;
        return createdRecord;
    }

    private IReadOnlyList<CandidateRecord> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CandidateRecord>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch
        {
            string damagedPath = _path + $".damaged-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
            File.Copy(_path, damagedPath, overwrite: false);
            return [];
        }
    }

    private static void NormalizeLoadedRecord(CandidateRecord record)
    {
        record.ActorAddresses ??= [];
        record.SessionIds ??= [];
        record.ExperimentIds ??= [];
        record.ActionEvidence ??= [];
        record.SlotEvidence ??= [];
        foreach (SlotEvidenceRecord slot in record.SlotEvidence)
        {
            slot.ActorAddresses ??= [];
            slot.SessionIds ??= [];
        }
        foreach (ActionEvidenceRecord action in record.ActionEvidence)
        {
            action.SessionIds ??= [];
            action.ExperimentIds ??= [];
            action.SlotEvidence ??= [];
            foreach (ActionSlotEvidenceRecord slot in action.SlotEvidence)
            {
                slot.ActorAddresses ??= [];
                slot.SessionIds ??= [];
            }
        }
        record.ClassificationTags ??= [];
        record.ClassificationScores ??= [];
        record.ValueShape ??= string.Empty;
        record.StatFamily = string.IsNullOrWhiteSpace(record.StatFamily) ? "Unknown" : record.StatFamily;
        record.StatRole = string.IsNullOrWhiteSpace(record.StatRole) ? "Unclassified" : record.StatRole;
        record.ClassificationSource = string.IsNullOrWhiteSpace(record.ClassificationSource)
            ? "Automatic"
            : record.ClassificationSource;
        record.ValidationEvidenceIds ??= [];
        record.ValidationStage = CandidateValidationStages.All.Contains(record.ValidationStage, StringComparer.Ordinal)
            ? record.ValidationStage
            : CandidateValidationStages.Observed;
        if (record.Status == "Known")
        {
            record.ValidationStage = CandidateValidationStages.Verified;
        }
        else if (record.CausalValidationCount > 0)
        {
            record.ValidationStage = PromoteValidationStage(record.ValidationStage, CandidateValidationStages.CausallyValidated);
        }
        else if (record.CodeAnchorCount > 0)
        {
            record.ValidationStage = PromoteValidationStage(record.ValidationStage, CandidateValidationStages.CodeAnchored);
        }
        else if (record.ManuallyPromoted)
        {
            record.ValidationStage = CandidateValidationStages.Correlated;
        }
        else if (record.ValidationStage == CandidateValidationStages.Correlated ||
                 record.Status is "Solid" or "Strong")
        {
            // Legacy builds promoted generic repeatability to semantic correlation. Demote those
            // automatic records during load; explicit/manual and stronger validation is preserved.
            record.ValidationStage = CandidateValidationStages.Observed;
            if (record.Status == "Solid") record.Status = "Strong";
        }
    }

    private void MarkDirty(bool changed, bool forceSave = false)
    {
        if (!changed)
        {
            return;
        }

        _dirty = true;
        _exportsDirty = true;
        _orderedSnapshot = null;
        _groupSnapshot = null;
        if (forceSave)
        {
            Save(includeExports: true);
        }
        else
        {
            TimeSpan checkpointInterval = _records.Count >= 50_000
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(20);
            if (DateTimeOffset.UtcNow - _lastCheckpointUtc >= checkpointInterval)
            {
                Save(includeExports: false);
            }
        }
    }

    private void Save(bool includeExports)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            IReadOnlyList<CandidateRecord> records = Records;
            WriteAtomic(_path, JsonSerializer.Serialize(records, JsonOptions));
            _dirty = false;
            _lastCheckpointUtc = DateTimeOffset.UtcNow;

            if (!includeExports)
            {
                return;
            }

            Directory.CreateDirectory(_candidateGroupsDirectory);
            Directory.CreateDirectory(_candidateRolesDirectory);
            Directory.CreateDirectory(_candidateStatusesDirectory);
            Directory.CreateDirectory(_candidateTiersDirectory);
            Directory.CreateDirectory(_candidateValidationDirectory);
            Directory.CreateDirectory(_findingsDirectory);
            Directory.CreateDirectory(_findingGroupsDirectory);
            WriteGroupedFiles(_candidateGroupsDirectory, records, record => record.StatFamily);
            WriteGroupedFiles(_candidateRolesDirectory, records, record => record.StatRole);
            WriteGroupedFiles(_candidateStatusesDirectory, records, record => record.Status);

            IReadOnlyList<CandidateGroupRecord> physicalGroups = Groups;
            WriteAtomic(_physicalGroupsPath, JsonSerializer.Serialize(physicalGroups, JsonOptions));
            WriteGroupedGroupFiles(_candidateTiersDirectory, physicalGroups, group => group.SignalTier);
            WriteGroupedGroupFiles(_candidateValidationDirectory, physicalGroups, group => group.ValidationStage);
            CandidateGroupRecord[] unresolved = physicalGroups
                .Where(group => !group.IsExplained && group.SignalTier != CandidateSignalTiers.BackgroundNoise)
                .OrderBy(group => CandidateGroupBuilder.SignalTierRank(group.SignalTier))
                .ThenByDescending(group => group.Confidence)
                .ThenByDescending(group => group.ChangeCount)
                .ToArray();
            WriteAtomic(_unresolvedIndexPath, JsonSerializer.Serialize(unresolved, JsonOptions));

            CandidateRecord[] findings = records
                .Where(record => record.Status is "Known" or "Solid")
                .ToArray();
            WriteAtomic(_findingsPath, JsonSerializer.Serialize(findings, JsonOptions));
            WriteGroupedFiles(_findingGroupsDirectory, findings, record => record.StatFamily);
            CandidateGroupRecord[] verifiedFindings = physicalGroups
                .Where(group => group.ValidationStage == CandidateValidationStages.Verified)
                .ToArray();
            WriteAtomic(Path.Combine(_findingsDirectory, "verified-findings.json"),
                JsonSerializer.Serialize(verifiedFindings, JsonOptions));

            object[] index = records
                .GroupBy(record => record.StatFamily, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => FamilyRank(group.Key))
                .Select(group => (object)new
                {
                    statFamily = group.Key,
                    total = group.Count(),
                    known = group.Count(record => record.Status == "Known"),
                    solid = group.Count(record => record.Status == "Solid"),
                    strong = group.Count(record => record.Status == "Strong"),
                    candidate = group.Count(record => record.Status == "Candidate"),
                    provisional = group.Count(record => record.Status == "Provisional"),
                    noise = group.Count(record => record.Status == "Noise"),
                    manuallyClassified = group.Count(record => record.ManuallyClassified),
                    distinctSlots = group.SelectMany(record => record.SlotEvidence).Select(slot => slot.Slot).Distinct().Count()
                })
                .ToArray();
            WriteAtomic(_classificationIndexPath, JsonSerializer.Serialize(index, JsonOptions));
            _exportsDirty = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
            _exportsDirty = true;
            _persistenceWarning = $"Candidate persistence was deferred: {exception.Message}";
        }
    }

    private static void WriteGroupedFiles(
        string directory,
        IEnumerable<CandidateRecord> records,
        Func<CandidateRecord, string> keySelector)
    {
        Directory.CreateDirectory(directory);
        foreach (string stale in Directory.EnumerateFiles(directory, "*.json"))
        {
            File.Delete(stale);
        }

        foreach (IGrouping<string, CandidateRecord> group in records
                     .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = SanitizeFileName(group.Key) + ".json";
            CandidateRecord[] ordered = group
                .OrderBy(record => StatusRank(record.Status))
                .ThenByDescending(record => record.Confidence)
                .ThenBy(record => record.RegionPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.ObjectOffset)
                .ToArray();
            WriteAtomic(Path.Combine(directory, fileName), JsonSerializer.Serialize(ordered, JsonOptions));
        }
    }

    private static void WriteGroupedGroupFiles(
        string directory,
        IEnumerable<CandidateGroupRecord> groups,
        Func<CandidateGroupRecord, string> keySelector)
    {
        Directory.CreateDirectory(directory);
        foreach (string stale in Directory.EnumerateFiles(directory, "*.json"))
        {
            File.Delete(stale);
        }

        foreach (IGrouping<string, CandidateGroupRecord> group in groups
                     .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = SanitizeFileName(group.Key) + ".json";
            CandidateGroupRecord[] ordered = group
                .OrderBy(item => CandidateGroupBuilder.SignalTierRank(item.SignalTier))
                .ThenByDescending(item => item.Confidence)
                .ThenByDescending(item => item.ChangeCount)
                .ThenBy(item => item.RegionPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ObjectOffset)
                .ToArray();
            WriteAtomic(Path.Combine(directory, fileName), JsonSerializer.Serialize(ordered, JsonOptions));
        }
    }

    private static string SanitizeFileName(string value)
    {
        char[] characters = value
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        string name = string.Join('-', new string(characters)
            .Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
    }

    private static void WriteAtomic(string path, string content)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void ApplyValue(CandidateRecord record, double value)
    {
        if (!double.IsFinite(value))
        {
            record.InvalidCount++;
            return;
        }

        if (record.ValidValueCount == 0)
        {
            record.MinimumValue = value;
            record.MaximumValue = value;
        }
        else
        {
            record.MinimumValue = Math.Min(record.MinimumValue, value);
            record.MaximumValue = Math.Max(record.MaximumValue, value);
        }
        record.ValidValueCount++;
        record.LastValue = value;
    }

    private static void ApplyDelta(CandidateRecord record, double delta)
    {
        if (!double.IsFinite(delta))
        {
            return;
        }

        if (record.DeltaSampleCount == 0)
        {
            record.MinimumDelta = delta;
            record.MaximumDelta = delta;
        }
        else
        {
            record.MinimumDelta = Math.Min(record.MinimumDelta, delta);
            record.MaximumDelta = Math.Max(record.MaximumDelta, delta);
        }
        record.DeltaSampleCount++;
        record.LastDelta = delta;
        if (delta > 0)
        {
            record.IncreaseCount++;
        }
        else if (delta < 0)
        {
            record.DecreaseCount++;
        }
    }

    private static void ApplyActionEvidence(
        CandidateRecord record,
        string sessionId,
        ScannerObservationMessage observation)
    {
        ActionEvidenceRecord? action = record.ActionEvidence.FirstOrDefault(existing =>
            string.Equals(existing.ActionLabel, observation.ActionLabel, StringComparison.OrdinalIgnoreCase));
        if (action is null && !observation.Changed)
        {
            return;
        }
        if (action is null)
        {
            action = new ActionEvidenceRecord { ActionLabel = observation.ActionLabel };
            record.ActionEvidence.Add(action);
        }

        action.ObservationCount++;
        if (observation.Changed)
        {
            action.ChangeCount++;
            double absoluteDelta = Math.Abs(observation.Delta);
            action.TotalAbsoluteDelta += absoluteDelta;
            action.MaximumAbsoluteDelta = Math.Max(action.MaximumAbsoluteDelta, absoluteDelta);
            if (observation.Delta > 0)
            {
                action.IncreaseCount++;
            }
            else if (observation.Delta < 0)
            {
                action.DecreaseCount++;
            }
        }
        if (observation.Stable)
        {
            action.StableCount++;
        }

        ActionSlotEvidenceRecord? slotEvidence = action.SlotEvidence.FirstOrDefault(item => item.Slot == observation.FighterSlot);
        if (slotEvidence is null)
        {
            slotEvidence = new ActionSlotEvidenceRecord { Slot = observation.FighterSlot };
            action.SlotEvidence.Add(slotEvidence);
        }
        slotEvidence.ObservationCount++;
        if (observation.Changed)
        {
            slotEvidence.ChangeCount++;
            if (observation.Delta > 0) slotEvidence.IncreaseCount++;
            else if (observation.Delta < 0) slotEvidence.DecreaseCount++;
        }
        if (observation.Stable) slotEvidence.StableCount++;
        AddUnique(slotEvidence.ActorAddresses, $"0x{observation.ActorAddress:X16}");
        AddUnique(slotEvidence.SessionIds, sessionId);

        AddUnique(action.SessionIds, sessionId);
        AddUnique(action.ExperimentIds, observation.ExperimentId);
    }

    private static void FinalizeTouched(
        IEnumerable<CandidateRecord> records,
        IReadOnlySet<CandidateRecord> classificationRecords)
    {
        foreach (CandidateRecord record in records)
        {
            Evaluate(record);
            if (classificationRecords.Contains(record))
            {
                Classify(record);
            }
        }
    }

    private static void ApplySlotEvidence(
        CandidateRecord record,
        string sessionId,
        int slot,
        ulong actorAddress,
        bool changed,
        double delta,
        bool stable)
    {
        SlotEvidenceRecord? evidence = record.SlotEvidence.FirstOrDefault(item => item.Slot == slot);
        if (evidence is null)
        {
            evidence = new SlotEvidenceRecord { Slot = slot };
            record.SlotEvidence.Add(evidence);
        }

        evidence.ObservationCount++;
        if (changed)
        {
            evidence.ChangeCount++;
            if (delta > 0) evidence.IncreaseCount++;
            else if (delta < 0) evidence.DecreaseCount++;
        }
        if (stable) evidence.StableCount++;
        if (actorAddress != 0) AddUnique(evidence.ActorAddresses, $"0x{actorAddress:X16}");
        AddUnique(evidence.SessionIds, sessionId);
    }

    private static void Evaluate(CandidateRecord record)
    {
        int existingValidationRank = CandidateGroupBuilder.ValidationStageRank(record.ValidationStage);
        if (existingValidationRank >= CandidateGroupBuilder.ValidationStageRank(CandidateValidationStages.Verified))
        {
            record.Status = "Known";
            record.ValidationStage = CandidateValidationStages.Verified;
            record.Confidence = 1.0;
            return;
        }
        if (existingValidationRank >= CandidateGroupBuilder.ValidationStageRank(CandidateValidationStages.CausallyValidated))
        {
            record.Status = "Solid";
            record.ValidationStage = CandidateValidationStages.CausallyValidated;
            record.Confidence = Math.Max(record.Confidence, 0.985);
            return;
        }
        if (existingValidationRank >= CandidateGroupBuilder.ValidationStageRank(CandidateValidationStages.CodeAnchored))
        {
            record.Status = "Solid";
            record.ValidationStage = CandidateValidationStages.CodeAnchored;
            record.Confidence = Math.Max(record.Confidence, 0.97);
            return;
        }

        if (record.ManuallyRejected)
        {
            record.Status = "Noise";
            record.ValidationStage = CandidateValidationStages.Observed;
            record.Confidence = Math.Min(record.Confidence, 0.10);
            return;
        }
        if (record.ManuallyPromoted)
        {
            record.Status = "Solid";
            record.ValidationStage = PromoteValidationStage(record.ValidationStage, CandidateValidationStages.Correlated);
            record.Confidence = Math.Max(record.Confidence, 0.95);
            return;
        }

        bool knownCurrentHealth = IsKnownCurrentHealth(record);
        bool knownMaximumHealth = IsKnownMaximumHealth(record);

        double confidence = 0.10;
        confidence += Math.Min(0.16, record.DistinctActorCount * 0.04);
        confidence += Math.Min(0.10, record.DistinctSlotCount * 0.025);
        confidence += Math.Min(0.14, record.SessionCount * 0.07);
        confidence += Math.Min(0.18, record.ExperimentCount * 0.06);
        confidence += Math.Min(0.16, record.ActionEvidence.Count(action => action.ChangeCount > 0) * 0.04);
        confidence += Math.Min(0.14, Math.Log10(record.ChangeCount + 1) * 0.06);
        confidence += Math.Min(0.08, Math.Log10(record.StableCount + record.SnapshotCount + 1) * 0.025);

        if (record.ChangeCount == 0 && record.ComparisonCount > 0)
        {
            confidence -= 0.08;
        }
        if (record.ActionEvidence.Count > 0 && record.ActionEvidence.All(action =>
                string.Equals(action.ActionLabel, "Continuous telemetry", StringComparison.OrdinalIgnoreCase)))
        {
            confidence -= 0.12;
        }
        confidence -= Math.Min(0.45, record.InvalidCount * 0.02);
        record.Confidence = Math.Clamp(confidence, 0, 0.99);

        if (knownCurrentHealth && record.ChangeCount >= 3 && record.DistinctActorCount >= 2)
        {
            record.Status = "Known";
            record.ValidationStage = CandidateValidationStages.Verified;
            record.Confidence = Math.Max(record.Confidence, 0.98);
            record.Label = "Current health";
            return;
        }
        if (knownMaximumHealth && record.SnapshotCount >= 2 && record.DistinctActorCount >= 2)
        {
            record.Status = "Known";
            record.ValidationStage = CandidateValidationStages.Verified;
            record.Confidence = Math.Max(record.Confidence, 0.98);
            record.Label = "Maximum health";
            return;
        }

        bool repeatableSignal = record.ExperimentCount >= 2 &&
            record.ActionEvidence.Count(action => action.ChangeCount > 0) >= 1 &&
            record.ChangeCount >= 2;
        record.Status = repeatableSignal && record.Confidence >= 0.78
            ? "Strong"
            : record.Confidence >= 0.52
                ? "Candidate"
                : "Provisional";
        // Repeatability controls signal strength only. It does not establish what the field means.
        // Semantic correlation is granted separately by structured-pair evidence or manual review.
        record.ValidationStage = CandidateValidationStages.Observed;
    }

    private static void Classify(CandidateRecord record)
    {
        if (record.ManuallyClassified)
        {
            record.ClassificationSource = "Manual";
            record.ClassificationConfidence = 1.0;
            return;
        }

        if (IsKnownCurrentHealth(record))
        {
            SetKnownClassification(record, "Health", "Current Value", "Known offset +0x100");
            return;
        }
        if (IsKnownMaximumHealth(record))
        {
            SetKnownClassification(record, "Health", "Maximum / Capacity", "Known offset +0x104");
            return;
        }

        Dictionary<string, double> scores = CandidateTaxonomy.Families
            .ToDictionary(family => family, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        List<string> tags = [];
        bool shapeEvidence = false;
        bool actionCorrelation = false;
        if (!string.IsNullOrWhiteSpace(record.ValueShape))
        {
            tags.Add(record.ValueShape);
        }

        if (string.Equals(record.ValueType, ScannerValueType.Pointer64.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            double pointerShapeScore = 0.35;
            if (record.ValueShape.Contains("Canonical user-mode pointer", StringComparison.OrdinalIgnoreCase))
            {
                pointerShapeScore += 0.25;
            }
            if (record.ChangeCount > 0 && record.DistinctActorCount > 1)
            {
                pointerShapeScore += 0.30;
            }
            if (record.RegionPath.Contains("->", StringComparison.Ordinal))
            {
                pointerShapeScore += 0.55;
            }
            AddScore(scores, "Object / Pointers", pointerShapeScore);
            tags.Add("Pointer-shaped interpretation; requires behavioral or structural confirmation");
            shapeEvidence = true;
        }
        if (string.Equals(record.ValueType, ScannerValueType.Byte.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            AddScore(scores, "State / Flags", 0.45);
            tags.Add("Byte-sized interpretation; insufficient alone for a state classification");
            shapeEvidence = true;
        }
        if (IsSmallIntegralRange(record))
        {
            AddScore(scores, "State / Flags", 0.25);
            tags.Add("Small integral range");
            shapeEvidence = true;
        }

        bool labelEvidence = ApplyLabelClassification(record.Label, scores, tags);

        foreach (ActionEvidenceRecord action in record.ActionEvidence.Where(action => action.ChangeCount > 0))
        {
            double signal = ActionSignal(action);
            string normalized = action.ActionLabel.Trim().ToLowerInvariant();
            if (normalized.Contains("spend ki"))
            {
                AddScore(scores, "Ki", DirectionalActionSignal(action, expectDecrease: true) * 2.4);
            }
            else if (normalized.Contains("regenerate ki") || normalized.Contains("gain ki"))
            {
                AddScore(scores, "Ki", DirectionalActionSignal(action, expectDecrease: false) * 2.4);
            }
            else if (normalized.Contains("spend stamina"))
            {
                AddScore(scores, "Stamina", DirectionalActionSignal(action, expectDecrease: true) * 2.4);
            }
            else if (normalized.Contains("regenerate stamina"))
            {
                AddScore(scores, "Stamina", DirectionalActionSignal(action, expectDecrease: false) * 2.4);
            }
            else if (normalized.Contains("take damage"))
            {
                AddScore(scores, "Health", DirectionalActionSignal(action, expectDecrease: true) * 1.8);
                AddScore(scores, "Defense / Guard", signal * 0.40);
            }
            else if (normalized.Contains("heal") || normalized.Contains("recover health"))
            {
                AddScore(scores, "Health", DirectionalActionSignal(action, expectDecrease: false) * 2.2);
            }
            else if (normalized.Contains("ko") || normalized.Contains("revive"))
            {
                AddScore(scores, "Health", signal * 1.4);
                AddScore(scores, "State / Flags", signal * 1.0);
            }
            else if (normalized.Contains("basic ki blast"))
            {
                AddScore(scores, "Basic Attack", signal * 1.5);
                AddScore(scores, "Ki", signal * 0.45);
            }
            else if (normalized.Contains("basic attack"))
            {
                AddScore(scores, "Basic Attack", signal * 1.45);
                AddScore(scores, "Health", signal * 0.70);
            }
            else if (normalized.Contains("strike skill"))
            {
                AddScore(scores, "Strike Skills", signal * 1.55);
                AddScore(scores, "Health", signal * 0.65);
            }
            else if (normalized.Contains("ki blast skill"))
            {
                AddScore(scores, "Ki Blast Skills", signal * 1.55);
                AddScore(scores, "Ki", signal * 0.80);
                AddScore(scores, "Health", signal * 0.55);
            }
            else if (normalized.Contains("evasive"))
            {
                AddScore(scores, "Movement", signal * 1.25);
                AddScore(scores, "Defense / Guard", signal * 1.05);
                AddScore(scores, "Stamina", signal * 0.85);
            }
            else if (normalized.Contains("de-transform") || normalized.Contains("transform"))
            {
                AddScore(scores, "Transformation", signal * 2.1);
            }
            else if (normalized.Contains("buff") || normalized.Contains("debuff") || normalized.Contains("effect"))
            {
                AddScore(scores, "State / Flags", signal * 1.45);
                AddScore(scores, "Transformation", signal * 0.55);
            }
            else if (normalized.Contains("cooldown"))
            {
                AddScore(scores, "Timers / Cooldowns", signal * 2.0);
            }
            else if (normalized.Contains("lock-on") || normalized.Contains("target change"))
            {
                AddScore(scores, "Identity / Metadata", signal * 1.5);
                AddScore(scores, "State / Flags", signal * 0.65);
            }
            else if (normalized.Contains("stamina break"))
            {
                AddScore(scores, "Stamina", signal * 1.35);
                AddScore(scores, "Defense / Guard", signal * 1.15);
            }
            else if (normalized.Contains("guard"))
            {
                AddScore(scores, "Defense / Guard", signal * 2.0);
                AddScore(scores, "Stamina", signal * 0.65);
            }
            else if (normalized.Contains("movement") || normalized.Contains("flight") ||
                     normalized.Contains("z-vanish") || normalized.Contains("step") || normalized.Contains("dash"))
            {
                AddScore(scores, "Movement", signal * 2.0);
                if (normalized.Contains("z-vanish") || normalized.Contains("dash"))
                {
                    AddScore(scores, "Stamina", signal * 0.55);
                }
            }

            if (normalized.Contains("spend ki") || normalized.Contains("regenerate ki") || normalized.Contains("gain ki") ||
                normalized.Contains("spend stamina") || normalized.Contains("regenerate stamina") ||
                normalized.Contains("take damage") || normalized.Contains("heal") || normalized.Contains("recover health") ||
                normalized.Contains("ko") || normalized.Contains("revive") || normalized.Contains("basic attack") ||
                normalized.Contains("basic ki blast") || normalized.Contains("strike skill") ||
                normalized.Contains("ki blast skill") || normalized.Contains("evasive") ||
                normalized.Contains("transform") || normalized.Contains("buff") || normalized.Contains("debuff") ||
                normalized.Contains("effect") || normalized.Contains("cooldown") || normalized.Contains("lock-on") ||
                normalized.Contains("target change") || normalized.Contains("stamina break") ||
                normalized.Contains("guard") || normalized.Contains("movement") || normalized.Contains("flight") ||
                normalized.Contains("z-vanish") || normalized.Contains("step") || normalized.Contains("dash"))
            {
                actionCorrelation = true;
            }
            tags.Add($"{action.ActionLabel}: {action.ChangeCount:N0} change(s)");
        }

        ApplyContrastivePenalties(record, scores, tags);

        ClassificationScoreRecord[] ordered = scores
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => FamilyRank(pair.Key))
            .Select(pair => new ClassificationScoreRecord { StatFamily = pair.Key, Score = pair.Value })
            .ToArray();

        record.ClassificationScores = ordered.ToList();
        if (ordered.Length == 0 || ordered[0].Score < 0.80)
        {
            record.StatFamily = "Unknown";
            record.StatRole = InferRole(record, "Unknown");
            record.ClassificationConfidence = 0.10;
            record.ClassificationSource = "Automatic — insufficient labeled evidence";
            record.ClassificationTags = tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            return;
        }

        double total = ordered.Sum(item => item.Score);
        double top = ordered[0].Score;
        double second = ordered.Length > 1 ? ordered[1].Score : 0;
        double separation = top / Math.Max(0.001, top + second);
        double evidenceBonus = Math.Min(0.20,
            record.ActionEvidence.Count(action => action.ChangeCount > 0) * 0.04 +
            record.ExperimentCount * 0.03 +
            record.SessionCount * 0.02);
        double confidence = Math.Clamp(0.25 + separation * 0.50 + evidenceBonus, 0.15, 0.94);
        if (total > 0 && top / total < 0.40)
        {
            confidence -= 0.12;
        }

        record.StatFamily = ordered[0].StatFamily;
        record.StatRole = InferRole(record, record.StatFamily);
        record.ClassificationConfidence = Math.Clamp(confidence, 0.05, 0.94);
        record.ClassificationSource = actionCorrelation
            ? "Automatic — action correlation"
            : labelEvidence
                ? "Automatic — label evidence"
                : shapeEvidence
                    ? "Automatic — value shape"
                    : "Automatic — weak evidence";
        foreach (ClassificationScoreRecord item in ordered.Take(3))
        {
            tags.Add($"{item.StatFamily} score {item.Score:N2}");
        }
        record.ClassificationTags = tags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private static void SetKnownClassification(CandidateRecord record, string family, string role, string tag)
    {
        record.StatFamily = family;
        record.StatRole = role;
        record.ClassificationSource = "Known reference";
        record.ClassificationConfidence = 1.0;
        record.ClassificationScores = [new ClassificationScoreRecord { StatFamily = family, Score = 1.0 }];
        record.ClassificationTags = [tag];
    }

    private static bool ApplyLabelClassification(
        string label,
        Dictionary<string, double> scores,
        List<string> tags)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        bool matched = false;
        string normalized = label.ToLowerInvariant();
        bool basicKiBlast = normalized.Contains("basic ki blast", StringComparison.Ordinal);
        if (basicKiBlast)
        {
            AddScore(scores, "Basic Attack", 2.5);
            tags.Add("Label suggests Basic Attack");
            matched = true;
        }
        (string Token, string Family)[] mappings =
        [
            ("health", "Health"),
            ("stamina", "Stamina"),
            ("basic attack", "Basic Attack"),
            ("strike", "Strike Skills"),
            ("ki blast", "Ki Blast Skills"),
            ("defense", "Defense / Guard"),
            ("guard", "Defense / Guard"),
            ("transform", "Transformation"),
            ("movement", "Movement"),
            ("flight", "Movement"),
            ("timer", "Timers / Cooldowns"),
            ("cooldown", "Timers / Cooldowns"),
            ("flag", "State / Flags"),
            ("pointer", "Object / Pointers"),
            ("identity", "Identity / Metadata")
        ];
        foreach ((string token, string family) in mappings)
        {
            if (basicKiBlast && token == "ki blast")
            {
                continue;
            }
            if (normalized.Contains(token, StringComparison.Ordinal))
            {
                AddScore(scores, family, 2.5);
                tags.Add($"Label suggests {family}");
                matched = true;
            }
        }

        // Plain "Ki" is checked after "Ki blast" so a skill label does not lose its skill family.
        if (normalized.Contains("ki", StringComparison.Ordinal) &&
            !normalized.Contains("ki blast", StringComparison.Ordinal))
        {
            AddScore(scores, "Ki", 2.5);
            tags.Add("Label suggests Ki");
            matched = true;
        }
        return matched;
    }

    private static string InferRole(CandidateRecord record, string family)
    {
        if (family == "Object / Pointers")
        {
            return "Pointer / Reference";
        }
        if (family == "State / Flags")
        {
            return "State / Flag";
        }
        if (family == "Timers / Cooldowns")
        {
            return "Timer / Cooldown";
        }
        if (family == "Identity / Metadata")
        {
            return "Identity / Metadata";
        }

        string label = record.Label.ToLowerInvariant();
        if (label.Contains("maximum", StringComparison.Ordinal) || label.Contains("max ", StringComparison.Ordinal))
        {
            return "Maximum / Capacity";
        }
        if (label.Contains("regen", StringComparison.Ordinal) || label.Contains("recovery", StringComparison.Ordinal))
        {
            return "Regeneration / Recovery";
        }
        if (label.Contains("cost", StringComparison.Ordinal) || label.Contains("consumption", StringComparison.Ordinal))
        {
            return "Cost / Consumption";
        }
        if (label.Contains("multiplier", StringComparison.Ordinal) || label.Contains("scale", StringComparison.Ordinal))
        {
            return "Multiplier / Scaling";
        }

        bool spendDecrease = HasDirectionalAction(record, "spend", decrease: true);
        bool regenIncrease = HasDirectionalAction(record, "regenerate", decrease: false);
        bool gainIncrease = HasDirectionalAction(record, "gain", decrease: false);
        if (family is "Ki" or "Stamina" && (spendDecrease || regenIncrease || gainIncrease))
        {
            return "Current Value";
        }
        if (family == "Health" &&
            (HasDirectionalAction(record, "damage", decrease: true) ||
             HasDirectionalAction(record, "heal", decrease: false) ||
             HasDirectionalAction(record, "recover health", decrease: false)))
        {
            return "Current Value";
        }
        if (family is "Basic Attack" or "Strike Skills" or "Ki Blast Skills")
        {
            return "Damage / Output";
        }
        if (family == "Defense / Guard")
        {
            return IsSmallIntegralRange(record) ? "State / Flag" : "Resistance / Reduction";
        }
        if (family is "Transformation" or "Movement")
        {
            return IsSmallIntegralRange(record) ? "State / Flag" : "Multiplier / Scaling";
        }
        return "Unclassified";
    }

    private static bool HasDirectionalAction(CandidateRecord record, string token, bool decrease) =>
        record.ActionEvidence.Any(action =>
            action.ActionLabel.Contains(token, StringComparison.OrdinalIgnoreCase) &&
            (decrease ? action.DecreaseCount > action.IncreaseCount : action.IncreaseCount > action.DecreaseCount));

    private static bool IsSmallIntegralRange(CandidateRecord record)
    {
        bool integralType = record.ValueType is "Byte" or "Int16" or "UInt16" or "Int32" or "UInt32";
        return integralType && record.ValidValueCount > 0 &&
            record.MinimumValue >= -16 && record.MaximumValue <= 256 &&
            record.MaximumValue - record.MinimumValue <= 64;
    }

    private static double DirectionalActionSignal(ActionEvidenceRecord action, bool expectDecrease)
    {
        long expected = expectDecrease ? action.DecreaseCount : action.IncreaseCount;
        long opposite = expectDecrease ? action.IncreaseCount : action.DecreaseCount;
        if (expected == 0)
        {
            return 0;
        }

        double purity = expected / (double)Math.Max(1, expected + opposite);
        return ActionSignal(action) * Math.Clamp(0.35 + purity * 0.65, 0.35, 1.0);
    }

    private static void ApplyContrastivePenalties(
        CandidateRecord record,
        IDictionary<string, double> scores,
        ICollection<string> tags)
    {
        ApplyContrastivePenalty(record, scores, tags, "Ki",
            label => label.Contains("ki", StringComparison.OrdinalIgnoreCase),
            label => label.Contains("stamina", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("damage", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("heal", StringComparison.OrdinalIgnoreCase));
        ApplyContrastivePenalty(record, scores, tags, "Stamina",
            label => label.Contains("stamina", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("evasive", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("z-vanish", StringComparison.OrdinalIgnoreCase),
            label => label.Contains("ki", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("damage", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("heal", StringComparison.OrdinalIgnoreCase));
        ApplyContrastivePenalty(record, scores, tags, "Health",
            label => label.Contains("damage", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("heal", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("health", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("ko", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("revive", StringComparison.OrdinalIgnoreCase),
            label => label.Contains("spend ki", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("regenerate ki", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("spend stamina", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("regenerate stamina", StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyContrastivePenalty(
        CandidateRecord record,
        IDictionary<string, double> scores,
        ICollection<string> tags,
        string family,
        Func<string, bool> relevant,
        Func<string, bool> unrelated)
    {
        if (!scores.TryGetValue(family, out double score) || score <= 0)
        {
            return;
        }

        long relevantChanges = record.ActionEvidence
            .Where(action => relevant(action.ActionLabel))
            .Sum(action => action.ChangeCount);
        long unrelatedChanges = record.ActionEvidence
            .Where(action => unrelated(action.ActionLabel))
            .Sum(action => action.ChangeCount);
        if (unrelatedChanges == 0)
        {
            return;
        }

        double specificity = relevantChanges / (double)Math.Max(1, relevantChanges + unrelatedChanges);
        double multiplier = Math.Clamp(0.45 + specificity * 0.55, 0.45, 1.0);
        scores[family] = score * multiplier;
        tags.Add($"{family} contrast specificity {specificity:P0}");
    }

    private static double ActionSignal(ActionEvidenceRecord action)
    {
        double changeRate = action.ChangeCount / (double)Math.Max(1, action.ObservationCount);
        double repeatability = Math.Min(1.0,
            action.ExperimentIds.Count * 0.30 + action.SessionIds.Count * 0.35);
        return Math.Log10(action.ChangeCount + 1) * 0.90 + changeRate * 1.40 + repeatability * 0.70;
    }

    private static void AddScore(Dictionary<string, double> scores, string family, double amount)
    {
        scores[family] = scores.TryGetValue(family, out double existing) ? existing + amount : amount;
    }

    private static bool IsKnownCurrentHealth(CandidateRecord record) =>
        record.RegionPath == "Battle_Mob" &&
        record.ObjectOffset == RuntimeProtocol.CurrentHealthOffset &&
        record.ValueType == ScannerValueType.Float32.ToString();

    private static bool IsKnownMaximumHealth(CandidateRecord record) =>
        record.RegionPath == "Battle_Mob" &&
        record.ObjectOffset == RuntimeProtocol.MaximumHealthOffset &&
        record.ValueType == ScannerValueType.Float32.ToString();

    private static string LabelForKnownOffset(string regionPath, uint offset, ScannerValueType valueType)
    {
        if (regionPath == "Battle_Mob" && valueType == ScannerValueType.Float32)
        {
            return offset switch
            {
                RuntimeProtocol.CurrentHealthOffset => "Current health",
                RuntimeProtocol.MaximumHealthOffset => "Maximum health",
                _ => $"Unclassified {valueType}"
            };
        }
        return $"Unclassified {valueType}";
    }

    private static int FamilyRank(string family)
    {
        for (int index = 0; index < CandidateTaxonomy.Families.Count; index++)
        {
            if (string.Equals(CandidateTaxonomy.Families[index], family, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return CandidateTaxonomy.Families.Count;
    }

    private static int StatusRank(string status) => status switch
    {
        "Known" => 0,
        "Solid" => 1,
        "Strong" => 2,
        "Candidate" => 3,
        "Provisional" => 4,
        "Noise" => 5,
        _ => 6
    };

    private static bool HasProtectedValidation(CandidateRecord record) =>
        record.Status == "Known" ||
        CandidateGroupBuilder.ValidationStageRank(record.ValidationStage) >=
        CandidateGroupBuilder.ValidationStageRank(CandidateValidationStages.CodeAnchored);

    private static string PromoteValidationStage(string current, string requested) =>
        CandidateGroupBuilder.ValidationStageRank(requested) > CandidateGroupBuilder.ValidationStageRank(current)
            ? requested
            : current;

    private static bool AddUniqueValidationEvidence(CandidateRecord record, string evidenceId)
    {
        if (record.ValidationEvidenceIds.Contains(evidenceId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        record.ValidationEvidenceIds.Add(evidenceId);
        return true;
    }

    private static void AppendNote(CandidateRecord record, string note)
    {
        record.Notes = string.IsNullOrWhiteSpace(record.Notes) ? note : record.Notes + " " + note;
    }

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }
}
