[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:Utf8Strict = New-Object System.Text.UTF8Encoding -ArgumentList $false, $true
function Read-Utf8Text {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.File]::ReadAllText($Path, $script:Utf8Strict)
}

$root = Split-Path -Parent $PSScriptRoot
$required = @(
    'PowerScalerLabs.sln',
    'README.md',
    'docs\POWERSCALER_FULL_BATTLECORE_VIRTUALIZATION_ARCHITECTURE_2026-08-03.md',
    'BUILD_ID.txt',
    'global.json',
    'Directory.Build.props',
    'src\PowerScalerLabs.Protocol\PowerScalerLabs.Protocol.csproj',
    'src\PowerScalerLabs.Protocol\RuntimeProtocol.cs',
    'src\PowerScalerLabs.Protocol\ProbeProtocol.cs',
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
    'src\PowerScalerLabs.App\ProbeHostClient.cs',
    'src\PowerScalerLabs.ProbeHost\PowerScalerLabs.ProbeHost.csproj',
    'src\PowerScalerLabs.ProbeHost\ProbeHostService.cs',
    'src\PowerScalerLabs.ProbeHost\ProbeInjector.cs',
    'src\PowerScalerLabs.ProbeHost\RemoteModuleResolver.cs',
    'src\PowerScalerLabs.ProbeHost\ProbeSharedMemory.cs',
    'src\PowerScalerLabs.ProbeHost\ProbePipeServer.cs',
    'src\PowerScalerLabs.ProbeHost\ProbeArchitectureSelfTest.cs',
    'src\native\PowerScalerLabs.ProbeShared\PowerScalerProbeAbi.h',
    'src\native\PowerScalerLabs.NativeProbe\PowerScalerLabs.NativeProbe.vcxproj',
    'src\native\PowerScalerLabs.NativeProbe\dllmain.cpp',
    'src\native\PowerScalerLabs.NativeProbe\WatchpointManager.cpp',
    'src\native\PowerScalerLabs.NativeProbe\WatchpointManager.h',
    'src\native\PowerScalerLabs.NativeProbe\ExceptionTracer.cpp',
    'src\native\PowerScalerLabs.NativeProbe\ExceptionTracer.h',
    'src\native\PowerScalerLabs.NativeProbe\ProbeEvents.cpp',
    'src\native\PowerScalerLabs.NativeProbe\ProbeEvents.h',
    'src\native\PowerScalerLabs.NativeProbe\ProbeWorker.cpp',
    'src\native\PowerScalerLabs.NativeProbe\ProbeWorker.h',
    'src\native\PowerScalerLabs.NativeProbe\ProbeRuntime.cpp',
    'src\native\PowerScalerLabs.NativeProbe\ProbeRuntime.h',
    'src\native\PowerScalerLabs.NativeProbe\ProbeSharedMemory.cpp',
    'src\native\PowerScalerLabs.NativeProbe\ProbeSharedMemory.h',
    'tests\native\PowerScalerLabs.NativeTransportTests.vcxproj',
    'tests\native\transport_tests.cpp',
    'src\PowerScalerLabs.App\Models\TelemetryViewModels.cs',
    'src\PowerScalerLabs.App\Companions\HealthScaleCompanionManager.cs',
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

foreach ($retiredPath in @(
    'src\PowerScalerLabs.App\Overlay',
    'src\PowerScalerLabs.App\Recording'
)) {
    if (Test-Path -LiteralPath (Join-Path $root $retiredPath)) {
        throw "Retired app subsystem is still present: $retiredPath"
    }
}

$manifestPath = Join-Path $root 'PACKAGE_MANIFEST.sha256'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'PACKAGE_MANIFEST.sha256 is missing.'
}
foreach ($line in [System.IO.File]::ReadAllLines($manifestPath, $script:Utf8Strict)) {
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
foreach ($token in @(
    'WriteProcessMemory',
    'VirtualAllocEx',
    'CreateRemoteThread',
    'NtWriteVirtualMemory',
    'PROCESS_ALL_ACCESS',
    'ProcessVmWrite',
    'ProcessCreateThread',
    'SetWindowsHookEx'
)) {
    if ($runtimeText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden managed-runtime token found: $token"
    }
}
foreach ($token in @(
    'ReadProcessMemory',
    'VirtualQueryEx',
    'ProcessVmRead',
    'ProcessQueryLimitedInformation',
    'BattleCoreLocatorCoordinator',
    'ObjectCapabilityScanner',
    'ChronologySampler',
    'new_chronology_epoch',
    'FighterIdentityMessage'
)) {
    if ($runtimeText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required research-runtime primitive is missing: $token"
    }
}

$protocolText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.Protocol\RuntimeProtocol.cs')
foreach ($token in @(
    'ProtocolVersion = 8',
    'ObservedFighterSlotCount = 14',
    'CurrentHealthOffset = 0x100',
    'MaximumHealthOffset = 0x104',
    'CurrentKiOffset = 0x10C',
    'MaximumKiOffset = 0x110',
    'CurrentStaminaOffset = 0x16C',
    'MaximumStaminaOffset = 0x170',
    'ChronologyConfiguration',
    'ChronologySampleMessage',
    'RuntimeAccessStatusMessage'
)) {
    if ($protocolText.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Protocol invariant is missing: $token"
    }
}

$probeProtocolText = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.Protocol\ProbeProtocol.cs')
foreach ($token in @(
    'PowerScalerLabs.ProbeHost.CausalResearchGate',
    'ProtocolVersion = 3',
    'NativeAbiVersion = 3',
    'ProbeStatusMessage',
    'ProbeCommand',
    'ProbeHostMessage',
    'ProbeCommandResult',
    'SimdRegister0',
    'SimdScalarBits0'
)) {
    if ($probeProtocolText.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Probe protocol invariant is missing: $token"
    }
}

$solutionText = Read-Utf8Text -Path (Join-Path $root 'PowerScalerLabs.sln')
foreach ($token in @('PowerScalerLabs.ProbeHost', 'PowerScalerLabs.NativeProbe')) {
    if ($solutionText.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Probe project is missing from the solution: $token"
    }
}

$probeHostText = (Get-ChildItem -LiteralPath (Join-Path $root 'src\PowerScalerLabs.ProbeHost') -Filter '*.cs' -File |
    ForEach-Object { Read-Utf8Text -Path $_.FullName }) -join "`n"
foreach ($token in @('ProcessVmWrite', 'ProcessVmOperation', 'ProcessCreateThread')) {
    if ($runtimeText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Probe privilege leaked into passive Runtime: $token"
    }
}
foreach ($token in @('WriteProcessMemory', 'VirtualAllocEx', 'CreateRemoteThread', 'PSL_Initialize', 'PSL_PrepareUnload')) {
    if ($probeHostText.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Required isolated ProbeHost capability is missing: $token"
    }
}

$nativeText = (Get-ChildItem -LiteralPath (Join-Path $root 'src\native\PowerScalerLabs.NativeProbe') -Recurse -File |
    Where-Object { $_.Extension -in @('.cpp', '.h') } |
    ForEach-Object { Read-Utf8Text -Path $_.FullName }) -join "`n"
foreach ($required in @(
    'TryCommitEvent', 'InterlockedCompareExchange64', 'command_event_count', 'Synthetic',
    'WatchpointManager', 'ExceptionTracer', 'AddVectoredExceptionHandler', 'RemoveVectoredExceptionHandler',
    'EXCEPTION_SINGLE_STEP', 'CONTEXT_DEBUG_REGISTERS', 'CreateToolhelp32Snapshot', 'TH32CS_SNAPTHREAD',
    'SuspendThread', 'GetThreadContext', 'SetThreadContext', 'ResumeThread', 'BuildDr0WriteControl',
    'HardwareWriteTrap', 'EXCEPTION_CONTINUE_SEARCH', 'EXCEPTION_CONTINUE_EXECUTION',
    'command_simd_register_0', 'simd_scalar_bits_0', 'ReadScalarBits'
)) {
    if ($nativeText.IndexOf($required, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Required native transport primitive is missing: $required"
    }
}
foreach ($forbidden in @('StackWalk64', 'RtlVirtualUnwind', 'AddVectoredContinueHandler', 'DebugActiveProcess', 'WriteProcessMemory')) {
    if ($nativeText.IndexOf($forbidden, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Deferred instrumentation appeared in NativeProbe: $forbidden"
    }
}
if ($nativeText.IndexOf('context.Dr1 = target_address_', [System.StringComparison]::Ordinal) -ge 0 -or
    $nativeText.IndexOf('context.Dr2 = target_address_', [System.StringComparison]::Ordinal) -ge 0 -or
    $nativeText.IndexOf('context.Dr3 = target_address_', [System.StringComparison]::Ordinal) -ge 0) {
    throw 'Only DR0 allocation is permitted in the HP write-watchpoint gate.'
}

$appText = (Get-ChildItem -LiteralPath (Join-Path $root 'src\PowerScalerLabs.App') -Recurse -Filter '*.cs' -File |
    ForEach-Object { Read-Utf8Text -Path $_.FullName }) -join "`n"
foreach ($required in @('DrainCommittedEvents', 'ProbeHostMessage.ForEvent', 'CommandId', 'ShutdownAsync')) {
    if (($probeHostText + $appText + $probeProtocolText).IndexOf($required, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Required managed transport primitive is missing: $required"
    }
}
foreach ($required in @(
    'arm_write_watch', 'disarm_watch', 'DescribeTrapContext', 'HpWriteTraceSession',
    'RuntimeProtocol.CurrentHealthOffset', 'FighterLifetime', 'CorrelateFighter',
    'HP trace stopped: selected fighter generation released.', 'HardwareWriteTrap',
    'WriterEvidence', 'WRITER EVIDENCE SUMMARY', 'HpTraceStimulusCombo',
    'HpTraceAutoDisarmCheckBox', 'AutoDisarmAfterDamageQuietPeriodAsync', 'Detected subtraction events',
    'ControllerShortcutTimer_Tick', 'xinput1_4.dll', 'ControllerSafetyChord', 'CycleStimulus'
)) {
    if (($probeHostText + $appText + $probeProtocolText).IndexOf($required, [System.StringComparison]::Ordinal) -lt 0) {
        throw "HP write-watchpoint gate requirement is missing: $required"
    }
}
foreach ($required in @(
    'AppShutdownState',
    'PerformShutdownCleanupAsync',
    'Dispatcher.BeginInvoke',
    'QueueDeadSessionCleanup',
    'Interlocked.CompareExchange(ref _deadProcessCleanupQueued',
    'DisposeDeadSessionLockedAsync',
    'WaitForTransportSettlementAsync',
    'TimedOutWithPendingAccounting',
    'post-overflow-recovery'
)) {
    if (($probeHostText + $appText).IndexOf($required, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Closeout lifecycle/accounting fix is missing: $required"
    }
}
foreach ($retiredToken in @(
    'SessionRecorder',
    'CandidateStore',
    'CandidateGroupBuilder',
    'ExperimentOverlayWindow',
    'GlobalHotKey.Register',
    'CaptureGuidedBaselineAsync',
    'CompareGuidedResultsAsync',
    'StartGuidedRecordingAsync'
)) {
    if ($appText.IndexOf($retiredToken, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Retired app workflow token is still compiled: $retiredToken"
    }
}
foreach ($requiredToken in @(
    'FighterRows',
    'ChronologyRows',
    'FindingRows',
    'HealthScaleCompanionManager',
    'new_chronology_epoch',
    'pause_chronology',
    'resume_chronology'
)) {
    if ($appText.IndexOf($requiredToken, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Clean research-app requirement is missing: $requiredToken"
    }
}

$mainWindowXaml = Read-Utf8Text -Path (Join-Path $root 'src\PowerScalerLabs.App\MainWindow.xaml')
foreach ($requiredToken in @(
    'Content="Fighters"',
    'Content="Research"',
    'Content="Findings"',
    'Content="Diagnostics"',
    'Content="Tools"',
    'Live fighter registry',
    'Native causal probe',
    'Durable findings',
    'HealthScale 1.1.1 companion'
    'Attach Probe'
    'Detach Probe'
)) {
    if ($mainWindowXaml.IndexOf($requiredToken, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Clean application layout requirement is missing: $requiredToken"
    }
}
foreach ($retiredToken in @(
    'Capability Scanner',
    'Capture Baseline',
    'Compare After Action',
    'Full Snapshot',
    'Candidates &amp; Findings',
    'Open Guided Overlay',
    'Start Recording'
)) {
    if ($mainWindowXaml.IndexOf($retiredToken, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Retired workflow is still visible in MainWindow.xaml: $retiredToken"
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

Write-Host 'PowerScaler Labs Mechanical Trace Trial Classification Gate verification passed.'
Write-Host 'App: Fighters / Research / Findings / Diagnostics / Tools; legacy scanner-recording-candidate UI removed.'
Write-Host 'Runtime: external read-only foundation retained, including fighter generations, targeted scanner primitive, chronology, provenance, and read budgets.'
Write-Host 'Probe: ABI 3 DR0 write-watch observation, selected SIMD evidence, transactional thread coverage, VEH ownership, and MPSC transport.'
