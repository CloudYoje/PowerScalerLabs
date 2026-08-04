# PowerScaler Labs — Full BattleCore Stat Virtualization Architecture

**Status:** Authoritative architecture update for Codex implementation  
**Date:** 2026-08-03  
**Supersedes:** Narrow HP/resource-trace framing as the project-level architecture  
**Preserves:** Existing PowerScaler scaling rules, identity rules, HealthScale boundary, Runtime read-only boundary, ProbeHost/NativeProbe privilege split, and already-proven causal telemetry work

---

## 1. Why this document exists

Recent causal-probe work proved a hardware write-watch workflow against `Battle_Mob + 0x100` (current HP). That was a **bootstrap experiment**, not a redefinition of PowerScaler around health.

The original project documentation was broader. Workspace Prep historically covered not only Health/Ki/Stamina but attacks, defenses, movement, and selected recharge/revival fields. The Full Mechanical Discovery Matrix likewise covered the complete combat-mechanical matrix: resources, damage categories, guard, movement, healing/regeneration, revival, transformations, temporary modifiers, Super Souls, context, clamps, and other runtime behavior.

This architecture update makes that full scope explicit for the current native causal-research era.

**Project goal: Dragon Ball Xenoverse 2 stat/mechanical virtualization across the full relevant BattleCore combat model.**

Health, Ki, and Stamina are only three members of that model.

---

## 2. Binding architectural objective

PowerScaler must eventually separate:

1. **Canonical / logical combat state** — the values and relationships PowerScaler wants to represent.
2. **Virtualized stat/mechanical state** — the authoritative logical runtime state owned by PowerScaler.
3. **Bounded Xenoverse execution state** — safe values that XV2 can execute without overflow, broken HUD/scouter behavior, no-damage states, or other engine limits.
4. **Presentation state** — normalized/percentage-oriented UI where appropriate.

Conceptually:

```text
Canonical / Lore Model
        ↓
PowerScaler Virtual Fighter
        ↓
Virtual Stat + Mechanical State
        ↓
Translation / Execution Adapter
        ↓
Bounded Xenoverse Runtime Values
        ↓
DBXV2 combat execution
```

PowerScaler is not merely a file editor and is not merely a Health/Ki/Stamina overlay.

The long-term runtime goal is to make PowerScaler the authoritative scaling layer while Xenoverse remains the bounded execution engine.

---

## 3. Full virtualization scope

The project must not be architected around a fixed list of only three resources.

The research and virtualization model must be able to represent all relevant combat mechanics discovered in or attached to live fighter/BattleCore state.

### 3.1 Resource / capacity mechanics

Known or expected categories include:

- Current Health
- Maximum Health
- Current Ki
- Maximum Ki
- Current Stamina
- Maximum Stamina
- Health capacity/scalar application
- Ki capacity application
- Stamina capacity application
- Resource initialization
- Resource consumption
- Resource recovery
- Resource recovery rate
- Resource cost
- Resource clamp behavior
- HUD/presentation normalization where appropriate

Current evidence anchors include:

```text
Battle_Mob + 0x100  Current Health
Battle_Mob + 0x104  Maximum Health
Battle_Mob + 0x10C  Current Ki source-backed candidate
Battle_Mob + 0x110  Maximum Ki correlated candidate
Battle_Mob + 0x16C  Current Stamina source-backed candidate
Battle_Mob + 0x170  Maximum Stamina correlated candidate
```

The health fields are live-verified. XV2 Patcher source independently identifies the
current Ki and current stamina fields, but PowerScaler still requires live behavioral
validation. The two capacity fields must not be promoted from correlated candidates
without separate evidence. These offsets are **research targets**, not the definition
of the entire system.

### 3.2 Offense / damage mechanics

The catalog must support discovery and eventual virtualization of:

- Basic melee damage
- Basic Ki blast damage
- Strike skill damage
- Ki blast skill damage
- Ultimate damage
- Evasive damage
- Grab damage
- Incoming damage
- Shared damage-processing paths
- AttackRank application
- Vanilla Basic Attack input
- Vanilla Strike input
- Vanilla Ki Blast input
- Technique/category coefficients
- Final damage
- Minimum positive damage
- Maximum damage
- Zero-damage behavior
- Scripted damage
- Environmental damage
- Self-damage
- Poison / damage-over-time
- Other exceptional damage routes discovered later

Do not assume every damage category has its own persistent Battle_Mob field.

Many important values may exist only transiently in registers or call frames.

### 3.3 Defense / durability / guard mechanics

The architecture must support:

