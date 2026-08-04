# PowerScaler Labs Codex Instructions

Read this file and `CODEX_HANDOFF.md` completely before changing code.

## Binding objective

PowerScaler Labs is a full Xenoverse 2 combat-stat and mechanical virtualization
system. Its end state is not larger values in Xenoverse float fields. PowerScaler
must own exact canonical fighter state and translate combat outcomes into bounded,
engine-safe Xenoverse execution values.

The binding numeric domains are:

1. Canonical state: exact values, including quadrillions and beyond.
2. Combat-effective state: canonical values after context, modifiers, and an
   explicit gameplay-compression policy.
3. Engine projection: bounded float32 values used only by Xenoverse execution.
4. Presentation state: canonical and normalized values shown to the user.

Never treat an engine float as canonical truth.

## Current safety boundary

- `PowerScalerLabs.Runtime` remains external and read-only.
- NativeProbe instrumentation is research infrastructure, not production stat
  authority.
- Do not enable stat writes, health writes, resource writes, damage writes, or
  controlled-write validation without explicit authorization in the current task.
- Do not claim an offset, writer, formula, or context is verified without
  repeatable evidence.
- HealthScale remains a sealed independent companion.
- Initial production virtualization is offline-only unless multiplayer safety and
  synchronization are explicitly designed and authorized.

## Engineering direction

- Build reusable mechanical targets and causal instruments, not one-off stat hacks.
- Bind evidence and virtual state to process, battle instance, fighter slot,
  generation, actor address, and proven identity/context.
- Prefer semantic combat boundaries over patching every downstream field writer.
- Keep canonical policy outside NativeProbe. In-process code should remain small,
  deterministic, bounded, and fail closed.
- Separate discovery, characterization, replay simulation, and production control.
- Every production control path must define restoration, detach, stale-generation,
  battle-transition, and fault behavior before it can be enabled.

## Required question before implementation

For every feature, identify:

- the mechanical target and target kind;
- the evidence state it advances;
- whether it affects canonical state, combat policy, engine projection, or display;
- the fighter/context binding;
- the failure and restoration behavior;
- how it advances full virtualization rather than only the current experiment.

The authoritative architecture is
`docs/POWERSCALER_FULL_BATTLECORE_VIRTUALIZATION_ARCHITECTURE_2026-08-03.md`.

