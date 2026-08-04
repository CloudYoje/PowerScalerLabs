# Codex Handoff

## Project direction

PowerScaler Labs will virtualize Xenoverse 2 combat by maintaining exact canonical
fighter state outside the game's float32 stat model. Xenoverse remains the real-time
animation, collision, AI, mission, and bounded execution engine.

The intended flow is:

```text
Canonical profile
    -> virtual fighter
    -> deterministic combat kernel
    -> bounded execution projection
    -> Xenoverse combat and presentation adapters
```

Quadrillion-scale values must not be written directly into native float fields.
Float32 has sufficient exponent range but insufficient precision and unknown engine
clamps. Around 10^15, adjacent float32 values are roughly 67 million apart.

## Current evidence

- Runtime Protocol 8, Probe Protocol 3, Native ABI 3.
- `Battle_Mob + 0x100`: current health, live read verified.
- `Battle_Mob + 0x104`: maximum health, live read verified.
- `Battle_Mob + 0x10C`: current Ki, source-backed candidate; live validation pending.
- `Battle_Mob + 0x16C`: current stamina, source-backed candidate; live validation pending.
- `Battle_Mob + 0x110` and `+0x170`: capacity hypotheses; correlated only.
- DR0 HP tracing works with transactional thread instrumentation and clean disarm.
- A stable direct HP-subtraction writer has been observed at `DBXV2.exe+0xC2BFE`
  for the tested DBXV2 build. This is evidence for pipeline discovery, not authority
  to write or a universal version-independent binding.
- Training-mode recovery can restore end-state HP; transient subtraction evidence
  must therefore be retained independently of final HP.

## Immediate implementation direction

1. Specify exact canonical numeric types and typed virtual-fighter domains.
2. Specify replayable combat-event and combat-result contracts.
3. Build an offline deterministic combat-kernel simulator with no game writes.
4. Continue read-only Ki/stamina behavioral validation.
5. Use causal tracing to map the semantic damage pipeline: attacker, defender,
   move/category, modifiers, final damage, HP mutation, KO, and exceptional routes.
6. Do not introduce production substitution until replay, identity binding,
   restoration, and fault behavior are proven.

## Repository state

The working tree contains cumulative intentional changes and intentional deletion of
historical root audit reports. Do not revert unrelated changes. No public release,
push, or automatic commit is authorized merely by running local build scripts.

Managed builds, Runtime architecture self-test, Probe architecture self-test,
native `/W4 /WX` build, native transport tests, frozen HealthScale audit, and local
Windows publishing passed at the last checkpoint. Live Ki/stamina validation and
full in-game virtualization remain unfinished.