- DurabilityRank application
- Incoming damage multiplier/reduction
- Guard state
- Guard damage
- Stamina damage
- Stamina break
- Break recovery
- Invulnerability/immunity/zero-damage paths
- Defensive temporary modifiers
- Transformation-linked durability modifiers
- Super Soul defensive modifiers

### 3.4 Recovery / revival mechanics

The architecture must support:

- Direct healing
- Health regeneration
- Ki recovery
- Stamina recovery
- Recovery rates
- Downed state
- Revival state
- Revival progress
- Revival speed/rate
- Restored-state application
- Training reset behavior
- Scripted restoration
- Transformation-linked recovery changes
- Super Soul recovery changes

**Revival speed is a first-class virtualization target.**

### 3.5 Movement / speed mechanics

The architecture must support:

- SpeedRank application
- Ground movement
- Air movement
- Boost movement
- Dash / step movement
- Acceleration
- Movement duration
- Movement costs
- Boost stamina-consumption rate
- Temporary movement buffs/debuffs
- Transformation movement modifiers
- Super Soul movement modifiers

### 3.6 Scaling inputs and formula stages

The research model must explicitly allow non-field targets such as:

- AttackRank
- DurabilityRank
- SpeedRank
- KiRank
- StaminaRank
- HealthPoolScalar
- Raw rank difference
- Rank-gap clamp
- Combat compression
- Raw multiplier
- Final multiplier
- Multiplier minimum clamp
- Multiplier maximum clamp
- Final damage
- Damage minimum clamp
- Damage maximum clamp
- Zero-damage handling

These may be stored values, transient register values, calculated outputs, call arguments, or code paths.

Do not force them into a memory-field abstraction.

### 3.7 Transformations and modifier systems

Support discovery and virtualization of:

- Transformation stage
- Awoken state
- Attack modifiers
- Durability modifiers
- Resource/capacity modifiers
- Movement modifiers
- Recovery modifiers
- Temporary buffs
- Temporary debuffs
- Super Soul activation state
- Super Soul modifiers
- PUP-linked transformation modifiers
- Character-specific transformation behavior
- Shared transformation behavior

### 3.8 Context and identity

Virtualized mechanics must be bound to correct fighter lifetimes and combat context.

Support:

- Character identity
- Costume
- Model preset
- Skill set
- Awoken skill
- Team
- Fighter slot
- Fighter generation
- Battle instance
- Mission
- Quest stage
- Boss state
- Giant state
- Body swap
- Training reset
- Forced mission transition
- Other exceptional contexts found during research

Never infer identity solely from pointer adjacency or current lock-on target.

---

## 4. Mechanical Target Catalog

The Research page must evolve toward a **Mechanical Target Catalog**, not a hard-coded HP/Ki/Stamina selector.

A target is metadata describing something PowerScaler wants to discover, verify, characterize, or virtualize.

Suggested conceptual model:

```text
MechanicalTarget
    TargetId
    DisplayName
    Family
    Kind
    Status
    Binding
    ValueType
    AccessType
    Width
    FighterScope
    ContextRequirements
    Evidence
    Confidence
    VirtualizationStatus
```

### 4.1 Target families

At minimum:

```text
Resources / Capacity
Damage / Offense
Defense / Guard
Recovery / Revival
Movement / Speed
Scaling Inputs
Formula Stages
Transformations / Modifiers
Context / Identity
Exceptional Mechanics
```

The catalog must be extensible.

Do not encode an assumption that the final count is 3, 6, 51, or 120. The historical discovery matrix is a baseline coverage map; new targets may be discovered.

### 4.2 Target kinds

A target may be:

```text
StoredField
ReaderFunction
WriterFunction
ExecutionSite
ResolverFunction
CallBoundary
RegisterValue
FormulaStage
StateFlag
IdentityBinding
ContextBinding
ModifierApplication
Clamp
PresentationAdapter
```

This is important: **not every virtualized mechanic is an address offset.**

---

## 5. Research instruments are generic

The DR0 HP experiment proved one research instrument. It must not become synonymous with the entire research system.

PowerScaler should support a toolkit of causal instruments:

```text
Mechanical Target
        │
        ├── Passive field observation
        ├── Hardware memory write watch
        ├── Hardware memory access watch (when justified)
        ├── Execution anchor
        ├── GPR context capture
        ├── XMM/SIMD value-flow capture
        ├── Caller/callee characterization
        ├── Code-window capture / decoding
        ├── Fighter lifetime correlation
        ├── Action/context correlation
        └── Controlled write verification only when explicitly authorized
```

The instrument selected depends on `TargetKind`.

Examples:

