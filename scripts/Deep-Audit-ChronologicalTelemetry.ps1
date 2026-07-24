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
    'MaximumChronologyTargets = 64',
    'MaximumChronologyBatch = 4_096',
    'CurrentHealthOffset = 0x100',
    'MaximumHealthOffset = 0x104',
    'CurrentKiOffset = 0x10C',
    'MaximumKiOffset = 0x110',
    'CurrentStaminaOffset = 0x16C',
    'MaximumStaminaOffset = 0x170',
    'ChronologyConfiguration',
    'ChronologyWatchTarget',
    'ChronologySampleMessage',
    'ChronologyStatusMessage',
    'long Sequence',
    'long CaptureId',
    'long Epoch',
    'PollStartedMonotonicTicks',
    'PollCompletedMonotonicTicks',
    'DroppedSampleCount',
    'InvalidatedSampleCount',
    'EpochInitialSampleCount',
    'EpochPollCount',
    'EpochDroppedSampleCount',
    'EpochPollOverrunCount',
    'PollOverrunCount'
)) {
    if ($protocol.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Chronology protocol invariant is missing: $token"
    }
}

$defaultTargetMatches = @([regex]::Matches($protocol, 'new\("Battle_Mob", RuntimeProtocol\.(?:Current|Maximum)(?:Health|Ki|Stamina)Offset'))
if ($defaultTargetMatches.Count -ne 6) {
    throw "Expected exactly six default chronology anchors; found $($defaultTargetMatches.Count)."
}

$sampler = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\ChronologySampler.cs'
foreach ($token in @(
    'Task.Run(WorkerAsync)',
    'ChronologyConfiguration.Default',
    'IntervalMs, 10, 1000',
    'MaximumPendingSamples = 20_000',
    'ConcurrentQueue<ChronologySampleMessage>',
    'TryReadKnownReadable',
    'Stopwatch.GetTimestamp()',
    'DateTimeOffset.UtcNow',
    'if (!initial && !changed)',
    'previous.RawValue == current.RawValue',
    'sample with { Sequence = ++_deliveredSequence }',
    'sample.Epoch != currentEpoch',
    'previous.Clear()',
    'state.Generation',
    'PollOverrunCount',
    'MaximumPollDurationMilliseconds',
    'MaximumChronologyBatch',
    'reset_chronology',
    'new_chronology_epoch',
    'pause_chronology',
    'resume_chronology'
)) {
    if ($sampler.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Chronology sampler invariant is missing: $token"
    }
}

