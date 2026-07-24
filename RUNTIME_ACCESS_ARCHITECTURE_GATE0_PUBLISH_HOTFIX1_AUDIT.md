# Runtime Access Architecture Gate 0 — Publish Verifier Hotfix 1

## Failure corrected

The Gate 0 refactor centralized the approved DBXV2 version constant in:

```text
src/PowerScalerLabs.Runtime/ValidatedRuntimeLayout.cs
ValidatedRuntimeLayout.SupportedGameVersion = "1.25.2.0"
```

The publish verifier still required the retired pre-refactor token `ExpectedGameVersion`. Because the token no longer existed, `Verify-PowerScalerLabs.ps1` stopped the Windows publish before compilation even though the centralized version guard was present.

## Correction

- Replaced the obsolete `ExpectedGameVersion` verifier requirement with the exact centralized `SupportedGameVersion = "1.25.2.0"` invariant.
- Renamed the resulting verifier error from `capability-scanner token` to `runtime-architecture token` so failures identify the current architecture layer.
- Added this hotfix audit to the package's required-file and SHA-256 integrity checks.

## Boundary

This hotfix changes no PowerScaler runtime access behavior, telemetry cadence, DBXV2 address, HealthScale source, installation behavior, or safety boundary. It only repairs the source-package publish gate so the Windows build can proceed to the real compilation and self-test stages.
