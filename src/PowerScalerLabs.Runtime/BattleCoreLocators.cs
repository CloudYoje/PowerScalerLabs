using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal interface IBattleCoreLocator
{
    string ProviderId { get; }
    string DisplayName { get; }
    BattleCoreLocatorResult Locate(GameMemoryReader reader);
}

internal readonly record struct BattleCoreLocatorResult(
    BattleCoreLocatorReport Report,
    ulong SelectedCore);

internal sealed class BattleCoreLocatorCoordinator
{
    private readonly IBattleCoreLocator[] _providers =
    [
        new Xv2PatcherBattleCoreLocator(),
        new DirectDbxv2SignatureResearchLocator()
    ];

    internal BattleCoreResolution Resolve(GameMemoryReader reader)
    {
        List<BattleCoreLocatorReport> reports = [];
        List<(string ProviderId, ulong Core, int Score)> resolved = [];

        foreach (IBattleCoreLocator provider in _providers)
        {
            BattleCoreLocatorResult result;
            try
            {
                result = provider.Locate(reader);
            }
            catch (Exception exception)
            {
                result = new BattleCoreLocatorResult(
                    new BattleCoreLocatorReport(
                        provider.ProviderId,
                        provider.DisplayName,
                        LocatorOutcome.Error,
                        exception.Message,
                        null,
                        0,
                        0,
                        null,
                        -1,
                        []),
                    0);
            }

            reports.Add(result.Report);
            if (result.Report.Outcome == LocatorOutcome.Resolved && result.SelectedCore != 0)
            {
                resolved.Add((provider.ProviderId, result.SelectedCore, result.Report.CandidateScore));
            }
        }

        ulong[] distinctCores = resolved.Select(item => item.Core).Distinct().ToArray();
        if (distinctCores.Length > 1)
        {
            reports.Add(new BattleCoreLocatorReport(
                "coordinator",
                "BattleCore locator coordinator",
                LocatorOutcome.Conflict,
                "Independent providers resolved different BattleCore addresses. The runtime refused to select either address.",
                null,
                0,
                0,
                null,
                -1,
                resolved.Select(item => item.ProviderId).ToArray()));
            return new BattleCoreResolution(0, -1, null, "Provider conflict; failed closed.", reports);
        }

        if (resolved.Count == 0)
        {
            BattleCoreLocatorReport? mostSpecific = reports.FirstOrDefault(report => report.Outcome is LocatorOutcome.Unsupported or LocatorOutcome.NoCandidate);
            return new BattleCoreResolution(
                0,
                -1,
                null,
                mostSpecific?.Detail ?? "No BattleCore provider produced a validated candidate.",
                reports);
        }

        (string providerId, ulong core, int score) = resolved
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.ProviderId, StringComparer.Ordinal)
            .First();
        return new BattleCoreResolution(core, score, providerId, $"Resolved by {providerId}.", reports);
    }
}

internal sealed record BattleCoreResolution(
    ulong SelectedCore,
    int SelectedScore,
    string? ProviderId,
    string Detail,
    IReadOnlyList<BattleCoreLocatorReport> Reports);

internal sealed class Xv2PatcherBattleCoreLocator : IBattleCoreLocator
{

    public string ProviderId => AddressProvenanceCatalog.PatcherProviderId;
    public string DisplayName => "Validated XV2 Patcher 1.25.2 layout";

    public BattleCoreLocatorResult Locate(GameMemoryReader reader)
    {
        RemoteModule? patcher = reader.FindModule("xinput1_3.dll");
        if (patcher is null)
        {
            return Result(LocatorOutcome.Unavailable,
                "XV2 Patcher module xinput1_3.dll is not loaded.", 0, 0, null, -1, 0);
        }

        if (patcher.ImageSize != ValidatedRuntimeLayout.ExpectedPatcherImageSize)
        {
            return Result(LocatorOutcome.Unsupported,
                $"xinput1_3.dll image size is 0x{patcher.ImageSize:X}; expected 0x{ValidatedRuntimeLayout.ExpectedPatcherImageSize:X}.",
                ValidatedRuntimeLayout.ExpectedPatcherImageSize, patcher.ImageSize, null, -1, 0);
        }

        if (!reader.TryReadUInt64(patcher.BaseAddress + ValidatedRuntimeLayout.PatcherBattleCoreStorageRva, out ulong storageAddress) ||
            storageAddress == 0)
        {
            return Result(LocatorOutcome.NoCandidate,
                "The validated patcher storage RVA was readable but did not yield a nonzero root.",
                ValidatedRuntimeLayout.ExpectedPatcherImageSize, patcher.ImageSize, null, -1, 0);
        }

        reader.TryReadUInt64(storageAddress, out ulong firstPointer);
        ulong secondPointer = 0;
        if (firstPointer != 0)
        {
            reader.TryReadUInt64(firstPointer, out secondPointer);
        }

        (ulong Core, int Score)[] candidates =
        [
            (firstPointer, ScoreCoreCandidate(reader, firstPointer)),
            (secondPointer, ScoreCoreCandidate(reader, secondPointer)),
            (storageAddress, ScoreCoreCandidate(reader, storageAddress))
        ];

        (ulong Core, int Score) selected = candidates
            .Where(candidate => candidate.Core != 0 && candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Core == firstPointer ? 0 : candidate.Core == secondPointer ? 1 : 2)
            .FirstOrDefault();

        if (selected.Core == 0 || selected.Score < 0)
        {
            return Result(LocatorOutcome.NoCandidate,
                "The patcher root produced no structurally valid BattleCore candidate.",
                ValidatedRuntimeLayout.ExpectedPatcherImageSize, patcher.ImageSize, null, -1, 0);
        }

        return Result(LocatorOutcome.Resolved,
            $"Structurally validated BattleCore candidate 0x{selected.Core:X16}.",
            ValidatedRuntimeLayout.ExpectedPatcherImageSize, patcher.ImageSize, selected.Core, selected.Score, selected.Core);
    }