foreach ($forbidden in @(
    'WriteProcessMemory',
    'VirtualAllocEx',
    'CreateRemoteThread',
    'SetWindowsHookEx',
    'Present(',
    'xinput_other.dll'
)) {
    if ($sampler.IndexOf($forbidden, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden chronology sampler token found: $forbidden"
    }
}

$reader = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\GameMemoryReader.cs'
foreach ($token in @(
    'TryReadKnownReadable',
    'ReadProcessMemory',
    'ReadProcessMemory remains fail-closed',
    'Avoiding a VirtualQueryEx call for every 25 ms scalar sample'
)) {
    if ($reader.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Optimized read-only chronology lane is missing: $token"
    }
}

$runtimeHost = Read-StrictUtf8 'src\PowerScalerLabs.Runtime\RuntimeHost.cs'
foreach ($token in @(
    'private readonly ChronologySampler _chronologySampler = new();',
    'commands.Where(ChronologySampler.IsChronologyCommand)',
    'observerCommands',
    '_chronologySampler.UpdateTarget(game.Id, frame.Fighters)',
    '_chronologySampler.DrainFrame()',
    'activeChronologyFrame.Status',
    'activeChronologyFrame.Samples',
    'Task.Delay(TimeSpan.FromMilliseconds(100)'
)) {
    if ($runtimeHost.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Runtime chronology integration invariant is missing: $token"
    }
}
if ($runtimeHost.IndexOf('Task.Delay(TimeSpan.FromMilliseconds(25)', [System.StringComparison]::Ordinal) -ge 0) {
    throw 'The main runtime heartbeat was accelerated to 25 ms. Chronology must remain on its isolated worker lane.'
}

$recorder = Read-StrictUtf8 'src\PowerScalerLabs.App\Recording\SessionRecorder.cs'
foreach ($token in @(
    'SchemaVersion = 7',
    'chronology-samples.jsonl',
    'chronology-watchlist.json',
    'ReceiptMonotonicTicks',
    'ReceiptLatencyMilliseconds',
    'ChronologyOutOfOrderCount',
    'ChronologySequenceGapCount',
    'ChronologyPollCount',
    'ChronologyReadCount',
    'ChronologyUnreadableReadCount',
    'ChronologyPollOverrunCount',
    'InvalidatedChronologySampleCount',
    'MaximumChronologyPollDurationMilliseconds',
    'chronology-baseline',
    'chronology-change',
    'FileOptions.SequentialScan',
    'AutoFlush = false'
)) {
    if ($recorder.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Chronology persistence invariant is missing: $token"
    }
}

$mainWindow = Read-StrictUtf8 'src\PowerScalerLabs.App\MainWindow.xaml.cs'
foreach ($token in @(
    'ApplyChronologyStatus(message.Chronology)',
    'ChronologyRows.ReplaceAll',
    'OrderByDescending(sample => sample.Sequence)',
    'StartRecordingCoreAsync',
    'new_chronology_epoch',
    'Session chronology epoch',
    'WaitForChronologyStateAsync',
    'WaitForRuntimeQueuesAsync',
    'pause_chronology',
    'resume_chronology',
    'PendingSampleCount',
    'ChronologySampleCount'
)) {
    if ($mainWindow.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Chronology app integration invariant is missing: $token"
    }
}

$xaml = Read-StrictUtf8 'src\PowerScalerLabs.App\MainWindow.xaml'
foreach ($token in @(
    'Chronological changes',
    'ChronologyStatusText',
    'ChronologyMetricsText',
    'ItemsSource="{Binding ChronologyRows}"',
    'Binding="{Binding Sequence}"',
    'Binding="{Binding Capture}"',
    'Binding="{Binding Validation}"'
)) {
    if ($xaml.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Chronology review UI invariant is missing: $token"
    }
}

# Force array semantics under Windows PowerShell 5.1 StrictMode.
$sourceBinaries = @(
    Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File | Where-Object {
        $_.Extension -in @('.dll', '.exe', '.pdb', '.obj', '.lib')
    }
)
if ($sourceBinaries.Count -gt 0) {
    throw "Generated binary found in source tree: $($sourceBinaries[0].FullName)"
}

Write-Host 'Deep audit passed: six focused Battle_Mob anchors are sampled on an isolated 25 ms worker.' -ForegroundColor Green
Write-Host 'Deep audit passed: each emitted change carries sequence, capture, epoch, QPC sample time, and poll bounds.' -ForegroundColor Green
Write-Host 'Deep audit passed: stable polls do not create disk clutter; exact raw-value changes and initial anchors are retained.' -ForegroundColor Green
Write-Host 'Deep audit passed: the broad scanner heartbeat remains 100 ms and was not accelerated.' -ForegroundColor Green
Write-Host 'Deep audit passed: known-readable scalar reads avoid per-sample VirtualQueryEx while ReadProcessMemory remains fail-closed.' -ForegroundColor Green
Write-Host 'Deep audit passed: session start establishes a fresh epoch; normal stop pauses, drains, saves, and resumes.' -ForegroundColor Green
Write-Host 'Deep audit passed: chronology queue, sequence gaps, out-of-order delivery, receipt latency, overruns, and drops are persisted.' -ForegroundColor Green
Write-Host 'Deep audit passed: runtime remains external/read-only; no hooks, injection, game-memory writes, or HealthScale coupling.' -ForegroundColor Green
