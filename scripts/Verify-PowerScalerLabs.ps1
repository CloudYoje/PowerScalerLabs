[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:Utf8Strict = New-Object System.Text.UTF8Encoding -ArgumentList $false, $true

function Read-Utf8Text {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.File]::ReadAllText($Path, $script:Utf8Strict)
}

function Read-Utf8Lines {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.File]::ReadAllLines($Path, $script:Utf8Strict)
}

$root = Split-Path -Parent $PSScriptRoot
$required = @(
    'PowerScalerLabs.sln',
    'README.md',
    'CAPABILITY_SCANNER_TEST.md',
    'DEEP_AUDIT_REPORT.md',
    'DECLUTTER_CHRONOLOGY_AUDIT.md',
    'SCANNER_DECLUTTER_GATE2_AUDIT.md',
    'SCANNER_DECLUTTER_GATE2_DEEP_AUDIT.txt',
    'CHRONOLOGICAL_TELEMETRY_AUDIT.md',
    'CHRONOLOGICAL_TELEMETRY_TEST.md',
    'CHRONOLOGICAL_TELEMETRY_DEEP_AUDIT.txt',
    'GUIDED_OVERLAY_TEST.md',
    'GUIDED_OVERLAY_AUDIT.md',
    'HEALTHSCALE_COMPANION_AUDIT.md',
    'HEALTHSCALE_COMPANION_TEST.md',
    'RUNTIME_ACCESS_ARCHITECTURE_GATE0_AUDIT.md',
    'RUNTIME_ACCESS_ARCHITECTURE_GATE0_TEST.md',
    'RUNTIME_ACCESS_ARCHITECTURE_GATE0_DEEP_AUDIT.txt',
    'RUNTIME_ACCESS_ARCHITECTURE_GATE0_PUBLISH_HOTFIX1_AUDIT.md',
    'RUNTIME_ACCESS_ARCHITECTURE_GATE0_PUBLISH_HOTFIX2_AUDIT.md',
    'RUNTIME_ACCESS_ARCHITECTURE_GATE0_BUILD_HOTFIX1_AUDIT.md',
    'TEST_RUNTIME_ACCESS_ARCHITECTURE.cmd',
    'VALIDATION_REPORT.txt',
    'BUILD_ID.txt',
    'scripts\Import-PreviousData.ps1',
    'scripts\Deep-Audit-DeclutterChronology.ps1',
    'scripts\Deep-Audit-ChronologicalTelemetry.ps1',
    'scripts\Deep-Audit-HealthScaleCompanion.ps1',
    'scripts\Deep-Audit-RuntimeAccessArchitecture.ps1',
    'IMPORT_PREVIOUS_DATA.cmd',
    'OPEN_DATA.cmd',
    'global.json',
    'Directory.Build.props',
    'src\PowerScalerLabs.Protocol\PowerScalerLabs.Protocol.csproj',
    'src\PowerScalerLabs.Protocol\RuntimeProtocol.cs',
    'src\PowerScalerLabs.Runtime\PowerScalerLabs.Runtime.csproj',
    'src\PowerScalerLabs.Runtime\Program.cs',
    'src\PowerScalerLabs.Runtime\RuntimeHost.cs',
    'src\PowerScalerLabs.Runtime\NativeMethods.cs',
    'src\PowerScalerLabs.Runtime\GameMemoryReader.cs',
    'src\PowerScalerLabs.Runtime\AddressProvenanceCatalog.cs',
    'src\PowerScalerLabs.Runtime\BattleCoreLocators.cs',
    'src\PowerScalerLabs.Runtime\TelemetryComparisonPolicy.cs',
    'src\PowerScalerLabs.Runtime\RuntimeArchitectureSelfTest.cs',
    'src\PowerScalerLabs.Runtime\ValidatedRuntimeLayout.cs',
    'src\PowerScalerLabs.Runtime\ExternalCapabilityObserver.cs',
    'src\PowerScalerLabs.Runtime\ObjectCapabilityScanner.cs',
    'src\PowerScalerLabs.Runtime\ChronologySampler.cs',
    'src\PowerScalerLabs.Runtime\RuntimeLog.cs',
    'src\PowerScalerLabs.App\PowerScalerLabs.App.csproj',
    'src\PowerScalerLabs.App\App.xaml',
    'src\PowerScalerLabs.App\App.xaml.cs',
    'src\PowerScalerLabs.App\MainWindow.xaml',
    'src\PowerScalerLabs.App\MainWindow.xaml.cs',
    'src\PowerScalerLabs.App\Models\TelemetryViewModels.cs',
    'src\PowerScalerLabs.App\Companions\HealthScaleCompanionManager.cs',
    'src\PowerScalerLabs.App\Overlay\ExperimentCatalog.cs',
    'src\PowerScalerLabs.App\Overlay\OverlayViewState.cs',
    'src\PowerScalerLabs.App\Overlay\GlobalHotKey.cs',
    'src\PowerScalerLabs.App\Overlay\ExperimentOverlayWindow.xaml',
    'src\PowerScalerLabs.App\Overlay\ExperimentOverlayWindow.xaml.cs',
    'src\PowerScalerLabs.App\Recording\SessionRecorder.cs',
    'src\PowerScalerLabs.App\Recording\CandidateStore.cs',
    'src\PowerScalerLabs.App\Recording\CandidateGroupBuilder.cs',
    'src\PowerScalerLabs.App\Assets\PowerScaler.ico',
    'src\PowerScalerLabs.App\Assets\PowerScalerIcon.png',
    'companions\HealthScale\companion-manifest.json',
    'companions\HealthScale\README_COMPANION.md',
    'companions\HealthScale\UPSTREAM_SOURCE_SHA256SUMS.txt',
    'companions\HealthScale\Source\HealthScale.sln',
    'companions\HealthScale\Source\src\native\HealthScale.Runtime\HealthScale.Runtime.vcxproj',
    'companions\HealthScale\Source\src\native\HealthScale.Runtime\HealthScale.ini'
)