    private BattleCoreLocatorResult Result(
        LocatorOutcome outcome,
        string detail,
        uint requiredSize,
        uint observedSize,
        ulong? candidate,
        int score,
        ulong selectedCore) =>
        new(
            new BattleCoreLocatorReport(
                ProviderId,
                DisplayName,
                outcome,
                detail,
                "xinput1_3.dll",
                requiredSize,
                observedSize,
                candidate,
                score,
                [AddressProvenanceCatalog.BattleCoreStorageKey, AddressProvenanceCatalog.BattleCoreMobArrayKey]),
            selectedCore);

    private static int ScoreCoreCandidate(GameMemoryReader reader, ulong core)
    {
        if (!reader.IsPrivateWritableObject(core) ||
            !reader.IsReadableRange(core + ValidatedRuntimeLayout.BattleCoreMobArrayOffset,
                checked((ulong)(RuntimeProtocol.ObservedFighterSlotCount * sizeof(ulong)))) ||
            !reader.IsReadableRange(core + ValidatedRuntimeLayout.BattleCoreTailProbeOffset, sizeof(ulong)) ||
            !reader.TryReadUInt64(core, out ulong coreVtable) ||
            !reader.IsGameImageAddress(coreVtable))
        {
            return -1000;
        }

        int score = 0;
        for (int slot = 0; slot < RuntimeProtocol.ObservedFighterSlotCount; slot++)
        {
            ulong slotAddress = core + ValidatedRuntimeLayout.BattleCoreMobArrayOffset + checked((ulong)(slot * sizeof(ulong)));
            if (!reader.TryReadUInt64(slotAddress, out ulong mob))
            {
                return -1000;
            }
            if (mob == 0)
            {
                continue;
            }
            if (!TryValidateMob(reader, mob))
            {
                return -800;
            }
            score += 10;
        }
        return score;
    }

    private static bool TryValidateMob(GameMemoryReader reader, ulong mob)
    {
        if (!reader.IsPrivateWritableObject(mob) ||
            !reader.TryReadUInt64(mob, out ulong vtable) ||
            !reader.IsGameImageAddress(vtable) ||
            !reader.TryReadSingle(mob + RuntimeProtocol.CurrentHealthOffset, out float currentHealth) ||
            !reader.TryReadSingle(mob + RuntimeProtocol.MaximumHealthOffset, out float maximumHealth))
        {
            return false;
        }

        return float.IsFinite(currentHealth) && float.IsFinite(maximumHealth) &&
            maximumHealth >= ValidatedRuntimeLayout.MinimumPlausibleMaximumHealth && currentHealth >= -1.0f &&
            (currentHealth <= maximumHealth * 8.0f || currentHealth <= 1000.0f);
    }
}

internal sealed class DirectDbxv2SignatureResearchLocator : IBattleCoreLocator
{
    public string ProviderId => AddressProvenanceCatalog.DirectProviderId;
    public string DisplayName => "Direct DBXV2 signature provider (research placeholder)";

    public BattleCoreLocatorResult Locate(GameMemoryReader reader) => new(
        new BattleCoreLocatorReport(
            ProviderId,
            DisplayName,
            LocatorOutcome.Unavailable,
            "No direct DBXV2 signature has been approved. This provider performs no scan and cannot select an address.",
            "DBXV2.exe",
            0,
            reader.GameModule.ImageSize,
            null,
            -1,
            []),
        0);
}
