[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Verify-PowerScalerLabs.ps1')

$utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false, $true
function Read-StrictUtf8 {
    param([Parameter(Mandatory)][string]$RelativePath)
    return [System.IO.File]::ReadAllText((Join-Path $root $RelativePath), $utf8)
}

$protocol = Read-StrictUtf8 'src\PowerScalerLabs.Protocol\RuntimeProtocol.cs'
foreach ($token in @(
    'ProtocolVersion = 7',
    'FighterIdentityMessage',
    'RawMemoryObservationMessage',
    'AddressProvenanceEntry',
    'BattleCoreLocatorReport',
    'MemoryAccessMetricsMessage',
    'ComparisonPolicyMessage',
    'RuntimeAccessStatusMessage',
    'FighterIdentityKey',
    'FighterSlotGeneration'
)) {
    if ($protocol.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Runtime-access protocol invariant is missing: $token"
    }
}

$locators = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\BattleCoreLocators.cs'
foreach ($token in @(
    'interface IBattleCoreLocator',
    'BattleCoreLocatorCoordinator',
    'Xv2PatcherBattleCoreLocator',
    'DirectDbxv2SignatureResearchLocator',
    'Provider conflict; failed closed.',
    'LocatorOutcome.Conflict',
    'No direct DBXV2 signature has been approved',
    'ValidatedRuntimeLayout.PatcherBattleCoreStorageRva',
    'ValidatedRuntimeLayout.BattleCoreMobArrayOffset'
)) {
    if ($locators.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "BattleCore provider invariant is missing: $token"
    }
}


$layout = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\ValidatedRuntimeLayout.cs'
foreach ($token in @(
    'SupportedGameVersion = "1.25.2.0"',
    'PatcherBattleCoreStorageRva = 0x2080C8',
    'ExpectedPatcherImageSize = 0x394000',
    'BattleCoreMobArrayOffset = 0x3A58',
    'BattleCoreTailProbeOffset = 0x4A88',
    'MinimumPlausibleMaximumHealth = 1.0f'
)) {
    if ($layout.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Validated runtime-layout invariant is missing: $token"
    }
}

$observer = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\ExternalCapabilityObserver.cs'
foreach ($forbidden in @('PatcherBattleCoreStorageRva', 'ExpectedPatcherImageSize', 'ResolveBattleCore(RemoteModule')) {
    if ($observer.IndexOf($forbidden, [System.StringComparison]::Ordinal) -ge 0) {
        throw "The observer still owns a locator-specific implementation detail: $forbidden"
    }
}
foreach ($token in @(
    'BattleCoreLocatorCoordinator',
    'FighterIdentityMessage',
    'SlotGeneration',
    '_processInstanceId',
    'RawMemoryObservationMessage',
    'TelemetryComparisonPolicy.Changed',
    'SnapshotMetrics("observer")',
    'ActiveLocatorRefreshHeartbeats',
    'ResolveBattleCore(_reader)',
    'pointerIdentityKey'
)) {
    if ($observer.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Renewed observer invariant is missing: $token"
    }
}
if ($observer.IndexOf('Math.Max(1.0f', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The one-point compressed-scale dead zone returned to the observer.'
}

$provenance = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\AddressProvenanceCatalog.cs'
foreach ($token in @(
    'BattleCoreStorageKey',
    'BattleCoreMobArrayKey',
    'CurrentHealthKey',
    'MaximumHealthKey',
    'CurrentKiKey',
    'MaximumKiKey',
    'CurrentStaminaKey',
    'MaximumStaminaKey',
    'FighterLayoutProviderId',
    'Unreadable samples are counted and omitted'
)) {
    if ($provenance.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Address provenance invariant is missing: $token"
    }
}

$comparison = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\TelemetryComparisonPolicy.cs'
foreach ($token in @(
    'compressed-scale-v1',
    'AbsoluteTolerance = 1.0e-6',
    'RelativeTolerance = 1.0e-6',
    'Chronology uses exact raw-bit equality'
)) {
    if ($comparison.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Comparison-policy invariant is missing: $token"
    }
}

$reader = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\GameMemoryReader.cs'
foreach ($token in @(
    '_readRequests',
    'ReadProcessMemoryCalls',
    '_requestedBytes',
    '_completedBytes',
    '_failedReadCalls',
    '_rejectedReadRequests',
    '_virtualQueryCalls',
    'SnapshotMetrics'
)) {
    if ($reader.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Read-budget metric invariant is missing: $token"
    }
}

$sampler = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\ChronologySampler.cs'
foreach ($token in @(
    'fighter.Identity.IdentityKey',
    'fighter.Identity.SlotGeneration',
    'pendingSample.Fighter.IdentityKey',
    'pendingSample.Fighter.SlotGeneration',
    'SnapshotMetrics("chronology")'
)) {
    if ($sampler.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Chronology identity/budget invariant is missing: $token"
    }
}

$recorder = Read-StrictUtf8 'src\PowerScalerLabs.App\Recording\SessionRecorder.cs'
foreach ($token in @(
    'SchemaVersion = 7',
    'raw-memory-observations.jsonl',
    'runtime-access-architecture.json',
    'RawMemoryObservationCount',
    'ObserverReadProcessMemoryCalls',
    'ChronologyReadProcessMemoryCalls',
    'FighterIdentityKey',
    'FighterSlotGeneration'
)) {
    if ($recorder.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Runtime-access recording invariant is missing: $token"
    }
}

$runtimeSources = (Get-ChildItem -LiteralPath (Join-Path $root 'src\PowerScalerLabs.Runtime') -Filter '*.cs' -File |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName, $utf8) }) -join "`n"
foreach ($forbidden in @('WriteProcessMemory', 'VirtualAllocEx', 'CreateRemoteThread', 'SetWindowsHookEx', 'Detours', 'MinHook')) {
    if ($runtimeSources.IndexOf($forbidden, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden runtime-access token found: $forbidden"
    }
}

Write-Host 'Runtime Access Architecture Gate 0 deep audit passed.' -ForegroundColor Green
Write-Host 'BattleCore resolution is provider-based and conflicts fail closed.'
Write-Host 'Fighter identity includes process, battle, slot, and acquisition generation.'
Write-Host 'Raw observations, address provenance, comparison policy, and read budgets are persisted.'
Write-Host 'Boundary remains external query + VM-read only; no hooks, injection, allocation, remote threads, or game writes.'
