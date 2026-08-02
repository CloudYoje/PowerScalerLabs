namespace PowerScalerLabs.Runtime;

/// <summary>
/// Source-grounded constants used by the structurally validated DBXV2 telemetry route.
/// Provider-specific and shared object-layout values live here so access code does not
/// grow independent copies of the same runtime assumptions.
/// </summary>
internal static class ValidatedRuntimeLayout
{
    internal const ulong PatcherBattleCoreStorageRva = 0x2080C8;
    internal const ulong BattleCoreMobArrayOffset = 0x3A58;
    internal const ulong BattleCoreTailProbeOffset = 0x4A88;
    internal const float MinimumPlausibleMaximumHealth = 1.0f;
}
