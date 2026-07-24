namespace PowerScalerLabs.Runtime;

/// <summary>
/// Source-grounded constants for the currently approved DBXV2 1.25.2 telemetry domain.
/// Provider-specific and shared object-layout values live here so access code does not
/// grow independent copies of the same runtime assumptions.
/// </summary>
internal static class ValidatedRuntimeLayout
{
    internal const string SupportedGameVersion = "1.25.2.0";
    internal const ulong PatcherBattleCoreStorageRva = 0x2080C8;
    internal const uint ExpectedPatcherImageSize = 0x394000;
    internal const ulong BattleCoreMobArrayOffset = 0x3A58;
    internal const ulong BattleCoreTailProbeOffset = 0x4A88;
    internal const float MinimumPlausibleMaximumHealth = 1.0f;
}
