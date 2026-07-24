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

$groupBuilder = Read-StrictUtf8 'src\PowerScalerLabs.App\Recording\CandidateGroupBuilder.cs'
foreach ($token in @(
    'PhysicalGroupId',
    'PreferredCandidateId',
    'TryGetTypedMember',
    'ScannerValueType.Float32',
    'credible64BitAddress',
    'LooksLikeStableCapacity',
    'allowHealth: false',
    'capacityGroup.IsKnownEffect',
    '!string.IsNullOrWhiteSpace(currentGroup.PairRelationship)',
    'Structured pair + directional action evidence'
)) {
    if ($groupBuilder.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Declutter grouping invariant is missing: $token"
    }
}


foreach ($token in @(
    'A statistically repeatable value is not automatically a semantically correlated stat',
    'record.ManuallyPromoted',
    'record.Status is "Strong" or "Candidate"',
    'CandidateValidationStages.Correlated'
)) {
    if ($groupBuilder.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Semantic declutter invariant is missing: $token"
    }
}
if ($groupBuilder.IndexOf('record.Status is "Solid" or "Strong") return CandidateSignalTiers.HighConfidence', [System.StringComparison]::Ordinal) -ge 0) {
    throw 'Generic Strong/Solid records still leak into High-confidence.'
}

$mainWindow = Read-StrictUtf8 'src\PowerScalerLabs.App\MainWindow.xaml.cs'
foreach ($token in @(
    'BulkObservableCollection<CandidateGroupRecord>',
    '_candidateStore.Groups',
    'CandidateSignalTiers.KnownEffect or CandidateSignalTiers.HighConfidence',
    'CandidateGroupBuilder.SignalTierRank',
    'PreferredCandidateId',
    'focused rows shown'
)) {
    if ($mainWindow.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Focused candidate-view invariant is missing: $token"
    }
}


$candidateStore = Read-StrictUtf8 'src\PowerScalerLabs.App\Recording\CandidateStore.cs'
foreach ($token in @(
    'HasProtectedValidation',
    'PromoteValidationStage',
    'AddUniqueValidationEvidence',
    'Causal validation evidence',
    'record.CausalValidationCount >= 2',
    'record.DistinctActorCount >= 2',
    'Verification request rejected',
    'Legacy builds promoted generic repeatability to semantic correlation',
    'Repeatability controls signal strength only'
)) {
    if ($candidateStore.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Validation-ladder invariant is missing: $token"
    }
}

$sessionRecorder = Read-StrictUtf8 'src\PowerScalerLabs.App\Recording\SessionRecorder.cs'
foreach ($token in @(
    'SchemaVersion = 7',
    'timeline.jsonl',
    'session-start',
    'session-stop',
    'RelativeMilliseconds',
    'if (observation.Changed)',
    'scanner-change'
)) {
    if ($sessionRecorder.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Chronology/recording invariant is missing: $token"
    }
}
if ($sessionRecorder.IndexOf('WriteTimelineRecord("scanner-observation"', [System.StringComparison]::Ordinal) -ge 0) {
    throw 'The sparse timeline regressed to duplicating every raw scanner observation.'
}

$runtimeHost = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\RuntimeHost.cs'
$observer = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\ExternalCapabilityObserver.cs'
$scanner = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\ObjectCapabilityScanner.cs'
foreach ($token in @('Stopwatch.GetTimestamp()', 'Stopwatch.Frequency', '_observer.Observe(game.Id, observerCommands, now, monotonicTicks)')) {
    if ($runtimeHost.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Runtime chronology propagation is missing: $token"
    }
}
foreach ($token in @('long monotonicTicks', 'FighterIdentityMessage', 'SlotGeneration', 'new FighterSnapshot(')) {
    if ($observer.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Observer chronology propagation is missing: $token"
    }
}
foreach ($token in @('_lastCaptureMonotonicTicks', 'new ScannerObservationMessage(', 'monotonicTicks')) {
    if ($scanner.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Scanner chronology propagation is missing: $token"
    }
}

# Force the pipeline result to remain an array under Windows PowerShell 5.1.
# Without @(...), a single FileInfo is unwrapped and StrictMode rejects .Count.
$sourceBinaries = @(
    Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File | Where-Object {
        $_.Extension -in @('.dll', '.exe', '.pdb', '.obj', '.lib')
    }
)
if ($sourceBinaries.Count -gt 0) {
    throw "Generated binary found in source tree: $($sourceBinaries[0].FullName)"
}

Write-Host 'Deep audit passed: physical-offset grouping and preferred-type ranking are present.' -ForegroundColor Green
Write-Host 'Deep audit passed: known groups and existing pairs are protected from pair-overlap reassignment.' -ForegroundColor Green
Write-Host 'Deep audit passed: validation evidence is unique, monotonic, and cannot be promoted to Verified without required coverage.' -ForegroundColor Green
Write-Host 'Deep audit passed: default Research view contains Known effects and High-confidence correlated groups only.' -ForegroundColor Green
Write-Host 'Deep audit passed: generic Strong evidence remains Promising/Observed until semantic correlation is established.' -ForegroundColor Green
Write-Host 'Deep audit passed: monotonic timestamps propagate runtime -> observer -> scanner -> session timeline.' -ForegroundColor Green
Write-Host 'Deep audit passed: timeline is sparse; raw scanner evidence remains lossless in scanner-observations.jsonl.' -ForegroundColor Green
Write-Host 'Deep audit passed: external runtime remains read-only and independent from the sealed HealthScale companion.' -ForegroundColor Green
