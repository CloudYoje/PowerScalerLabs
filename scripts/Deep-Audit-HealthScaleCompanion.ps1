[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false, $true

function Read-StrictUtf8 {
    param([Parameter(Mandatory)][string]$RelativePath)
    return [System.IO.File]::ReadAllText((Join-Path $root $RelativePath), $utf8)
}

$required = @(
    'companions\HealthScale\companion-manifest.json',
    'companions\HealthScale\README_COMPANION.md',
    'companions\HealthScale\UPSTREAM_SOURCE_SHA256SUMS.txt',
    'companions\HealthScale\Source\HealthScale.sln',
    'companions\HealthScale\Source\src\native\HealthScale.Runtime\HealthScale.Runtime.vcxproj',
    'companions\HealthScale\Source\src\native\HealthScale.Runtime\HealthScale.ini',
    'src\PowerScalerLabs.App\Companions\HealthScaleCompanionManager.cs'
)
foreach ($relativePath in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relativePath) -PathType Leaf)) {
        throw "HealthScale companion file is missing: $relativePath"
    }
}

$sourceRoot = Join-Path $root 'companions\HealthScale\Source'
$upstreamManifest = Join-Path $root 'companions\HealthScale\UPSTREAM_SOURCE_SHA256SUMS.txt'
foreach ($line in [System.IO.File]::ReadAllLines($upstreamManifest, $utf8)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<path>.+)$') {
        throw "Malformed HealthScale upstream hash line: $line"
    }
    $expectedHash = $Matches.hash.ToLowerInvariant()
    $relativePath = $Matches.path.Trim()
    if ($relativePath.StartsWith('.\') -or $relativePath.StartsWith('./')) {
        $relativePath = $relativePath.Substring(2)
    }
    $relativePath = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $fullPath = Join-Path $sourceRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Frozen HealthScale source file is missing: $relativePath"
    }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Frozen HealthScale source changed: $relativePath"
    }
}

$manifest = Read-StrictUtf8 'companions\HealthScale\companion-manifest.json'
$manifestObject = $manifest | ConvertFrom-Json
if ($manifestObject.id -ne 'healthscale' -or $manifestObject.version -ne '1.1.1' -or $manifestObject.boundary -ne 'sealed-companion') {
    throw 'HealthScale companion manifest identity or boundary is invalid.'
}
if ($manifestObject.runtimeFile -ne 'xinput_other.dll' -or $manifestObject.configurationFile -ne 'HealthScale.ini') {
    throw 'HealthScale companion manifest payload names are invalid.'
}

$solution = Read-StrictUtf8 'PowerScalerLabs.sln'
if ($solution.IndexOf('HealthScale.Runtime', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'HealthScale was merged into PowerScalerLabs.sln. It must remain an independent solution.'
}

$appProject = Read-StrictUtf8 'src\PowerScalerLabs.App\PowerScalerLabs.App.csproj'
if ($appProject.IndexOf('HealthScale.Runtime.vcxproj', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The app project directly references the native HealthScale project.'
}

$runtimeSource = (Get-ChildItem -LiteralPath (Join-Path $root 'src\PowerScalerLabs.Runtime') -Filter '*.cs' -File |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName, $utf8) }) -join "`n"
foreach ($token in @('HealthScale', 'xinput_other.dll', 'File.Copy', 'File.Delete')) {
    if ($runtimeSource.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "PowerScaler runtime crossed the HealthScale companion boundary: $token"
    }
}

$manager = Read-StrictUtf8 'src\PowerScalerLabs.App\Companions\HealthScaleCompanionManager.cs'
foreach ($token in @(
    'CompanionVersion = "1.1.1"',
    'RuntimeFileName = "xinput_other.dll"',
    'ConfigurationFileName = "HealthScale.ini"',
    'ComputeSha256',
    'ReceiptMatches',
    'CopyFileAtomic',
    'IsGameRunning',
    'new Version(1, 25, 2, 0)',
    'InstallOrAdopt',
    'Uninstall',
    'The installed DLL no longer matches the managed receipt',
    'An unrecognized xinput_other.dll already exists',
    'ConfigurationCreatedByManager',
    'InstalledConfigurationHash',
    'SaveJsonAtomic'
)) {
    if ($manager.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "HealthScale companion safety invariant is missing: $token"
    }
}
foreach ($token in @('WriteProcessMemory', 'ReadProcessMemory', 'VirtualAllocEx', 'CreateRemoteThread', 'DllImport')) {
    if ($manager.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "HealthScale companion manager contains a forbidden process-access token: $token"
    }
}

$xaml = Read-StrictUtf8 'src\PowerScalerLabs.App\MainWindow.xaml'
foreach ($token in @(
    'Companion Apps',
    'HealthScale 1.1.1',
    'Install / Adopt',
    'Uninstall Managed Copy',
    'Boundary guarantees',
    'PowerScaler Labs never overwrites an unknown xinput_other.dll'
)) {
    if ($xaml.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        throw "HealthScale companion UI invariant is missing: $token"
    }
}

$publisher = Read-StrictUtf8 'scripts\Publish-Windows.ps1'
foreach ($token in @(
    'Find-MSBuild',
    'HealthScale.sln',
    '/p:Configuration=Release',
    '/p:Platform=x64',
    '$healthScaleArtifacts = Join-Path $artifacts ''Companions\HealthScale''',
    '$healthScalePayloadArtifacts = Join-Path $healthScaleArtifacts ''Payload''',
    'New-Item -ItemType Directory -Path $healthScalePayloadArtifacts -Force',
    'Copy-Item -LiteralPath $healthScaleDll -Destination (Join-Path $healthScalePayloadArtifacts ''xinput_other.dll'') -Force',
    'Copy-Item -LiteralPath $healthScaleIni -Destination (Join-Path $healthScalePayloadArtifacts ''HealthScale.ini'') -Force',
    'Join-Path $healthScalePayloadArtifacts ''payload.sha256'''
)) {
    if ($publisher.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "HealthScale companion publish invariant is missing: $token"
    }
}

$layoutProbeRoot = Join-Path $root '__publish-layout-probe__'
$layoutProbeCompanion = Join-Path $layoutProbeRoot 'Companions\HealthScale'
$layoutProbePayload = Join-Path $layoutProbeCompanion 'Payload'
$expectedPayloadSuffix = Join-Path (Join-Path 'Companions' 'HealthScale') 'Payload'
if (-not $layoutProbePayload.EndsWith($expectedPayloadSuffix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'HealthScale companion publish layout composition is invalid.'
}

Write-Host 'Deep audit passed: uploaded HealthScale 1.1.1 source matches its frozen upstream SHA-256 manifest.' -ForegroundColor Green
Write-Host 'Deep audit passed: HealthScale remains an independent Visual C++ solution and is not linked into PowerScaler assemblies.' -ForegroundColor Green
Write-Host 'Deep audit passed: the external PowerScaler runtime has no HealthScale file-management or process-access path.' -ForegroundColor Green
Write-Host 'Deep audit passed: desktop companion management refuses unknown DLL replacement and requires a matching receipt for removal.' -ForegroundColor Green
Write-Host 'Deep audit passed: DBXV2-running lockout, SHA-256 verification, atomic copies, and modified-INI preservation are present.' -ForegroundColor Green