foreach ($relativePath in $required) {
    $fullPath = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required file is missing: $relativePath"
    }
}

$manifestPath = Join-Path $root 'PACKAGE_MANIFEST.sha256'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'PACKAGE_MANIFEST.sha256 is missing.'
}
foreach ($line in (Read-Utf8Lines -Path $manifestPath)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([0-9a-fA-F]{64}) \*(.+)$') {
        throw "Malformed package-manifest line: $line"
    }
    $expectedHash = $Matches[1].ToLowerInvariant()
    $relativePath = $Matches[2].Replace('/', [IO.Path]::DirectorySeparatorChar)
    $fullPath = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Manifest file is missing: $relativePath"
    }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Manifest hash mismatch: $relativePath"
    }
}

$xmlFiles = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File | Where-Object {
    $_.Extension -in @('.csproj', '.xaml', '.manifest')
}
foreach ($file in $xmlFiles) {
    [void][xml](Read-Utf8Text -Path $file.FullName)
}

$runtimeText = (Get-ChildItem -LiteralPath (Join-Path $root 'src\PowerScalerLabs.Runtime') -Filter '*.cs' -File |
    ForEach-Object { Read-Utf8Text -Path $_.FullName }) -join "`n"

$bannedRuntimeTokens = @(
    'WriteProcessMemory',
    'VirtualAllocEx',
    'CreateRemoteThread',
    'NtWriteVirtualMemory',
    'PROCESS_ALL_ACCESS',
    'ProcessVmWrite',
    'ProcessCreateThread',
    'SetWindowsHookEx',
    'xinput_other.dll'
)
foreach ($token in $bannedRuntimeTokens) {
    if ($runtimeText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden runtime token found: $token"
    }
}

$requiredRuntimeTokens = @(
    'ReadProcessMemory',
    'VirtualQueryEx',
    'ProcessVmRead',
    'ProcessQueryLimitedInformation',
    'xinput1_3.dll',
    '0x2080C8',
    '0x3A58',
    'ObjectCapabilityScanner',
    'capture_baseline',
    'compare_after',
    'capture_full_snapshot',
    'ContinuousDelta',
    'FollowPointers',
    'MaximumChildObjects',
    'MaximumCompleteCaptureObservations',
    'MaximumContinuousObservations',
    'EstimateObservationCount',
    'CanCaptureComplete',
    'PipeOptions.CurrentUserOnly',
    'UnsupportedGameBuild',
    'SupportedGameVersion = "1.25.2.0"',
    'ChronologySampler',
    'TryReadKnownReadable',
    'configure_chronology',
    'reset_chronology',
    'new_chronology_epoch',
    'MaximumChronologyBatch'
)
foreach ($token in $requiredRuntimeTokens) {
    if ($runtimeText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required runtime-architecture token is missing: $token"
    }
}