- Current Health: stored field + DR write watch is appropriate.
- Revival speed: may require field observation, code-access tracing, or formula characterization.
- Final damage multiplier: may be register/value-flow only.
- Shared damage resolver: likely execution/caller characterization.
- Transformation modifier application: may require state + execution correlation.

Do not build separate architecture gates for every stat.

Build reusable research primitives and use controlled in-game experiments to characterize individual targets.

---

## 6. Evidence lifecycle

Keep discoveries evidence-driven.

Recommended states:

```text
Unknown
Observed
Candidate
StrongCandidate
VerifiedRead
VerifiedWrite
VerifiedRestoration
Characterized
VirtualizationCandidate
ProductionReady
```

A target can have multiple independent evidence dimensions.

Examples:

```text
Stored field known             ≠ writer known
Writer known                   ≠ formula known
Formula known                  ≠ source inputs known
Read verified                  ≠ safe write verified
Safe write verified            ≠ safe restoration verified
One character verified         ≠ universal fighter mechanic
One battle mode verified       ≠ all contexts verified
```

Do not silently promote a target because a nearby target is understood.

---

## 7. Causal HP result and what it means

The current HP work proved the **method**, not the project scope.

Proven capability:

```text
Validated live fighter generation
        ↓
Known mechanic binding
        ↓
Transactional hardware watchpoint
        ↓
Native exception capture
        ↓
Machine context
        ↓
ProbeHost/App evidence
        ↓
Clean restoration
```

That capability should now be generalized for the full Mechanical Target Catalog.

The successful HP writer characterization is a research finding under the Health target family. It does not justify restructuring the application around Health.

Freeze the proven DR0/VEH plumbing except for generic extensions required by new target kinds.

---

## 8. Virtual Fighter model

PowerScaler ultimately needs a per-live-fighter virtual state object.

Conceptually:

```text
VirtualFighter
    FighterIdentity
    FighterLifetime
    CanonicalProfile
    VirtualStats
    VirtualResources
    VirtualModifiers
    VirtualContext
    EngineBindings
```

### 8.1 Fighter identity/lifetime

At minimum:

```text
ProcessId
BattleInstanceId
Slot
SlotGeneration
ActorAddress
AcquiredQpc
ReleasedQpc
Character/Preset identity when proven
```

Pointer alone is never permanent identity.

### 8.2 Virtual stat domains

Do not use one flat `Dictionary<string,float>` as the only architecture.

Use typed domains where semantics matter, while retaining an extensible target registry.

Possible domains:

```text
VirtualResources
VirtualOffense
VirtualDefense
VirtualMovement
VirtualRecovery
VirtualRevival
VirtualScaling
VirtualModifiers
VirtualContext
```

### 8.3 Engine execution state

For each mechanic PowerScaler eventually controls, track the relationship between:

```text
Logical / virtual value
Engine-safe execution value
Observed engine value
Normalization / presentation value
Binding confidence
```

The engine value is an implementation detail of the XV2 execution adapter, not the canonical truth.

---

## 9. Normalization and percentage representation

Health, Ki, and Stamina are obvious normalized resources:

```text
Normalized = Current / Maximum
```

Other mechanics may need different normalized representations.

Do **not** force every stat into a percentage if a percentage has no correct semantic meaning.

Instead, the architecture should support:

```text
Raw engine value
Virtual logical value
Normalized representation when defined
Canonical/rank representation when defined
```

For resources, normalized current/max percentage is binding.

For attack, durability, speed, revival rate, movement, or formula stages, define normalization according to the final virtualization model rather than assuming resource semantics.

---

## 10. Research UI direction

The Research page should eventually become a general mechanical-research workspace.

Conceptual layout:

```text
RESEARCH

Fighter Target
  Slot / Generation / Actor / Identity / live state

Mechanical Family
  [Resources]
  [Damage]
  [Defense]
  [Recovery]
  [Movement]
  [Scaling]
  [Modifiers]
  [Context]

Mechanical Target
  <catalog-driven list>

Known Binding
  address / RVA / function / formula / unknown

Evidence State
  Unknown / Candidate / Verified / Characterized / ...

Research Instrument
  passive observation
  write watch
  execution trace
  value-flow capture
  ...

[ARM / START TRACE]
[STOP]

Live Evidence
  target values
  normalized values where applicable
  trace events
  register/XMM data
  fighter correlations
  code sites
  context
```

Only show actions valid for the selected `TargetKind`.

Do not hard-code the Research page around HP.

---

## 11. Historical Full Mechanical Discovery coverage

The historical Full Mechanical Discovery Matrix remains a useful coverage baseline and should be retained as a checklist/reference, even though the old broad differential scanner is retired.

