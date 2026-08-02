# Version Independence Hotfix 1 Audit

## Objective

Remove hard dependencies on a specific DBXV2 executable version and XV2 Patcher image size without converting fixed-address access into blind trust.

## Removed gates

- `ExternalCapabilityObserver` no longer returns an unsupported-game state based on `DBXV2.exe` file version.
- `Xv2PatcherBattleCoreLocator` no longer rejects `xinput1_3.dll` based on PE image size.
- `HealthScaleCompanionManager` no longer blocks installation based on `DBXV2.exe` file version.
- Native `dllmain.cpp` no longer stops HealthScale on a version mismatch.
- Native `health_overhaul_runtime.cpp` no longer disables health writes on a patcher image-size mismatch.
- The companion manifest no longer declares supported game or patcher versions.

## Retained safety boundary

The hotfix does not treat all versions as layout-compatible. It replaces version allowlists with live evidence:

- guarded memory reads and range validation;
- private writable object ownership checks;
- DBXV2 image-vtable checks;
- complete fighter-slot structural scoring;
- exact native HUD writer signatures;
- stable-sample requirements;
- provider disagreement failure;
- no guessed fallback address.

A changed build can therefore run only when the known route still proves itself structurally. Otherwise the runtime remains waiting/no-candidate and HealthScale hook initialization remains inactive.

## Protocol and recording

Protocol and session schemas advance to 8. Address provenance now records a `CompatibilityPolicy` instead of a `SupportedGameVersion`.

## Validation status

Source-level consistency and integrity-manifest checks are included. Windows .NET and Visual C++ compilation, publishing, and live DBXV2 testing remain required on the user's development machine.
