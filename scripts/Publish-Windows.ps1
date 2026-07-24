[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$logs = Join-Path $root 'logs'
$artifacts = Join-Path $root 'artifacts\PowerScalerLabs'
$runtimeArtifacts = Join-Path $artifacts 'Runtime'
$healthScaleCompanionRoot = Join-Path $root 'companions\HealthScale'
$healthScaleSourceRoot = Join-Path $healthScaleCompanionRoot 'Source'
$healthScaleSolution = Join-Path $healthScaleSourceRoot 'HealthScale.sln'
$healthScaleArtifacts = Join-Path $artifacts 'Companions\HealthScale'
$healthScalePayloadArtifacts = Join-Path $healthScaleArtifacts 'Payload'
$logPath = Join-Path $logs 'publish.log'

Set-Location -LiteralPath $root
New-Item -ItemType Directory -Path $logs -Force | Out-Null
if (Test-Path -LiteralPath $logPath) {
    Remove-Item -LiteralPath $logPath -Force
}

function Write-Log {
    param([Parameter(Mandatory)][string]$Message)
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
    Write-Host $line
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    Write-Log "dotnet $($Arguments -join ' ')"
    & $script:DotNetExe @Arguments 2>&1 | ForEach-Object {
        $text = $_.ToString()
        Write-Host $text
        Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8
    }
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Find-MSBuild {
    $command = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $msbuildMatches = @(& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
        $match = $msbuildMatches | Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($match) -and (Test-Path -LiteralPath $match -PathType Leaf)) {
            return $match
        }
    }

    throw 'MSBuild with the Visual C++ x64 toolchain was not found. Install the Visual Studio C++ desktop workload or run from a Visual Studio Developer Command Prompt.'
}

function Invoke-MSBuild {
    param([Parameter(Mandatory)][string[]]$Arguments)
    Write-Log "msbuild $($Arguments -join ' ')"
    & $script:MSBuildExe @Arguments 2>&1 | ForEach-Object {
        $text = $_.ToString()
        Write-Host $text
        Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8
    }
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE."
    }
}

function Copy-CompanionDocument {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$DestinationName
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required HealthScale companion document is missing: $Source"
    }
    Copy-Item -LiteralPath $Source -Destination (Join-Path $healthScaleArtifacts $DestinationName) -Force
}