It included controlled experiments for:

- idle control
- Ki spend/recovery
- stamina spend/recovery
- stamina damage/break/recovery
- incoming health damage
- basic melee
- basic Ki
- Strike skill
- Ki skill
- Ultimate
- damaging Evasive
- grab
- guard state / guarded damage
- ground movement
- air movement
- boost movement
- dash/step
- direct healing
- regeneration
- poison/DoT
- environmental damage
- self-damage
- scripted damage
- downed/revival
- temporary buff
- temporary debuff
- Super Soul activation
- transformation
- health capacity change
- boss state
- giant state
- body swap
- mission context
- quest-stage context
- minimum damage clamp
- maximum damage clamp
- zero-damage control
- training reset
- forced mission transition
- battle exit

The old *differential candidate-ranking implementation* is retired.

The **mechanical coverage intent is not retired.**

Modern causal instrumentation replaces the old noisy discovery method.

---

## 12. Implementation boundaries that remain binding

### Runtime

`PowerScalerLabs.Runtime` remains external and read-only.

It owns passive observation, fighter registry/lifetimes, structural BattleCore discovery, chronology, and supporting evidence.

It must not become the write/injection lane.

### ProbeHost

`PowerScalerLabs.ProbeHost` remains the isolated privileged managed lane.

It owns explicit attachment, NativeProbe lifecycle, privileged enrichment, and instrumentation control.

### NativeProbe

`PowerScalerLabs.NativeProbe.dll` contains only the native code necessary for in-process causal instrumentation.

Do not turn it into a second application or policy engine.

### HealthScale

HealthScale remains a sealed companion boundary unless the user explicitly changes that policy.

Do not merge or refactor HealthScale source into PowerScaler.

### Version independence

Do not reintroduce hard `ExpectedGameVersion` / supported-version gating as the primary discovery architecture.

Prefer structural live discovery and runtime evidence.

---

## 13. Codex implementation rule

Before implementing a new research feature, Codex must answer:

1. Which `MechanicalTarget` does this feature serve?
2. Which target family does it belong to?
3. Is it a stored field, code path, formula stage, context binding, or modifier?
4. Which existing generic research instrument can investigate it?
5. Does a new reusable instrument need to be added?
6. What evidence upgrades its status?
7. How does this contribute to eventual virtualization?
8. Does the implementation accidentally narrow the UI/model around one stat?

If the answer to #8 is yes, redesign it generically.

---

## 14. Immediate direction from the current checkpoint

Do **not** create separate architecture gates named:

```text
Health Gate
Ki Gate
Stamina Gate
Revival Gate
Speed Gate
...
```

when they merely repeat the same research primitive.

Instead:

1. Preserve the proven HP watchpoint result as the first causal target.
2. Generalize HP-specific session/UI naming toward `MechanicalTrace` / `MechanicalTarget`.
3. Seed the target catalog with all already-known bindings.
4. Represent the historical Full Mechanical Discovery targets as catalog entries, including unknown bindings.
5. Allow a target to select the appropriate research instrument.
6. Continue controlled in-game characterization target by target.
7. Add new native instrumentation only when a target requires a genuinely new reusable primitive (for example XMM value-flow capture or execution/caller tracing).
8. Accumulate verified bindings and formulas into the virtualization model.
9. Only enable production writes/virtualization per mechanic after read/write/restoration/context safety is proven.

---

## 15. Near-term research priority

The successful HP trace gives a high-confidence direct HP-subtraction writer candidate and proves hardware-watch causality.

The next research work should be chosen based on information value, not because Health happens to be first.

Near-term useful branches include:

- characterize the HP damage writer across controlled damage categories;
- capture XMM/SIMD values when needed to follow damage quantities;
- discover central damage-resolution/caller paths;
- use the same generic memory-watch primitive on known Ki/Stamina fields;
- characterize recovery/revival paths;
- begin mapping rank/formula stages once execution anchors exist.

The order may change based on live evidence.

The architecture must not.

---

## 16. Definition of the project

**PowerScaler Labs is a Xenoverse 2 full combat-stat/mechanical virtualization and causal-research system.**

It must be capable of learning and ultimately controlling the relevant fighter/BattleCore mechanics needed to represent canonical power relationships safely inside Xenoverse's bounded runtime.

It is not:

- an HP scaler;
- a resource-bar scaler;
- a three-stat tracer;
- a per-stat collection of unrelated hacks;
- a hard-version-address table;
- the retired broad snapshot candidate scanner.

The current causal-probe work is the instrumentation foundation for the larger virtualization system described here.
