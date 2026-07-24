# HealthScale 1.1.1 — Unbounded Transformation Domain Candidate

This is a separate repair candidate. It does not replace or modify the frozen
HealthScale 1.0.1 source archive or the prior 1.1.0 source candidate.

## Scope

HealthScale remains health-only:

- native single-bar HUD normalization;
- current and maximum HP acquisition;
- percentage preservation when maximum HP changes;
- transformation, detransformation, scripted, buff/debuff and equipment health
  transitions;
- quest/additional HUD lane discovery;
- zero-frame and death protection;
- health-only diagnostics and tests.

No Ki, stamina, combat-scaling, lore, scanner, camera, overlay, or PowerScaler
Studio systems are included.

## 1.1.1 correction — no health-domain ceiling

The 1.1.0 candidate accepted mixed-domain health pairs up to 256x. That still
encoded an assumption about the strongest possible transformation. HealthScale
1.1.1 removes the ratio ceiling completely.

A mixed frame may contain current HP from any previous transformation domain and
maximum HP from the new domain. The pair is accepted when the values are finite,
the maximum is positive, and the Battle_Mob has already passed ownership,
vtable, and memory-range verification.

The runtime does **not** derive a new health percentage from an incoherent pair.
It freezes the last coherent percentage until the new maximum and current-health
domain stabilize.

## Core percentage contract

```text
saved ratio = coherent current HP / coherent maximum HP
corrected current HP = saved ratio * stabilized new maximum HP
```

No 8x, 256x, Super Saiyan God, Super Saiyan Blue, or custom-transformation
multiplier threshold participates in health validity. The practical numeric bound
is the finite range of the game's 32-bit floating-point health fields.

## Retained 1.1.0 repairs

- Quest/additional HUD lane discovery and normalization.
- Independent transition tracking for discovered lanes.
- Kaioken x20 target-domain rebase protection.
- Zero-frame and death safeguards.

## Build

Open `HealthScale.sln` in Visual Studio and build `Release|x64`.
The loader-compatible output remains `xinput_other.dll`.

## Model test

```text
g++ -std=c++20 tests/native/health_transition_model_tests.cpp \
  src/native/HealthScale.Runtime/src/health_transition_model.cpp \
  -o health_transition_model_tests
./health_transition_model_tests
```

See `FIX_REPORT.txt`, `VALIDATION_REPORT.txt`, and
`QUEST_RUNTIME_TEST_PLAN.md` before live testing.
