# PowerScaler Labs

PowerScaler Labs is a Xenoverse 2 combat-stat and mechanical virtualization research
system. Its long-term purpose is to represent canonical character power, including
values in the quadrillions and beyond, without depending on Xenoverse's bounded
float32 stat fields as authoritative state.

## Architectural objective

```text
Exact canonical profile
        -> virtual fighter state
        -> deterministic combat kernel
        -> bounded Xenoverse execution projection
        -> animation, collision, AI, missions, and presentation
```

Canonical values, combat-effective values, engine projection, and presentation are
separate domains. Large canonical numbers are not written directly into Xenoverse
resource fields. The game receives bounded values that preserve the outcome selected
by PowerScaler's combat policy.

## Current checkpoint

The repository currently provides:

- external read-only DBXV2 observation;
- structural BattleCore and fighter-lifetime discovery;
- a 14-slot live `Battle_Mob` registry with generation identity;
- address provenance, guarded reads, chronology, and read-budget diagnostics;
- explicit ProbeHost/NativeProbe attachment and verified unload;
- bounded native event transport;
- transactional DR0 write observation with register and selected SIMD evidence;
- controller-friendly HP research workflow and automatic disarm;
- HealthScale 1.1.1 as a sealed independent companion.

Current field evidence:

```text
Battle_Mob + 0x100  Current health       live verified
Battle_Mob + 0x104  Maximum health       live verified
Battle_Mob + 0x10C  Current Ki           source-backed candidate
Battle_Mob + 0x16C  Current stamina      source-backed candidate
Battle_Mob + 0x110  Maximum Ki           correlated hypothesis
Battle_Mob + 0x170  Maximum stamina      correlated hypothesis
```

The HP writer trace proves a causal-research method. It does not mean full damage,
resource, modifier, KO, revival, or stat virtualization is complete.

## Safety boundary

`PowerScalerLabs.Runtime` remains external and read-only. NativeProbe is an isolated
research instrument. The current checkpoint does not authorize production stat,
health, resource, or damage writes. HealthScale remains independently built and
managed under Tools.

## Next research

Near-term work is to define exact canonical numeric and combat-event contracts,
construct a deterministic offline combat-kernel simulator, validate Ki/stamina reads,
and map Xenoverse's semantic damage pipeline. Production substitution comes only
after replay evidence, fighter/context binding, restoration, and fail-closed behavior
are proven.

## Build and launch

Run the existing local build:

```text
START_HERE.cmd
```

Force a complete verified local rebuild:

```text
START_HERE.cmd /build
```

Expected local outputs:

```text
artifacts\PowerScalerLabs\PowerScalerLabs.exe
artifacts\PowerScalerLabs\Runtime\PowerScalerLabs.Runtime.exe
artifacts\PowerScalerLabs\Probe\PowerScalerLabs.ProbeHost.exe
artifacts\PowerScalerLabs\Probe\PowerScalerLabs.NativeProbe.dll
```

These commands create local artifacts. They do not publish or release the project
publicly.

## Documentation

- `AGENTS.md`: binding instructions for Codex and coding agents.
- `CODEX_HANDOFF.md`: current evidence, state, and immediate direction.
- `docs/POWERSCALER_FULL_BATTLECORE_VIRTUALIZATION_ARCHITECTURE_2026-08-03.md`:
  authoritative virtualization architecture.
- `docs/XENOVERSE_2_ANATOMY_CASE_STUDY_2026-08-04.md`: game/content anatomy and
  research provenance.

