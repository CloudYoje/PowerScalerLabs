using System;
using System.Collections.Generic;
using System.Linq;
using PowerScalerLabs.App.Models;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.App.Recording;

/// <summary>
/// Collapses the scanner's typed interpretations into one physical-address hypothesis.
/// Raw CandidateRecord objects remain lossless and are still persisted separately.
/// </summary>
internal static class CandidateGroupBuilder
{
    private const double PairCapacityTolerance = 1.05;

    internal static IReadOnlyList<CandidateGroupRecord> Build(IReadOnlyList<CandidateRecord> records)
    {
        Dictionary<string, CandidateRecord[]> membersByGroup = records
            .GroupBy(PhysicalGroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        List<CandidateGroupRecord> groups = new(membersByGroup.Count);
        foreach ((string groupId, CandidateRecord[] members) in membersByGroup)
        {
            CandidateRecord preferred = members
                .OrderByDescending(TypeHypothesisScore)
                .ThenByDescending(record => record.Confidence)
                .ThenByDescending(record => record.EvidenceCount)
                .ThenBy(record => ValueTypeRank(record.ValueType))
                .First();

            string validationStage = ResolveValidationStage(preferred);
            CandidateGroupRecord group = new()
            {
                GroupId = groupId,
                RegionPath = preferred.RegionPath,
                ObjectOffset = preferred.ObjectOffset,
                PreferredCandidateId = preferred.CandidateId,
                PreferredValueType = preferred.ValueType,
                AlternativeTypes = string.Join(", ", members
                    .Where(record => !string.Equals(record.CandidateId, preferred.CandidateId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(record => ValueTypeRank(record.ValueType))
                    .Select(record => record.ValueType)
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                AlternativeCount = Math.Max(0, members.Length - 1),
                Label = preferred.Label,
                StatFamily = preferred.StatFamily,
                StatRole = preferred.StatRole,
                Status = preferred.Status,
                ValidationStage = validationStage,
                SignalTier = ResolveSignalTier(preferred, validationStage),
                IsKnownEffect = string.Equals(validationStage, CandidateValidationStages.Verified, StringComparison.Ordinal),
                IsExplained = string.Equals(validationStage, CandidateValidationStages.Verified, StringComparison.Ordinal),
                Confidence = preferred.Confidence,
                ClassificationConfidence = preferred.ClassificationConfidence,
                EvidenceCount = preferred.EvidenceCount,
                ChangeCount = preferred.ChangeCount,
                StableCount = preferred.StableCount,
                SessionCount = preferred.SessionCount,
                ExperimentCount = preferred.ExperimentCount,
                DistinctActorCount = preferred.DistinctActorCount,
                DistinctSlotCount = preferred.DistinctSlotCount,
                LastValue = preferred.LastValue,
                MinimumValue = preferred.MinimumValue,
                MaximumValue = preferred.MaximumValue,
                ValueShape = preferred.ValueShape,
                ClassificationSource = preferred.ClassificationSource,
                ClassificationTagsText = preferred.ClassificationTagsText,
                TopActions = preferred.TopActions,
                SlotSummary = preferred.SlotSummary,
                Notes = preferred.Notes
            };
            ApplyKnownReference(group, preferred);
            groups.Add(group);
        }

        ApplyResourcePairHypotheses(groups, membersByGroup);

        return groups
            .OrderBy(group => SignalTierRank(group.SignalTier))
            .ThenBy(group => ValidationStageRank(group.ValidationStage))
            .ThenBy(group => FamilyRank(group.StatFamily))
            .ThenByDescending(group => group.Confidence)
            .ThenByDescending(group => group.ChangeCount)
            .ThenBy(group => group.RegionPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.ObjectOffset)
            .ToArray();
    }

    private static void ApplyKnownReference(CandidateGroupRecord group, CandidateRecord preferred)
    {
        if (!string.Equals(group.RegionPath, "Battle_Mob", StringComparison.Ordinal) ||
            !string.Equals(preferred.ValueType, ScannerValueType.Float32.ToString(), StringComparison.Ordinal))
        {
            return;
        }

        if (group.ObjectOffset == RuntimeProtocol.CurrentHealthOffset)
        {
            SetVerifiedKnownEffect(group, "Current health", "Health", "Current Value", "Known Battle_Mob +0x100 Float32 reference.");
        }
        else if (group.ObjectOffset == RuntimeProtocol.MaximumHealthOffset)
        {
            SetVerifiedKnownEffect(group, "Maximum health", "Health", "Maximum / Capacity", "Known Battle_Mob +0x104 Float32 reference.");
        }
    }

    private static void SetVerifiedKnownEffect(
        CandidateGroupRecord group,
        string label,
        string family,
        string role,
        string note)
    {
        group.Label = label;
        group.StatFamily = family;
        group.StatRole = role;
        group.Status = "Known";
        group.ValidationStage = CandidateValidationStages.Verified;
        group.SignalTier = CandidateSignalTiers.KnownEffect;
        group.IsKnownEffect = true;
        group.IsExplained = true;
        group.Confidence = Math.Max(group.Confidence, 0.99);
        group.ClassificationConfidence = 1.0;
        group.ClassificationSource = "Known reference";
        group.Notes = note;
    }

    private static void ApplyResourcePairHypotheses(
        IReadOnlyList<CandidateGroupRecord> groups,
        IReadOnlyDictionary<string, CandidateRecord[]> membersByGroup)
    {
        Dictionary<(string Region, uint Offset), CandidateGroupRecord> groupByAddress = groups
            .ToDictionary(group => (group.RegionPath, group.ObjectOffset));

        foreach (CandidateGroupRecord currentGroup in groups)
        {
            if (currentGroup.IsKnownEffect ||
                !string.IsNullOrWhiteSpace(currentGroup.PairRelationship) ||
                !groupByAddress.TryGetValue((currentGroup.RegionPath, currentGroup.ObjectOffset + sizeof(float)), out CandidateGroupRecord? capacityGroup) ||
                capacityGroup.IsKnownEffect ||
                !string.IsNullOrWhiteSpace(capacityGroup.PairRelationship) ||
                !TryGetTypedMember(currentGroup, membersByGroup, ScannerValueType.Float32.ToString(), out CandidateRecord current) ||
                !TryGetTypedMember(capacityGroup, membersByGroup, ScannerValueType.Float32.ToString(), out CandidateRecord capacity) ||
                current.ChangeCount < 2)
            {
                continue;
            }

            if (!LooksLikeStableCapacity(current, capacity))
            {
                continue;
            }

            // Battle_Mob already has a verified health current/capacity pair at +0x100/+0x104.
            // Unknown adjacent resource pairs must not be mislabeled as Health merely because Ki can
            // change while taking damage or healing during compound gameplay tests.
            (string Family, double Score, double RunnerUp) inference = InferDirectionalResourceFamily(current, allowHealth: false);
            if (inference.Score < 2.25 || inference.Score < inference.RunnerUp * 1.20)
            {
                continue;
            }

            string family = inference.Family;
            PromotePreferredType(currentGroup, current, membersByGroup[currentGroup.GroupId]);
            PromotePreferredType(capacityGroup, capacity, membersByGroup[capacityGroup.GroupId]);
            currentGroup.StatFamily = family;
            currentGroup.StatRole = "Current Value";
            currentGroup.Label = $"Current {family} candidate";
            currentGroup.ValidationStage = PromoteStage(currentGroup.ValidationStage, CandidateValidationStages.Correlated);
            currentGroup.SignalTier = CandidateSignalTiers.HighConfidence;
            currentGroup.Confidence = Math.Max(currentGroup.Confidence, 0.86);
            currentGroup.ClassificationConfidence = Math.Max(currentGroup.ClassificationConfidence, 0.84);
            currentGroup.ClassificationSource = "Structured pair + directional action evidence";
            currentGroup.PairRelationship = $"Paired with {capacityGroup.OffsetText} as a stable capacity candidate.";
            currentGroup.IsExplained = false;

            capacityGroup.StatFamily = family;
            capacityGroup.StatRole = "Maximum / Capacity";
            capacityGroup.Label = $"Maximum {family} candidate";
            capacityGroup.ValidationStage = PromoteStage(capacityGroup.ValidationStage, CandidateValidationStages.Correlated);
            capacityGroup.SignalTier = CandidateSignalTiers.HighConfidence;
            capacityGroup.Confidence = Math.Max(capacityGroup.Confidence, 0.82);
            capacityGroup.ClassificationConfidence = Math.Max(capacityGroup.ClassificationConfidence, 0.82);
            capacityGroup.ClassificationSource = "Adjacent stable capacity paired with directional current value";
            capacityGroup.PairRelationship = $"Paired with {currentGroup.OffsetText} as its changing current-value candidate.";
            capacityGroup.IsExplained = false;
        }
    }

    private static bool TryGetTypedMember(
        CandidateGroupRecord group,
        IReadOnlyDictionary<string, CandidateRecord[]> membersByGroup,
        string valueType,
        out CandidateRecord record)
    {
        CandidateRecord? found = membersByGroup[group.GroupId].FirstOrDefault(candidate =>
            string.Equals(candidate.ValueType, valueType, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            record = null!;
            return false;
        }

        record = found;
        return true;
    }

    private static void PromotePreferredType(
        CandidateGroupRecord group,
        CandidateRecord preferred,
        IReadOnlyList<CandidateRecord> members)
    {
        group.PreferredCandidateId = preferred.CandidateId;
        group.PreferredValueType = preferred.ValueType;
        group.AlternativeTypes = string.Join(", ", members
            .Where(record => !string.Equals(record.CandidateId, preferred.CandidateId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => ValueTypeRank(record.ValueType))
            .Select(record => record.ValueType)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        group.AlternativeCount = Math.Max(0, members.Count - 1);
        group.EvidenceCount = preferred.EvidenceCount;
        group.ChangeCount = preferred.ChangeCount;
        group.StableCount = preferred.StableCount;
        group.SessionCount = preferred.SessionCount;
        group.ExperimentCount = preferred.ExperimentCount;
        group.DistinctActorCount = preferred.DistinctActorCount;
        group.DistinctSlotCount = preferred.DistinctSlotCount;
        group.LastValue = preferred.LastValue;
        group.MinimumValue = preferred.MinimumValue;
        group.MaximumValue = preferred.MaximumValue;
        group.ValueShape = preferred.ValueShape;
        group.TopActions = preferred.TopActions;
        group.SlotSummary = preferred.SlotSummary;
    }

    private static bool LooksLikeStableCapacity(CandidateRecord current, CandidateRecord capacity)
    {
        if (capacity.ValidValueCount == 0 || capacity.MaximumValue <= 0 || current.ValidValueCount == 0)
        {
            return false;
        }

        bool mostlyStable = capacity.StableCount >= Math.Max(3, capacity.ChangeCount * 4) ||
            (capacity.ChangeCount == 0 && capacity.EvidenceCount >= 4);
        bool currentFitsCapacity = current.MaximumValue <= capacity.MaximumValue * PairCapacityTolerance &&
            current.MinimumValue >= -Math.Max(1.0, capacity.MaximumValue * 0.02);
        bool sensibleRange = capacity.MaximumValue <= 1.0e8 && current.MaximumValue <= 1.0e8;
        return mostlyStable && currentFitsCapacity && sensibleRange;
    }

    private static (string Family, double Score, double RunnerUp) InferDirectionalResourceFamily(
        CandidateRecord record,
        bool allowHealth)
    {
        Dictionary<string, double> scores = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ki"] = 0,
            ["Stamina"] = 0
        };
        if (allowHealth)
        {
            scores["Health"] = 0;
        }

        foreach (ActionEvidenceRecord action in record.ActionEvidence)
        {
            string label = action.ActionLabel.Trim().ToLowerInvariant();
            if (label.Contains("spend ki", StringComparison.Ordinal))
            {
                scores["Ki"] += DirectionEvidence(action, expectDecrease: true);
            }
            else if (label.Contains("regenerate ki", StringComparison.Ordinal) ||
                     label.Contains("gain ki", StringComparison.Ordinal))
            {
                scores["Ki"] += DirectionEvidence(action, expectDecrease: false);
            }
            else if (label.Contains("spend stamina", StringComparison.Ordinal))
            {
                scores["Stamina"] += DirectionEvidence(action, expectDecrease: true);
            }
            else if (label.Contains("regenerate stamina", StringComparison.Ordinal))
            {
                scores["Stamina"] += DirectionEvidence(action, expectDecrease: false);
            }
            else if (allowHealth &&
                     (label.Contains("take damage", StringComparison.Ordinal) ||
                      label.Contains("ko", StringComparison.Ordinal)))
            {
                scores["Health"] += DirectionEvidence(action, expectDecrease: true);
            }
            else if (allowHealth &&
                     (label.Contains("heal", StringComparison.Ordinal) ||
                      label.Contains("recover health", StringComparison.Ordinal) ||
                      label.Contains("revive", StringComparison.Ordinal)))
            {
                scores["Health"] += DirectionEvidence(action, expectDecrease: false);
            }
        }

        KeyValuePair<string, double>[] ordered = scores
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => FamilyRank(pair.Key))
            .ToArray();
        return (ordered[0].Key, ordered[0].Value, ordered.Length > 1 ? ordered[1].Value : 0);
    }

    private static double DirectionEvidence(ActionEvidenceRecord action, bool expectDecrease)
    {
        long expected = expectDecrease ? action.DecreaseCount : action.IncreaseCount;
        long opposite = expectDecrease ? action.IncreaseCount : action.DecreaseCount;
        if (expected == 0)
        {
            return opposite > 0 ? -1.0 : 0;
        }

        double directionPurity = expected / (double)Math.Max(1, expected + opposite);
        double repeatability = Math.Min(1.0,
            action.ExperimentIds.Count * 0.35 + action.SessionIds.Count * 0.40);
        double changeRate = action.ChangeCount / (double)Math.Max(1, action.ObservationCount);
        return Math.Log10(expected + 1) * 1.25 + directionPurity * 1.15 + repeatability * 0.85 + changeRate * 0.50;
    }

    private static double TypeHypothesisScore(CandidateRecord record)
    {
        double score = StatusScore(record.Status) + record.Confidence * 120 + record.ClassificationConfidence * 80;
        score += Math.Min(60, Math.Log10(record.EvidenceCount + 1) * 18);
        score += Math.Min(50, Math.Log10(record.ChangeCount + 1) * 20);

        if (record.ManuallyPromoted) score += 250;
        if (record.ManuallyRejected) score -= 500;
        if (string.Equals(record.ValidationStage, CandidateValidationStages.Verified, StringComparison.Ordinal)) score += 400;
        else if (string.Equals(record.ValidationStage, CandidateValidationStages.CausallyValidated, StringComparison.Ordinal)) score += 300;
        else if (string.Equals(record.ValidationStage, CandidateValidationStages.CodeAnchored, StringComparison.Ordinal)) score += 220;

        if (string.Equals(record.ValueType, ScannerValueType.Float32.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            score += record.ValueShape.Contains("Finite Float32", StringComparison.OrdinalIgnoreCase) ? 90 : 25;
            if (record.ValidValueCount > 0 && record.MinimumValue >= -1.0e8 && record.MaximumValue <= 1.0e8) score += 20;
        }
        else if (string.Equals(record.ValueType, ScannerValueType.Pointer64.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            // A 32-bit integer or Float32 bit pattern is technically canonical when widened to 64 bits,
            // but it is not a credible x64 object address. Do not let that interpretation outrank the
            // numeric type unless the observed range reaches normal 64-bit user-address space.
            bool credible64BitAddress = record.MaximumValue >= 0x1_0000_0000UL &&
                record.MaximumValue <= 0x0000_7FFF_FFFF_FFFFUL;
            if (credible64BitAddress && record.ValueShape.Contains("pointer", StringComparison.OrdinalIgnoreCase))
            {
                score += 75;
                if (record.StatFamily == "Object / Pointers") score += 30;
            }
            else
            {
                score -= 160;
            }
        }
        else if (record.ValueType is "Byte" or "Int16" or "UInt16")
        {
            if (record.MinimumValue >= -16 && record.MaximumValue <= 256) score += 45;
        }
        else if (record.ValueType is "Int32" or "UInt32")
        {
            score += 15;
        }

        if (IsKnownHealthRecord(record)) score += 1000;
        if (record.Status == "Noise") score -= 300;
        return score;
    }

    private static bool IsKnownHealthRecord(CandidateRecord record) =>
        string.Equals(record.RegionPath, "Battle_Mob", StringComparison.Ordinal) &&
        string.Equals(record.ValueType, ScannerValueType.Float32.ToString(), StringComparison.Ordinal) &&
        (record.ObjectOffset == RuntimeProtocol.CurrentHealthOffset ||
         record.ObjectOffset == RuntimeProtocol.MaximumHealthOffset);

    private static string ResolveValidationStage(CandidateRecord record)
    {
        if (IsKnownHealthRecord(record) || record.Status == "Known" ||
            string.Equals(record.ValidationStage, CandidateValidationStages.Verified, StringComparison.Ordinal))
        {
            return CandidateValidationStages.Verified;
        }
        if (record.CausalValidationCount > 0 ||
            string.Equals(record.ValidationStage, CandidateValidationStages.CausallyValidated, StringComparison.Ordinal))
        {
            return CandidateValidationStages.CausallyValidated;
        }
        if (record.CodeAnchorCount > 0 ||
            string.Equals(record.ValidationStage, CandidateValidationStages.CodeAnchored, StringComparison.Ordinal))
        {
            return CandidateValidationStages.CodeAnchored;
        }
        // A statistically repeatable value is not automatically a semantically correlated stat.
        // Correlated is reserved for explicit promotion or structured evidence (resource pairing is
        // applied at the physical-group layer below). This prevents generic Strong records from
        // flooding the focused view as false Health/Ki/Stamina findings.
        if (record.ManuallyPromoted &&
            string.Equals(record.ValidationStage, CandidateValidationStages.Correlated, StringComparison.Ordinal))
        {
            return CandidateValidationStages.Correlated;
        }
        return CandidateValidationStages.Observed;
    }

    private static string ResolveSignalTier(CandidateRecord record, string validationStage)
    {
        if (validationStage == CandidateValidationStages.Verified) return CandidateSignalTiers.KnownEffect;
        if (validationStage is CandidateValidationStages.CausallyValidated or
            CandidateValidationStages.CodeAnchored or
            CandidateValidationStages.Correlated)
        {
            return CandidateSignalTiers.HighConfidence;
        }
        if (record.Status is "Strong" or "Candidate") return CandidateSignalTiers.Promising;
        if (record.Status == "Noise" || record.ManuallyRejected) return CandidateSignalTiers.BackgroundNoise;
        return CandidateSignalTiers.NeedsAnotherTrial;
    }

    private static string PromoteStage(string current, string requested) =>
        ValidationStageRank(requested) < ValidationStageRank(current) ? current : requested;

    private static string PhysicalGroupId(CandidateRecord record) =>
        $"{record.RegionPath}:+0x{record.ObjectOffset:X}";

    private static double StatusScore(string status) => status switch
    {
        "Known" => 800,
        "Solid" => 650,
        "Strong" => 520,
        "Candidate" => 380,
        "Provisional" => 220,
        "Noise" => -250,
        _ => 0
    };

    private static int ValueTypeRank(string valueType) => valueType switch
    {
        "Float32" => 0,
        "Byte" => 1,
        "Int16" => 2,
        "UInt16" => 3,
        "Int32" => 4,
        "UInt32" => 5,
        "Float64" => 6,
        "Int64" => 7,
        "UInt64" => 8,
        "Pointer64" => 9,
        _ => 10
    };

    internal static int SignalTierRank(string tier) => tier switch
    {
        CandidateSignalTiers.KnownEffect => 0,
        CandidateSignalTiers.HighConfidence => 1,
        CandidateSignalTiers.Promising => 2,
        CandidateSignalTiers.NeedsAnotherTrial => 3,
        CandidateSignalTiers.BackgroundNoise => 4,
        _ => 5
    };

    internal static int ValidationStageRank(string stage) => stage switch
    {
        CandidateValidationStages.Observed => 0,
        CandidateValidationStages.Correlated => 1,
        CandidateValidationStages.CodeAnchored => 2,
        CandidateValidationStages.CausallyValidated => 3,
        CandidateValidationStages.Verified => 4,
        _ => 0
    };

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
}
