# Runtime Access Architecture Gate 0 — Publish Verifier Hotfix 2

## Failure corrected

`Deep-Audit-HealthScaleCompanion.ps1` required the derived publish path
`Companions\HealthScale\Payload` to occur as one contiguous string inside
`Publish-Windows.ps1`.

The publisher already constructed the correct path in two deliberate steps:

```powershell
$healthScaleArtifacts = Join-Path $artifacts 'Companions\HealthScale'
$healthScalePayloadArtifacts = Join-Path $healthScaleArtifacts 'Payload'
```

Because the final path was composed rather than hard-coded, the audit stopped the
publish before compilation even though the payload destination was correct.

## Correction

- Removed the impossible contiguous-literal requirement.
- Added exact invariants for the two path-composition assignments.
- Added invariants for payload-directory creation, DLL staging, INI staging, and
  `payload.sha256` generation through `$healthScalePayloadArtifacts`.
- Added a platform-neutral path-composition probe using `Join-Path`.
- Kept the Hotfix 1 version-guard correction and all Gate 0 runtime boundaries.

## Boundary

This hotfix changes no telemetry code, runtime address, read cadence, process
permission, HealthScale source file, companion installation rule, or uninstall
rule. It only repairs the pre-build companion audit so the Windows publisher can
continue to compilation, architecture self-test, native HealthScale build, and
artifact staging.
