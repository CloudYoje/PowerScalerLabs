# Runtime Access Architecture Gate 0 — Build Hotfix 1

## Compiler failures corrected

The first Windows compilation of Publish Verifier Hotfix 2 reached `dotnet build`
and exposed two source-level migration defects:

1. `TelemetryEventMessage` gained the nullable `FighterIdentityKey` field in
   protocol schema 7, but the scanner-control `ScannerEvent` helper still called
   the positional constructor with the older ten-argument shape.
2. `HealthScaleCompanionManager.cs` uses `Path`, `Directory`, `File`,
   `FileStream`, `FileMode`, `FileAccess`, `FileShare`, and `IOException` without
   importing `System.IO` explicitly. The Windows WPF compilation did not resolve
   those names in that file.

## Corrections

- Scanner-control events now pass an explicit final `null` identity key. These
  events are not tied to an acquired fighter and therefore must remain unbound.
- `HealthScaleCompanionManager.cs` now explicitly imports `System.IO`.
- The source verifier now requires both corrections so the same compile break
  cannot be repackaged silently.

## Boundary

This hotfix does not alter process permissions, addresses, polling cadence,
BattleCore provider behavior, raw observation semantics, HealthScale source,
companion installation rules, or game state. It only completes the schema-7
constructor migration and makes the companion manager's framework dependency
explicit.
