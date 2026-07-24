using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal static class AddressProvenanceCatalog
{
    internal const string PatcherProviderId = "xv2-patcher-1.25.2-layout";
    internal const string DirectProviderId = "dbxv2-direct-signature-research";
    internal const string FighterLayoutProviderId = "dbxv2-battle-mob-layout-1.25.2";
    internal const string BattleCoreStorageKey = "xv2patcher.battle-core-storage";
    internal const string BattleCoreMobArrayKey = "battle-core.mob-array";
    internal const string FighterVtableKey = "battle-mob.vtable";
    internal const string CurrentHealthKey = "battle-mob.current-health";
    internal const string MaximumHealthKey = "battle-mob.maximum-health";
    internal const string CurrentKiKey = "battle-mob.current-ki-candidate";
    internal const string MaximumKiKey = "battle-mob.maximum-ki-candidate";
    internal const string CurrentStaminaKey = "battle-mob.current-stamina-candidate";
    internal const string MaximumStaminaKey = "battle-mob.maximum-stamina-candidate";

    internal static IReadOnlyList<AddressProvenanceEntry> Entries { get; } =
    [
        new(
            BattleCoreStorageKey,
            "BattleCore locator",
            "xinput1_3.dll",
            "XV2Patcher",
            ValidatedRuntimeLayout.PatcherBattleCoreStorageRva,
            "Module RVA containing a pointer-chain root",
            ScannerValueType.Pointer64,
            8,
            "Validated XV2 Patcher route used to obtain BattleCore candidates.",
            "Historical Chronological Telemetry Gate 1 source-grounded layout.",
            ValidatedRuntimeLayout.SupportedGameVersion,
            PatcherProviderId,
            "Code-anchored",
            "Observer heartbeat after module validation",
            "Provider reports unavailable/unsupported/no-candidate and the runtime fails closed."),
        new(
            BattleCoreMobArrayKey,
            "Fighter acquisition",
            "DBXV2.exe heap object",
            "BattleCore",
            ValidatedRuntimeLayout.BattleCoreMobArrayOffset,
            "Object-relative offset",
            ScannerValueType.Pointer64,
            8,
            "Beginning of the 14-slot Battle_Mob pointer array.",
            "Validated structural observation from the historical runtime branch.",
            ValidatedRuntimeLayout.SupportedGameVersion,
            FighterLayoutProviderId,
            "Code-anchored",
            "Observer heartbeat",
            "Unreadable or structurally invalid slots are released; no guessed pointer is retained."),
        new(
            FighterVtableKey,
            "Fighter identity",
            "DBXV2.exe heap object",
            "Battle_Mob",
            0x000,
            "Object-relative offset",
            ScannerValueType.Pointer64,
            8,
            "Battle_Mob vtable pointer used as structural type evidence.",
            "Validated structural observation from the historical runtime branch.",
            ValidatedRuntimeLayout.SupportedGameVersion,
            FighterLayoutProviderId,
            "Code-anchored",
            "Fighter acquisition and validation",
            "A fighter is rejected unless the vtable is inside DBXV2.exe."),
        Resource(CurrentHealthKey, RuntimeProtocol.CurrentHealthOffset, "Current health", "Verified"),
        Resource(MaximumHealthKey, RuntimeProtocol.MaximumHealthOffset, "Maximum health", "Verified"),
        Resource(CurrentKiKey, RuntimeProtocol.CurrentKiOffset, "Current Ki candidate", "Correlated"),
        Resource(MaximumKiKey, RuntimeProtocol.MaximumKiOffset, "Maximum Ki candidate", "Correlated"),
        Resource(CurrentStaminaKey, RuntimeProtocol.CurrentStaminaOffset, "Current stamina candidate", "Correlated"),
        Resource(MaximumStaminaKey, RuntimeProtocol.MaximumStaminaOffset, "Maximum stamina candidate", "Correlated")
    ];

    internal static string KeyForOffset(uint offset) => offset switch
    {
        RuntimeProtocol.CurrentHealthOffset => CurrentHealthKey,
        RuntimeProtocol.MaximumHealthOffset => MaximumHealthKey,
        RuntimeProtocol.CurrentKiOffset => CurrentKiKey,
        RuntimeProtocol.MaximumKiOffset => MaximumKiKey,
        RuntimeProtocol.CurrentStaminaOffset => CurrentStaminaKey,
        RuntimeProtocol.MaximumStaminaOffset => MaximumStaminaKey,
        _ => $"battle-mob.offset-0x{offset:X}"
    };

    private static AddressProvenanceEntry Resource(string key, uint offset, string meaning, string stage) => new(
        key,
        "Focused telemetry",
        "DBXV2.exe heap object",
        "Battle_Mob",
        offset,
        "Object-relative offset",
        ScannerValueType.Float32,
        4,
        meaning,
        "Historical focused watchlist; semantics remain bounded by the stated validation stage.",
        ValidatedRuntimeLayout.SupportedGameVersion,
        FighterLayoutProviderId,
        stage,
        "25 ms chronology lane plus observer heartbeat for verified health fields",
        "Unreadable samples are counted and omitted; no fallback value is invented.");
}