$protocolText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.Protocol\RuntimeProtocol.cs')
$protocolRequirements = @(
    'ProtocolVersion = 7',
    'ObservedFighterSlotCount = 14',
    'CurrentHealthOffset = 0x100',
    'MaximumHealthOffset = 0x104',
    'CurrentKiOffset = 0x10C',
    'MaximumKiOffset = 0x110',
    'CurrentStaminaOffset = 0x16C',
    'MaximumStaminaOffset = 0x170',
    'ScannerConfiguration',
    'ScannerObservationMessage',
    'ScannerStatusMessage',
    'ChronologyConfiguration',
    'ChronologyWatchTarget',
    'ChronologySampleMessage',
    'ChronologyStatusMessage',
    'EpochInitialSampleCount',
    'EpochPollCount',
    'InvalidatedSampleCount',
    'MonotonicTicks',
    'MonotonicFrequency',
    'Float32',
    'Int32',
    'UInt32',
    'Pointer64',
    'FighterIdentityMessage',
    'RawMemoryObservationMessage',
    'AddressProvenanceEntry',
    'BattleCoreLocatorReport',
    'MemoryAccessMetricsMessage',
    'ComparisonPolicyMessage',
    'RuntimeAccessStatusMessage'
)
foreach ($token in $protocolRequirements) {
    if ($protocolText.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Protocol requirement is missing: $token"
    }
}

$appText = (Get-ChildItem -LiteralPath (Join-Path $root 'src\PowerScalerLabs.App') -Recurse -Filter '*.cs' -File |
    ForEach-Object { Read-Utf8Text -Path $_.FullName }) -join "`n"
$appRequirements = @(
    'scanner-observations.jsonl',
    'frames.jsonl',
    'events.jsonl',
    'chronology-samples.jsonl',
    'chronology-watchlist.json',
    'session.json',
    'candidates.json',
    'findings.json',
    'ObserveScanner',
    'ActionEvidenceRecord',
    'PromoteToSolid',
    'RejectAsNoise',
    'AssignLabel',
    'AssignClassification',
    'RestoreAutomaticClassification',
    'CandidateTaxonomy',
    'ClassificationScores',
    'ValueShape',
    'LocalApplicationData',
    'classification-index.json',
    'ByStat',
    'ByRole',
    'ByStatus',
    'ByTier',
    'ByValidation',
    'physical-groups.json',
    'RecordCodeAnchor',
    'RecordCausalValidation',
    'MarkVerified',
    'SlotEvidenceRecord',
    'ActionSlotEvidenceRecord',
    'BulkObservableCollection',
    'AutoFlush = false',
    'HealthScalerBoundaryPreserved',
    'ReadOnlyExternalRuntime',
    'ExperimentOverlayWindow',
    'CaptureGuidedBaselineAsync',
    'CompareGuidedResultsAsync',
    'RepeatGuidedTestAsync',
    'CancelGuidedTestAsync',
    'GlobalHotKey.Register',
    'Key.F11',
    'ChronologyRows',
    'ApplyChronologyStatus',
    'StartGuidedRecordingAsync',
    'new_chronology_epoch',
    'WaitForChronologyStateAsync',
    'WaitForRuntimeQueuesAsync',
    'HealthScaleCompanionManager',
    'InstallOrAdopt',
    'HealthScaleUninstallResult',
    'InstalledDllHash',
    'ConfigurationCreatedByManager'
)
foreach ($token in $appRequirements) {
    if ($appText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Recording or candidate-retention requirement is missing: $token"
    }
}

