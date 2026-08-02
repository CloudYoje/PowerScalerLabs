# PowerScaler Labs — Native Causal Probe Foundation

This checkpoint adds the isolated Native Causal Probe foundation while preserving the completed causal-research cleanup. The old broad snapshot/recording/candidate-ranking workflow remains retired.

The goal of this branch is smaller and more deliberate:

```text
PowerScaler Labs
    ├─ Runtime
    ├─ Fighters
    ├─ Research
    ├─ Findings
    ├─ Diagnostics
    └─ Tools
```

## What stays

The useful runtime foundation remains intact:

- external query/read-only DBXV2 access;
- provider-based BattleCore resolution;
- fail-closed provider conflict handling;
- the 14-slot live `Battle_Mob` registry;
- fighter slot generations to distinguish pointer reuse;
- address provenance;
- guarded reads and read-budget diagnostics;
- known health anchors at `Battle_Mob + 0x100` and `+0x104`;
- correlated Ki/stamina chronology anchors;
- the isolated 25 ms chronology lane as **supporting temporal evidence**;
- the low-level object scanner as an **internal targeted memory-inspection primitive**, not a primary user workflow;
- HealthScale 1.1.1 as a sealed, separate companion under **Tools**.

## What was removed from the app

The following compiled app subsystems are removed:

- `PowerScalerLabs.App/Overlay`;
- `PowerScalerLabs.App/Recording`;
- the guided F11 experiment overlay;
- generic baseline/compare/full-snapshot controls;
- generic start/stop recording sessions;
- automatic candidate classification;
- candidate signal tiers and noise ranking;
- candidate promotion/rejection UI;
- scanner-observation grids as a primary research surface.

Git/source checkpoints are the archive for that retired research approach. The active application no longer carries both research philosophies at once.

## Current application model

### Fighters

Displays the currently validated live fighter objects and their slot generations. A fighter address is treated as a live object address, **not** as durable character identity. Character/preset identity remains a later causal-research gate.

### Research

Chronology is shown here only as a timeline-support tool. The page is intentionally prepared for the next component:

```text
PowerScalerLabs.NativeProbe
    ├─ data watchpoints
    ├─ code breakpoints
    ├─ register / XMM capture
    ├─ call-stack capture
    └─ bounded trace transport
```

The probe foundation is now included: explicit attach/detach, native ABI handshake, host/probe heartbeats, bounded transport storage, inert failure behavior, and verified unload. Hardware tracing and gameplay instrumentation are not included.

### Findings

Findings now means durable evidence, not a ranked candidate list. The initial table contains the known/correlated `Battle_Mob` resource anchors. Future code findings should progress through a causal evidence ladder such as:

```text
Observed
  ↓
Reproduced
  ↓
Code-anchored
  ↓
Causally validated
  ↓
Virtualization-ready
```

### Diagnostics

Shows fighter lifetime events, known-field events, app logs, runtime state, access budgets, and chronology health. Broad scanner observations are intentionally not surfaced.

### Tools

HealthScale remains independently packaged and managed here. It is not merged into the PowerScaler runtime.

## Runtime safety boundary

The managed PowerScaler Runtime remains external and read-only. Its observation layer continues to use query/read APIs and does not use game-memory writes, remote threads, hooks, or injection.

The separate ProbeHost performs privileged attachment only after an explicit App command. NativeProbe currently provides lifecycle and transport infrastructure only; it installs no watchpoints or hooks and makes no gameplay writes.

## Why the low-level scanner remains in the runtime

The broad scanner workflow is retired, but `ObjectCapabilityScanner` is deliberately retained below the UI. Once causal tracing identifies a specific live object or structure, a bounded snapshot/compare operation can still be useful as a microscope for questions like:

- which field differs between Goku and Vegeta;
- which nearby field changes across presets;
- which pointer child is stable for one live fighter instance.

It is no longer intended to answer causal questions such as “which of thousands of changes was stamina spending?”

## Build and launch on Windows

Run:

```text
START_HERE.cmd
```

The Windows publisher now runs the cleanup verifier, HealthScale integrity audit, Release builds, the runtime architecture self-test, and publishes the app/runtime/companion artifacts.

Expected outputs:

```text
artifacts\PowerScalerLabs\PowerScalerLabs.exe
artifacts\PowerScalerLabs\Runtime\PowerScalerLabs.Runtime.exe
artifacts\PowerScalerLabs\Probe\PowerScalerLabs.ProbeHost.exe
artifacts\PowerScalerLabs\Probe\PowerScalerLabs.NativeProbe.dll
artifacts\PowerScalerLabs\Companions\HealthScale\Payload\xinput_other.dll
artifacts\PowerScalerLabs\Companions\HealthScale\Payload\HealthScale.ini
```

## Next implementation gate

After repeated attach/detach validation, the next gate is the real gameplay writer for one known `Battle_Mob + 0x100` current-health field using a hardware write watchpoint.

See `NATIVE_CAUSAL_PROBE_FOUNDATION_AUDIT.md` for the new boundary and `CAUSAL_RESEARCH_CLEANUP_AUDIT.md` for the preserved cleanup boundary.
