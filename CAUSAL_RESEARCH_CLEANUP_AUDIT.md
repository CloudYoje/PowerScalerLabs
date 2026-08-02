# Causal Research Cleanup Audit

## Purpose

Prepare PowerScaler Labs for causal tracing by removing the app-layer workflow that treated broad memory snapshots, guided action recordings, and probabilistic candidate classification as the primary discovery method.

## Removed compiled app surface

- `src/PowerScalerLabs.App/Overlay/`
  - guided experiment catalog;
  - F11 global hotkey;
  - overlay state/controller;
  - guided baseline/compare/snapshot UX.
- `src/PowerScalerLabs.App/Recording/`
  - generic session recorder;
  - candidate store;
  - candidate grouping/classification layer.

The main window no longer contains pages for the broad scanner, recording sessions, or candidates/noise ranking.

## Rebuilt navigation

The active desktop application is now organized around:

1. Dashboard
2. Runtime
3. Fighters
4. Research
5. Findings
6. Diagnostics
7. Tools

HealthScale was demoted from a first-class research page/sidebar feature to the Tools page while remaining sealed and separately managed.

## Preserved research primitives

The cleanup intentionally does **not** delete useful low-level runtime capabilities:

- provider-based BattleCore discovery;
- 14 fighter slots;
- fighter generation identities;
- known health anchors;
- address provenance;
- raw guarded reads;
- read budgets;
- chronology sampler;
- `ObjectCapabilityScanner` as a bounded internal inspection primitive.

The distinction is architectural: these are tools beneath the research workflow, not the workflow itself.

## Findings model

The candidate taxonomy/signal-tier model is removed from the compiled app. Findings now surface durable anchors directly. The initial findings are:

- Current health: `Battle_Mob + 0x100` — Verified field
- Maximum health: `Battle_Mob + 0x104` — Verified field
- Current Ki: `Battle_Mob + 0x10C` — Correlated
- Maximum Ki: `Battle_Mob + 0x110` — Correlated
- Current stamina: `Battle_Mob + 0x16C` — Correlated
- Maximum stamina: `Battle_Mob + 0x170` — Correlated

No stronger claim is made for Ki/stamina in this cleanup.

## Research page

The Research page retains chronology controls:

- New Epoch
- Pause
- Resume
- Clear View

Chronology is explicitly labeled as supporting temporal evidence. There is no replacement fake tracer UI: the page states that the native causal probe is the next implementation gate.

## Build verification changes

`Verify-PowerScalerLabs.ps1` was rewritten so the build now fails if:

- the retired `Overlay` or `Recording` app directories return;
- legacy recording/candidate/overlay tokens are compiled into the app;
- old scanner/recording workflow controls reappear in `MainWindow.xaml`;
- the read-only runtime foundation or known protocol anchors disappear.

`Publish-Windows.ps1` no longer runs legacy deep audits whose acceptance criteria required the retired UI/recording model. It runs the cleanup verifier and the independent HealthScale companion audit before build/publish.

## Validation performed in packaging environment

Performed here:

- XML parse of the rebuilt WPF XAML and project files;
- event-handler name cross-check between `MainWindow.xaml` and code-behind;
- static search confirming the retired app namespaces/classes are absent;
- static search confirming retained runtime primitives remain present.

Not possible in this packaging environment:

- .NET Release compilation (the .NET SDK is not installed here);
- Windows WPF launch;
- DBXV2 live runtime test.

Those remain mandatory on the target Windows development system via `START_HERE.cmd`.

## Next gate

Add `PowerScalerLabs.NativeProbe` as an isolated in-process research component. Do not restore the removed broad discovery UX. First acceptance target: arm one known current-health address and identify the exact gameplay HP writer reproducibly.
## Publish Hotfix 1 — HealthScale audit alignment

The first Windows publish of Cleanup Pass 1 exposed two stale deep-audit requirements inherited from the pre-cleanup companion UI/version-gate era. The audit simultaneously required the retired `new Version(1, 25, 2, 0)` token while later forbidding hard HealthScale version gates, and it still required the removed `Companion Apps` page labels.

Hotfix 1 removes the obsolete version-literal requirement, strengthens the retired-version-gate check to reject the old `1.25.2.0` literal, and validates the compact HealthScale controls now housed under `Tools`. No frozen HealthScale source or runtime payload is changed.