$mainWindowXaml = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.App\MainWindow.xaml')
$layoutRequirements = @(
    'Width="880"',
    'Height="590"',
    'MinWidth="700"',
    'MinHeight="460"',
    'WindowStartupLocation="Manual"',
    'Capability Scanner',
    'Capture Baseline',
    'Compare After Action',
    'Full Snapshot',
    'Candidates &amp; Findings',
    'Follow object pointers (bounded depth)',
    'All stat families',
    'Assign Class',
    'Auto Classify',
    'Preferred type',
    'All validation stages',
    'Research view',
    'Open Test Overlay',
    'Open Guided Overlay',
    'Chronological changes',
    'ChronologyStatusText',
    'ChronologyMetricsText',
    'Companion Apps',
    'HealthScale 1.1.1',
    'Install / Adopt',
    'Boundary guarantees'
)
foreach ($requirement in $layoutRequirements) {
    if ($mainWindowXaml.IndexOf($requirement, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Capability Scanner layout requirement is missing: $requirement"
    }
}

$handlerMatches = [regex]::Matches($mainWindowXaml, '(?:Click|Loaded|Closing|SourceInitialized|TextChanged|SelectionChanged)="([A-Za-z_][A-Za-z0-9_]*)"')
$codeBehind = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.App\MainWindow.xaml.cs')
foreach ($match in $handlerMatches) {
    $handler = $match.Groups[1].Value
    if ($codeBehind.IndexOf($handler + '(', [System.StringComparison]::Ordinal) -lt 0) {
        throw "XAML event handler is missing from code-behind: $handler"
    }
}


$overlayXamlPath = Join-Path $root 'src\PowerScalerLabs.App\Overlay\ExperimentOverlayWindow.xaml'
$overlayCodePath = Join-Path $root 'src\PowerScalerLabs.App\Overlay\ExperimentOverlayWindow.xaml.cs'
$overlayXaml = Read-Utf8Text -Path $overlayXamlPath
$overlayCode = Read-Utf8Text -Path $overlayCodePath
$overlayRequirements = @(
    'Topmost="True"',
    'CategoryListBox',
    'TestListBox',
    'Capture Baseline',
    'Compare Results',
    'Repeat Test',
    'Full Snapshot',
    'Cancel Test',
    'Start Recording',
    'CHANGED',
    'STABLE',
    'PENDING',
    'Mouse: click any test',
    'Enter confirm',
    'Esc/Backspace go back or cancel',
    'F11 hide/show'
)
foreach ($requirement in $overlayRequirements) {
    if ($overlayXaml.IndexOf($requirement, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Guided overlay layout requirement is missing: $requirement"
    }
}

$overlayHandlerMatches = [regex]::Matches($overlayXaml, '(?:Click|Loaded|PreviewKeyDown|SelectionChanged|MouseLeftButtonDown)="([A-Za-z_][A-Za-z0-9_]*)"')
foreach ($match in $overlayHandlerMatches) {
    $handler = $match.Groups[1].Value
    if ($overlayCode.IndexOf($handler + '(', [System.StringComparison]::Ordinal) -lt 0) {
        throw "Guided overlay XAML event handler is missing: $handler"
    }
}

foreach ($token in @('Key.Left', 'Key.Right', 'Key.Up', 'Key.Down', 'Key.Enter', 'Key.Escape', 'Key.Back', 'CategoryListBox.Focus', 'TestListBox.Focus', 'CategoryListBox.IsEnabled', 'TestListBox.IsEnabled', 'Button.ClickEvent')) {
    if ($overlayCode.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Guided overlay keyboard-navigation requirement is missing: $token"
    }
}
if ($overlayCode.IndexOf('Key.F9', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $overlayCode.IndexOf('Key.F10', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'F9/F10 test cycling was found. Test selection must remain menu-driven.'
}

if ($overlayCode.IndexOf('Task.Delay(250)', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'A fixed 250 ms scanner acknowledgement delay remains. Guided commands must await runtime state confirmation.'
}
if ($codeBehind.IndexOf('The armed baseline belongs to', [System.StringComparison]::Ordinal) -lt 0) {
    throw 'Baseline/action-label mismatch protection is missing.'
}

$catalogText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.App\Overlay\ExperimentCatalog.cs')
foreach ($token in @('Resources', 'Damage', 'Defense', 'Transformation', 'Movement', 'Spend Ki', 'Spend Stamina', 'Take Damage', 'Use Strike Skill', 'Use Ki Blast Skill', 'Transform', 'De-transform')) {
    if ($catalogText.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Guided experiment catalog requirement is missing: $token"
    }
}

$startHere = Read-Utf8Text -Path (Join-Path $root 'START_HERE.cmd')
if ($startHere.IndexOf('call "%~dp0PUBLISH_WINDOWS.cmd"', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'START_HERE.cmd must rebuild the current source package before launch.'
}
if (Test-Path -LiteralPath (Join-Path $root 'INSTALL_RUNTIME_WINDOWS.cmd')) {
    throw 'A game-bin runtime installer script was found.'
}


$buildProps = Read-Utf8Text -Path (Join-Path $root 'Directory.Build.props')
foreach ($token in @('<TreatWarningsAsErrors>true</TreatWarningsAsErrors>', '<EnableWindowsTargeting>true</EnableWindowsTargeting>', '<ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>')) {
    if ($buildProps.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Build quality requirement is missing: $token"
    }
}

$scannerText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.Runtime\ObjectCapabilityScanner.cs')
if ($scannerText.IndexOf('new(timestamp, monotonicTicks, kind, -1, 0, 0, 0, 0, 0, label, null);', [System.StringComparison]::Ordinal) -lt 0) {
    throw 'Schema-7 scanner-control events must explicitly supply a null FighterIdentityKey.'
}

$companionManagerText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.App\Companions\HealthScaleCompanionManager.cs')
if ($companionManagerText.IndexOf('using System.IO;', [System.StringComparison]::Ordinal) -lt 0) {
    throw 'HealthScaleCompanionManager.cs must explicitly import System.IO.'
}

$observerText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.Runtime\ExternalCapabilityObserver.cs')
if ([regex]::Matches($observerText, '(?m)^\s*ScannerFrame\s+scannerFrame\s*=').Count -gt 0) {
    throw 'Ambiguous scannerFrame local declarations remain in ExternalCapabilityObserver.cs.'
}

$sessionRecorderText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.App\Recording\SessionRecorder.cs')
foreach ($token in @('WriterBufferSize', 'FileOptions.SequentialScan', 'FlushInterval', 'MetadataInterval', 'HashSet<string>', 'candidate-keys.jsonl', 'candidate-index.json', 'timeline.jsonl', 'chronology-samples.jsonl', 'chronology-watchlist.json', 'raw-memory-observations.jsonl', 'runtime-access-architecture.json', 'SchemaVersion = 7', 'ReceiptLatencyMilliseconds', 'ChronologyOutOfOrderCount', 'ChronologySequenceGapCount', 'MonotonicFrequency', 'StartMonotonicTicks', 'scanner-change', 'chronology-change', 'DistinctCandidateCount', 'AutoFlush = false')) {
    if ($sessionRecorderText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Recording optimization requirement is missing: $token"
    }
}

$candidateStoreText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.App\Recording\CandidateStore.cs')
foreach ($token in @('_orderedSnapshot', '_groupSnapshot', 'TimeSpan.FromSeconds(20)', 'TimeSpan.FromSeconds(60)', 'FinalizeTouched', 'ApplySlotEvidence', 'ByRole', 'ByStatus', 'ByTier', 'ByValidation', 'physical-groups.json', 'unresolved-index.json')) {
    if ($candidateStoreText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Candidate organization/optimization requirement is missing: $token"
    }
}

$appResources = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.App\App.xaml')
foreach ($token in @('EnableRowVirtualization', 'EnableColumnVirtualization', 'VirtualizationMode')) {
    if ($appResources.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "WPF virtualization requirement is missing: $token"
    }
}

Write-Host 'PowerScaler Labs Runtime Access Architecture Gate 0 structural checks passed.' -ForegroundColor Green
Write-Host 'Runtime boundary: external query + VM-read only; no injection, game-memory writes, or hooks.'
Write-Host 'Scanner: configurable root ranges, typed decoding, labeled baselines/comparisons, continuous deltas, and bounded pointer-child discovery.'
Write-Host 'Recording: compact frames, raw memory facts, address provenance, scanner evidence, focused chronology, access budgets, candidates, and findings.'
Write-Host 'Classification: one physical-offset group per row, preferred typed interpretation, structured resource pairs, validation stages, and ByTier/ByValidation exports.'
Write-Host 'HealthScale boundary: sealed companion source; explicit desktop install/verify/uninstall only; no runtime coupling.'