try {
    Write-Log 'PowerScaler Labs Runtime Access Architecture Gate 0 Build Hotfix 1 publish started.'
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $script:DotNetExe = $dotnetCommand.Source
    $script:MSBuildExe = Find-MSBuild
    Write-Log "DOTNET_EXE=$script:DotNetExe"
    Write-Log "MSBUILD_EXE=$script:MSBuildExe"

    Get-ChildItem -LiteralPath (Join-Path $root 'src') -Directory -Recurse |
        Where-Object { $_.Name -in @('bin', 'obj') } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Get-ChildItem -LiteralPath $healthScaleSourceRoot -Directory -Recurse |
        Where-Object { $_.Name -in @('bin', 'obj') } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    & $script:DotNetExe --version 2>&1 | ForEach-Object {
        $text = $_.ToString()
        Write-Host $text
        Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8
    }
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet --version failed.'
    }

    & (Join-Path $PSScriptRoot 'Deep-Audit-DeclutterChronology.ps1') 2>&1 | ForEach-Object {
        $text = $_.ToString()
        Write-Host $text
        Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8
    }

    & (Join-Path $PSScriptRoot 'Deep-Audit-ChronologicalTelemetry.ps1') 2>&1 | ForEach-Object {
        $text = $_.ToString()
        Write-Host $text
        Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8
    }


    & (Join-Path $PSScriptRoot 'Deep-Audit-RuntimeAccessArchitecture.ps1') 2>&1 | ForEach-Object {
        $text = $_.ToString()
        Write-Host $text
        Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8
    }

    & (Join-Path $PSScriptRoot 'Deep-Audit-HealthScaleCompanion.ps1') 2>&1 | ForEach-Object {
        $text = $_.ToString()
        Write-Host $text
        Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8
    }

    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'

    if (Test-Path -LiteralPath $artifacts) {
        Remove-Item -LiteralPath $artifacts -Recurse -Force
    }
    New-Item -ItemType Directory -Path $runtimeArtifacts -Force | Out-Null
    New-Item -ItemType Directory -Path $healthScalePayloadArtifacts -Force | Out-Null

    Invoke-DotNet -Arguments @('restore', (Join-Path $root 'PowerScalerLabs.sln'), '--nologo')
    Invoke-DotNet -Arguments @('build', (Join-Path $root 'PowerScalerLabs.sln'), '-c', 'Release', '--no-restore', '--nologo', '--disable-build-servers', '-p:UseSharedCompilation=false')
    Invoke-DotNet -Arguments @('run', '--project', (Join-Path $root 'src\PowerScalerLabs.Runtime\PowerScalerLabs.Runtime.csproj'), '-c', 'Release', '--no-build', '--', '--architecture-self-test')

    Invoke-MSBuild -Arguments @($healthScaleSolution, '/m', '/p:Configuration=Release', '/p:Platform=x64', '/nologo')

    Invoke-DotNet -Arguments @(
        'publish', (Join-Path $root 'src\PowerScalerLabs.Runtime\PowerScalerLabs.Runtime.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '--nologo',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishReadyToRun=false', '-p:PublishTrimmed=false', '--disable-build-servers', '-p:UseSharedCompilation=false', '-o', $runtimeArtifacts
    )

    Invoke-DotNet -Arguments @(
        'publish', (Join-Path $root 'src\PowerScalerLabs.App\PowerScalerLabs.App.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '--nologo',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishReadyToRun=false', '-p:PublishTrimmed=false', '--disable-build-servers', '-p:UseSharedCompilation=false', '-o', $artifacts
    )

    $appExe = Join-Path $artifacts 'PowerScalerLabs.exe'
    $runtimeExe = Join-Path $runtimeArtifacts 'PowerScalerLabs.Runtime.exe'
    $healthScaleDll = Join-Path $healthScaleSourceRoot 'src\native\HealthScale.Runtime\bin\Release\xinput_other.dll'
    $healthScaleIni = Join-Path $healthScaleSourceRoot 'src\native\HealthScale.Runtime\HealthScale.ini'
    if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
        throw "Published app is missing: $appExe"
    }
    if (-not (Test-Path -LiteralPath $runtimeExe -PathType Leaf)) {
        throw "Published runtime is missing: $runtimeExe"
    }
    if (-not (Test-Path -LiteralPath $healthScaleDll -PathType Leaf)) {
        throw "Built HealthScale companion DLL is missing: $healthScaleDll"
    }
    if (-not (Test-Path -LiteralPath $healthScaleIni -PathType Leaf)) {
        throw "HealthScale companion configuration is missing: $healthScaleIni"
    }

    Copy-Item -LiteralPath $healthScaleDll -Destination (Join-Path $healthScalePayloadArtifacts 'xinput_other.dll') -Force
    Copy-Item -LiteralPath $healthScaleIni -Destination (Join-Path $healthScalePayloadArtifacts 'HealthScale.ini') -Force
    Copy-CompanionDocument -Source (Join-Path $healthScaleCompanionRoot 'companion-manifest.json') -DestinationName 'companion-manifest.json'
    Copy-CompanionDocument -Source (Join-Path $healthScaleCompanionRoot 'README_COMPANION.md') -DestinationName 'README_COMPANION.md'
    Copy-CompanionDocument -Source (Join-Path $healthScaleSourceRoot 'README.md') -DestinationName 'HealthScale-README.md'
    Copy-CompanionDocument -Source (Join-Path $healthScaleSourceRoot 'FIX_REPORT.txt') -DestinationName 'FIX_REPORT.txt'
    Copy-CompanionDocument -Source (Join-Path $healthScaleSourceRoot 'VALIDATION_REPORT.txt') -DestinationName 'VALIDATION_REPORT.txt'
    Copy-CompanionDocument -Source (Join-Path $healthScaleSourceRoot 'QUEST_RUNTIME_TEST_PLAN.md') -DestinationName 'QUEST_RUNTIME_TEST_PLAN.md'
    Copy-CompanionDocument -Source (Join-Path $root 'HEALTHSCALE_COMPANION_AUDIT.md') -DestinationName 'POWERSCALER_COMPANION_AUDIT.md'
    Copy-CompanionDocument -Source (Join-Path $root 'HEALTHSCALE_COMPANION_TEST.md') -DestinationName 'POWERSCALER_COMPANION_TEST.md'

    $appHash = (Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $runtimeHash = (Get-FileHash -LiteralPath $runtimeExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $healthScalePayloadDll = Join-Path $healthScalePayloadArtifacts 'xinput_other.dll'
    $healthScalePayloadIni = Join-Path $healthScalePayloadArtifacts 'HealthScale.ini'
    $healthScaleDllHash = (Get-FileHash -LiteralPath $healthScalePayloadDll -Algorithm SHA256).Hash.ToLowerInvariant()
    $healthScaleIniHash = (Get-FileHash -LiteralPath $healthScalePayloadIni -Algorithm SHA256).Hash.ToLowerInvariant()
    @(
        "$healthScaleDllHash *xinput_other.dll",
        "$healthScaleIniHash *HealthScale.ini"
    ) | Set-Content -LiteralPath (Join-Path $healthScalePayloadArtifacts 'payload.sha256') -Encoding UTF8

    $sourceBuildId = (Get-Content -LiteralPath (Join-Path $root 'BUILD_ID.txt') -Raw).Trim()
    @(
        'PowerScaler Labs - Runtime Access Architecture Gate 0 Build Hotfix 1 + HealthScale Companion 1',
        "Build ID: $sourceBuildId",
        "Published: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "PowerScalerLabs.exe SHA-256: $appHash",
        "PowerScalerLabs.Runtime.exe SHA-256: $runtimeHash",
        "HealthScale 1.1.1 xinput_other.dll SHA-256: $healthScaleDllHash",
        'Runtime boundary: provider-based external read-only access, factual raw observations, identity generations, and measured chronology; no game-memory writes, hooks, or injection.',
        'Companion boundary: HealthScale is independently built from frozen source and installed only through explicit fail-closed desktop-app actions.'
    ) | Set-Content -LiteralPath (Join-Path $artifacts 'BUILD_INFO.txt') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $artifacts 'BUILD_ID.txt') -Value $sourceBuildId -Encoding UTF8

    Write-Log "Publish completed: $appExe"
    Write-Log "HealthScale companion payload staged: $healthScalePayloadDll"
    exit 0
}
catch {
    Write-Log "ERROR: $($_.Exception.Message)"
    Write-Log "Full error: $($_ | Out-String)"
    exit 1
}
