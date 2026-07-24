[CmdletBinding()]
param(
    [string]$SourceDataFolder
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($SourceDataFolder)) {
    $SourceDataFolder = Read-Host 'Paste the full path to the previous PowerScaler Labs Data folder'
}
$source = [IO.Path]::GetFullPath($SourceDataFolder.Trim('"'))
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Source Data folder does not exist: $source"
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'Candidates')) -and
    -not (Test-Path -LiteralPath (Join-Path $source 'Sessions'))) {
    throw 'The selected folder does not look like a PowerScaler Labs Data folder.'
}

$target = Join-Path $env:LOCALAPPDATA 'PowerScaler Labs\Data'
$targetCandidates = Join-Path $target 'Candidates\candidates.json'
$sourceCandidates = Join-Path $source 'Candidates\candidates.json'
New-Item -ItemType Directory -Path $target -Force | Out-Null

if ((Test-Path -LiteralPath $sourceCandidates -PathType Leaf) -and
    (Test-Path -LiteralPath $targetCandidates -PathType Leaf)) {
    throw "The persistent candidate store already exists at $targetCandidates. This safety importer will not overwrite it."
}

Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force
}

Write-Host "Previous research data imported to:" -ForegroundColor Green
Write-Host "  $target"
Write-Host 'Future PowerScaler Labs builds use this persistent location automatically.'
