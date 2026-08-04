# PowerScaler Labs — Full BattleCore Character-Stat Virtualization Architecture

**Status:** Authoritative architecture update for PowerScaler Labs / Codex implementation  
**Date:** 2026-08-04  
**Supersedes:** The 2026-08-03 BattleCore virtualization architecture wherever it treats native XV2 stat values as bounded execution values that retain statistical authority  
**Preserves:** Existing fighter identity/lifetime work, Mechanical Target Catalog direction, causal research instrumentation, Runtime read-only boundary, ProbeHost/NativeProbe privilege split, HealthScale companion boundary, version-independence doctrine, and already-proven causal telemetry work

---

# 1. Why this revision exists

PowerScaler Labs is not trying to make Xenoverse 2's native character-stat floats large enough, precise enough, or flexible enough to represent the desired roster hierarchy.

That is the wrong abstraction.

The native character-stat system is to be **flattened into a universal safe statistical substrate**. Once a statistic is fully virtualized, the native value for that statistic has no character-specific authority.

PowerScaler owns the real fighter statistics.

Xenoverse continues to own character identity, models, skills, animations, hitboxes, move timing, battle state, and other non-statistical mechanics unless a specific mechanic must be intercepted to apply a virtual character statistic.

The project goal is therefore:

> **Replace Xenoverse 2's character-stat authority with an independent virtual fighter-stat runtime while retaining Xenoverse as the mechanics and presentation host.**

Health, Ki, Stamina, damage, defense, recovery, revival, transformation stat changes, movement-related fighter stats, and every other character-owned BattleCore statistic are members of the same virtualization mission.

---

# 2. Binding architectural invariant

For every fully virtualized character statistic:

```text
Native character-specific statistical influence = 0
```

The native game may still require a valid value in memory or in its files, but that value is a safe compatibility/proxy value, not the fighter's true statistic.

Conceptually:

```text
CHARACTER SELECTED
        │
        ├──────────────────────────┐
        │                          │
        ▼                          ▼
XV2 IDENTITY / MECHANICS     POWERSCALER PROFILE RESOLUTION
        │                          │
        ▼                          ▼
UNIVERSAL SAFE NATIVE        AUTHORITATIVE VIRTUAL PROFILE
STATISTICAL SUBSTRATE              │
        │                          ▼
        │                   VIRTUAL FIGHTER STATE
        │                          │
        └──────────────┬───────────┘
                       ▼
                XV2 BATTLE RUNTIME
                       │
                       ▼
                AUTHORITY FIREWALL
                       │
            native character magnitude
                  terminates here
                       │
                       ▼
               VIRTUAL STAT LOGIC
                       │
                       ▼
               SAFE NATIVE PROXIES
                       │
                       ▼
             HUD / state / presentation
```

The native float32 character-stat space is not the PowerScaler hierarchy space.

It is not the canonical stat space.

It is not the Moderate stat space.

It is not the Vanilla virtual stat space.

It is only the neutral host substrate and proxy space required to keep Xenoverse running.

---

# 3. Universal native statistical substrate

PowerScaler must flatten every character-specific native statistic that it controls.

The invariant is:

```text
NativeStat(Goku)
=
NativeStat(Jiren)
=
NativeStat(Hercule)
=
NativeStat(Nail)
=
NativeStat(any other fighter)
```

for every PowerScaler-controlled stat channel.

This does not require every field to use the same literal value. Different engine fields may require different safe neutral values.

Conceptually:

```text
Native Health Maximum       = SAFE_HEALTH
Native Ki Maximum           = SAFE_KI
Native Stamina Maximum      = SAFE_STAMINA
Native Basic Attack         = NEUTRAL_ATTACK
Native Strike               = NEUTRAL_STRIKE
Native Ki Blast             = NEUTRAL_KI_BLAST
Native Defense              = NEUTRAL_DEFENSE
Native Recovery             = NEUTRAL_RECOVERY
Native Revival              = NEUTRAL_REVIVAL
Native Movement modifiers   = NEUTRAL_MOVEMENT
...
```

"Flat" means **character-invariant and statistically neutral**, not "every value equals 1.0."

Safe native values exist only because Xenoverse requires valid state.

They do not define fighter power.

---

# 4. Native character files are not runtime authority

Once virtualization is complete for a channel, vanilla character files are no longer the source of truth for that channel.

Native files may still serve three limited purposes:

1. engine compatibility;
2. source material for reconstructing the **Vanilla Scaling Doctrine**;
3. research evidence about how original Xenoverse behaved.

They must not remain a hidden contributor to the live virtual result.

Wrong model:

```text
Native character stat
    × PowerScaler residual
    = desired result
```

Correct model:

```text
Native character stat contribution = neutral

PowerScaler virtual stat
    = authoritative result
```

PowerScaler must not preserve residual character-stat authority simply because the original game already expresses part of a desired difference.

---

# 5. Authority Firewall

PowerScaler must establish an explicit **Authority Firewall** between Xenoverse's native character-stat system and PowerScaler-controlled statistical outcomes.

```text
XV2 CHARACTER STAT SOURCES
          │
          ▼
   AUTHORITY FIREWALL
          │
          │ native character magnitude
          │ cannot pass through
          ▼
   XV2 MECHANICAL EVENT
          │
          ▼
  POWERSCALER VIRTUAL LOGIC
          │
          ▼
  AUTHORITATIVE VIRTUAL STATE
```

The firewall requirement applies not only to PSC/base character data but to every native character-stat contributor discovered during research.

Examples include:

- base character stat files;
- level scaling;
- race/gender modifiers;
- attributes;
- QQ Bang/equipment contributions;
- transformation stat modifiers;
- Super Souls;
- temporary buffs;
- temporary debuffs;
- reinforcement skills;
- recovery modifiers;
- regeneration;
- revival modifiers;
- boss/NPC stat modifiers;
- preset-specific modifiers;
- mission-specific fighter modifiers;
- other discovered character-stat sources.

If one of these can alter an actual PowerScaler-controlled outcome without changing `VirtualFighterState`, an **authority leak** remains.

---

# 6. Character identity remains native; statistical identity does not

PowerScaler keeps Xenoverse's ability to identify and instantiate fighters.

Relevant native/runtime identity may include:

```text
Character
Preset
Costume
Model
Skill set
Awoken skill
CAC identity
Fighter slot
Fighter generation
Battle instance
Body-swap state
Team
Current transformation state
```

Xenoverse answers:

```text
Who is this fighter and what game mechanics/assets are active?
```

PowerScaler answers:

```text
What are this fighter's actual statistics?
```

These responsibilities must not be conflated.

---

# 7. VirtualFighterState is the actual fighter-stat state

Every active fighter receives one authoritative `VirtualFighterState`.

Conceptually:

```text
VirtualFighterState
│
├── Identity
│   ├── Character
│   ├── Preset
│   ├── Costume
│   ├── FighterGeneration
│   ├── BattleInstance
│   ├── CACIdentity
│   └── CurrentTransformation
│
├── Resources
│   ├── Health
│   │   ├── Current
│   │   └── Maximum
│   ├── Ki
│   │   ├── Current
│   │   └── Maximum
│   └── Stamina
│       ├── Current
│       └── Maximum
│
├── Offense
│   ├── BasicMelee
│   ├── BasicKi
│   ├── Strike
│   ├── KiBlast
│   ├── Ultimate
│   └── DiscoveredDamageChannels
│
├── Defense
│   ├── General
│   ├── BasicMelee
│   ├── BasicKi
│   ├── Strike
│   ├── KiBlast
│   └── DiscoveredDefenseChannels
│
├── Recovery
│   ├── Health
│   ├── Ki
│   ├── Stamina
│   └── ContextSpecificRecovery
│
├── Revival
│
├── MovementRelatedCharacterStats
│
├── TransformationModifiers
│
├── TemporaryModifiers
│
├── ContextualCharacterModifiers
│
└── AdditionalDiscoveredCharacterStats
```

The schema is extensible.

The final ontology is discovered by research rather than limited to Health/Ki/Stamina or to the six visible Xenoverse attributes.

---

# 8. Virtual numeric domain

PowerScaler virtual values must not inherit Xenoverse's float32 limits.

The architecture should expose a dedicated abstraction such as:

```text
VirtualScalar
```

The exact internal representation is an implementation decision, but it must support ranges and relationships beyond native float32 where required.

Candidate capabilities:

```text
Compare
Add
Subtract
Multiply
Divide
Ratio
Exact relationship metadata
LogMagnitude
Proxy conversion
Serialization
Deterministic validation
```

Possible implementation strategies include:

- high-precision decimal;
- mantissa + arbitrary exponent;
- rational relationships where exact multipliers matter;
- logarithmic support for comparison and reporting.

A transformation relationship such as `×50` should remain exactly `×50` in canonical metadata even if a selected scaling doctrine applies another value.

Native float limitations must never rewrite the authoritative virtual relationship.

---

# 9. No matchup normalization

A fighter's base virtual profile must be invariant across opponents.

Jiren does not receive one profile against Hercule and another against UI Goku.

For a fixed Scaling Doctrine:

```text
Resolve(Jiren, vs Hercule)
=
Resolve(Jiren, vs UI Goku)
=
Resolve(Jiren, vs Jiren)
```

apart from legitimate external/dynamic battle effects.

There is no battle-local power scaling.

There is no opponent-relative character normalization.

There is no native-safe "projection" that changes the fighter's authoritative virtual stat coordinate.

The opponent only changes the **interaction**, because the opponent has different virtual statistics.

Example:

```text
Damage(
    Jiren.VirtualAttack,
    Hercule.VirtualDefense,
    move mechanics,
    active virtual modifiers
)
```

versus:

```text
Damage(
    Jiren.VirtualAttack,
    UI_Goku.VirtualDefense,
    move mechanics,
    active virtual modifiers
)
```

Jiren's own virtual statistic does not move.

---

# 10. Proxy values are presentation/compatibility state, not fighter magnitude

Some Xenoverse systems will still require a bounded native value.

That value is a proxy.

Example:

```text
Jiren Virtual Maximum HP:
6,943,859,204,949,382

Jiren Virtual Current HP:
3,471,929,602,474,691

Virtual Health Ratio:
50%
```

A safe native mirror may be:

```text
Native Proxy Maximum HP:
10,000

Native Proxy Current HP:
5,000
```

The native `10,000` is not Jiren's health.

It is a carrier for `50%`.

Conceptually:

```text
Virtual Current / Virtual Maximum
              │
              ▼
        Normalized State
              │
              ▼
         Safe Native Proxy
```

This is not character-power normalization.

The authoritative virtual magnitude remains unchanged.

---

# 11. Proxy synchronization is one-way by default

Normal authority flow:

```text
Virtual State
     ↓
Native Proxy
```

not:

```text
Native Proxy
     ↓
Virtual State
```

If Xenoverse changes a native proxy unexpectedly, PowerScaler must not blindly copy the proxy backward into the virtual state.

Instead:

```text
Native mutation detected
        │
        ▼
Identify semantic event
        │
        ▼
Determine virtual meaning
        │
        ▼
Update VirtualFighterState
        │
        ▼
Regenerate native proxy
```

A raw proxy mutation is evidence of an event or an authority leak, not automatically authoritative character state.

---

# 12. KO, healing, Ki, and stamina authority

## 12.1 Health / KO

Actual defeat should eventually be based on:

```text
VirtualCurrentHealth <= 0
```

PowerScaler then drives whatever safe native condition is necessary for Xenoverse to execute its normal KO mechanics.

Native proxy health must not independently decide the virtual fighter's durability.

## 12.2 Healing

Healing should update virtual health first:

```text
Healing event
    ↓
Virtual healing calculation
    ↓
VirtualCurrentHealth
    ↓
Proxy synchronization
```

## 12.3 Ki

PowerScaler ultimately owns:

```text
VirtualCurrentKi
VirtualMaximumKi
VirtualKiRecovery
VirtualKiModifiers
```

Skill affordability and virtual Ki consumption must use virtual state once fully virtualized.

## 12.4 Stamina

PowerScaler ultimately owns:

```text
VirtualCurrentStamina
VirtualMaximumStamina
VirtualStaminaRecovery
VirtualStaminaModifiers
```

Native resource bars become compatibility/presentation mirrors.

---

# 13. Damage virtualization

PowerScaler's goal is not to replace every Xenoverse move mechanic.

It is to remove native **character-stat authority** from the statistical meaning of those mechanics.

Desired architecture:

```text
XV2 HIT / DAMAGE EVENT
         │
         ▼
Identify:
- attacker
- defender
- move
- hit
- attack class
- mechanical context
         │
         ▼
AUTHORITY FIREWALL
(native character magnitude excluded)
         │
         ▼
VIRTUAL DAMAGE KERNEL
         │
         ├── attacker virtual stats
         ├── defender virtual stats
         ├── virtual transformation modifiers
         ├── virtual buffs/debuffs
         └── native non-character move mechanics
         │
         ▼
Virtual Damage
         │
         ▼
Defender.VirtualCurrentHealth
         │
         ▼
Proxy / KO synchronization
```

The runtime should preserve useful native mechanical behavior where possible:

- skill identity;
- move-specific relative strength;
- combo-hit differences;
- animation;
- hit timing;
- hitboxes;
- knockback;
- move-specific mechanics.

It must not preserve native character-specific damage magnitude once that channel is fully virtualized.

---

# 14. Neutral mechanics seed

Flattened native fighters may still reveal move-relative mechanics.

For example, if all character stats are neutral:

```text
Light hit        -> neutral result A
Heavy hit        -> neutral result B
Kamehameha       -> neutral result C
Ultimate         -> neutral result D
```

those differences may represent move mechanics rather than fighter magnitude.

PowerScaler may use such results as a **Neutral Mechanics Seed** if research proves the separation is clean.

Conceptually:

```text
Neutral native move mechanics
          +
Virtual fighter statistics
          =
Virtual combat result
```

This is preferred to manually rebuilding every skill coefficient if the native mechanics can be safely retained.

However, any hidden native character multiplier must still be eliminated.

---

# 15. Formula lifting and interception strategies

Not every stat system needs the same implementation.

PowerScaler may use one of three broad strategies per mechanic.

## A. Operand replacement / formula lifting

At a proven calculation boundary, replace the native character-stat operand with virtual data or run the corresponding calculation in PowerScaler's numeric domain.

## B. Neutral-result reconstruction

Let Xenoverse compute a neutral mechanical seed using the universal native substrate, then derive the actual virtual result from that seed and the virtual fighter states.

## C. Full virtual calculation

Where native mechanics cannot be cleanly separated, PowerScaler owns the statistical calculation and returns only the safe state required for Xenoverse to continue normal battle flow.

All three strategies obey the same invariant:

```text
native character-specific statistical influence = 0
```

---

# 16. Transformations: mechanics native, stats virtual

Transformations contain both mechanical and statistical behavior.

## Xenoverse may retain

- transformation animation;
- model/hair changes;
- aura;
- form state;
- moveset changes;
- skill changes;
- other non-statistical transformation mechanics.

## PowerScaler owns

- attack changes;
- defense/durability changes;
- health changes;
- Ki changes;
- stamina changes;
- recovery changes;
- regeneration changes;
- revival changes;
- movement-related fighter-stat changes;
- all other discovered statistical changes.

Flattening only base PSC stats is insufficient.

Native transformation stat modifiers must also be neutralized, intercepted, or bypassed so they cannot stack secretly on top of virtual transformation rules.

---

# 17. Mixed systems such as Super Souls

A Super Soul may combine:

```text
trigger logic
+
statistical modifiers
+
special non-statistical behavior
```

PowerScaler should decompose these responsibilities.

Example:

```text
Trigger condition                -> Xenoverse mechanic
Attack +X%                       -> PowerScaler virtual stat
Ki recovery +Y%                  -> PowerScaler virtual stat
Special gameplay side effect     -> Xenoverse mechanic where appropriate
```

The same decomposition applies to:

- transformations;
- reinforcement skills;
- equipment;
- buffs/debuffs;
- boss modifiers;
- other mixed systems.

---

# 18. Scaling Doctrines

PowerScaler officially supports three first-class scaling doctrines:

```text
Canonical
Moderate
Vanilla
```

All doctrines run through the **same virtualization runtime**.

The selected doctrine changes how the authoritative virtual character profile is resolved.

It does not change the native flattening architecture.

---

# 19. Canonical Scaling Doctrine

Canonical attempts to preserve canonical character/stat relationships as faithfully as the project's source model supports.

If the source model defines:

```text
Kaioken x2
Super Saiyan x50
Super Saiyan 2 x100
Super Saiyan 3 x400
```

Canonical may apply those relationships directly.

Canonical prioritizes:

```text
1. Canonical hierarchy
2. Canonical transformation relationships
3. Internal mathematical consistency
4. Correct virtualization
5. Runtime stability
6. Presentation compatibility
```

Competitive balance or "fairness" is not an architectural requirement.

If a weak character is canonically incapable of meaningfully harming a vastly stronger fighter, the virtual system must be capable of expressing that.

If a vastly stronger fighter should defeat another with one attack, the system must be capable of expressing that as well.

---

# 20. Moderate Compression Scaling Doctrine

Moderate is not engine compression.

Moderate is a **curated Dragon Ball hierarchy doctrine**.

Its purpose is to preserve the intended hierarchy and encounter relationships while reducing the explosive escalation introduced by transformations and the villains/opponents tied to those transformations.

Moderate does not globally divide the roster by one constant.

Moderate changes **transformation and escalation edges** in the hierarchy graph.

---

# 21. Super Saiyan compression example

Canonical Namek example:

```text
Base Goku        = 3,000,000
Super Saiyan     = x50
SSJ Goku         = 150,000,000
Frieza 100%      = 120,000,000
```

Moderate example:

```text
Base Goku        = 3,000,000
Super Saiyan     = x5
SSJ Goku         = 15,000,000
Frieza 100%      = 12,000,000
```

The encounter relationship remains:

```text
150,000,000 / 120,000,000 = 1.25

15,000,000 / 12,000,000 = 1.25
```

The transformed tier moved.

The local SSJ Goku / Frieza relationship did not.

Frieza is not independently "divided by 10 because the engine needs smaller numbers."

Frieza moves because his hierarchy placement is relationally tied to the transformed tier.

---

# 22. Transformation-anchored hierarchy compression

Moderate compression operates around transformation/escalation anchors.

```text
BASE / PRE-TRANSFORMATION TIER
              │
              │ doctrine-specific transformation edge
              ▼
      TRANSFORMED / ESCALATED TIER
              │
              ├── transformed hero
              ├── villain who fights that hero
              ├── rivals
              └── comparable characters
```

Changing the transformation edge changes the tier that depends on it.

The hierarchy system should propagate the resulting values through relational character placement rather than relying on manually edited independent absolute values.

---

# 23. Kaioken example: Moderate is curated, not uniform

Moderate is not:

```text
CanonicalMultiplier / 10
```

for every transformation.

Example:

```text
Canonical Kaioken x2
```

may become:

```text
Moderate Kaioken +10%
= x1.10
```

This compresses the Saiyan Saga escalation surrounding Kaioken.

The associated Vegeta/Goku hierarchy should then be resolved under the Moderate relationship model rather than preserving the much larger canonical separation.

For example, a canonical gap of more than two times may become a Moderate gap of a little more than approximately 25%, according to the intended doctrine.

The key rule:

> **Moderate compresses escalation while preserving the intended narrative ordering and local matchup relationships as much as possible.**

It is not a universal numeric reduction formula.

---

# 24. Transformation families

Related transformations may share a doctrine-specific family policy where appropriate.

Example:

```text
Classic Super Saiyan Family

Canonical:
SSJ  = x50
SSJ2 = x100
SSJ3 = x400

Moderate:
SSJ  = x5
SSJ2 = x10
SSJ3 = x40
```

This preserves:

```text
SSJ2 / SSJ = 2
SSJ3 / SSJ2 = 4
```

while compressing the initial Base -> SSJ escalation.

Other transformation families may require different Moderate policies:

```text
Kaioken family
Super Saiyan family
Fusion
God forms
Blue forms
Blue Kaioken
Evolution
Ultra Instinct
Beast
Orange Piccolo
villain transformations
absorptions
potential unlocks
other discovered or authored forms
```

Do not hard-code one global compression coefficient.

---

# 25. Hierarchy graph

The character power database should be relational rather than a flat table of unrelated absolute numbers.

Suggested conceptual node:

```text
CharacterPowerNode
    Identity
    ReferenceNode
    RelationshipType
    CanonicalRelationship
    DoctrineOverrides
    Provenance
    Confidence
    ResolvedPower
```

Relationship types may include:

```text
Transformation
TrainingGrowth
OpponentPlacement
ArcScaling
RelativeMultiplier
GreaterThan
ApproximatelyEqual
Between
Fusion
Absorption
PotentialUnlock
TemporaryPowerUp
ExplicitAbsolute
Other derived relationships
```

Example:

```text
Goku_Namek_Base
    absolute = 3,000,000

Goku_Namek_SSJ
    parent = Goku_Namek_Base
    transformation = SuperSaiyan

Frieza_Namek_100
    reference = Goku_Namek_SSJ
    ratio = 0.8
```

Under Canonical, the SSJ edge may resolve as `x50`.

Under Moderate, it may resolve as `x5`.

Frieza's relational placement then resolves automatically.

---

# 26. Canonical metadata must never be overwritten by Moderate

Example:

```text
Transformation: Super Saiyan

CanonicalMultiplier:
    x50

ModerateMultiplier:
    x5

CurrentDoctrine:
    Moderate

AppliedMultiplier:
    x5
```

Moderate is a project design doctrine.

It must not rewrite the source-of-truth canonical relationship.

The database must always distinguish:

```text
What the source/canon says
```

from:

```text
What the selected PowerScaler doctrine applies
```

---

# 27. Vanilla Scaling Doctrine

Vanilla does **not** mean PowerScaler is disabled.

Vanilla means:

> Reproduce Xenoverse's original character-stat relationships through the virtual stat runtime.

Conceptually:

```text
Original XV2 statistical behavior
          │
          ▼
Vanilla profile import / characterization
          │
          ▼
Virtual Vanilla Character Profile
          │
          ▼
VirtualFighterState
          │
          ▼
Flat neutral native substrate
```

Native runtime authority remains zero.

The original native data is source material, not the live authority.

---

# 28. Vanilla as a control group

Vanilla is also a critical virtualization validation mode.

Compare:

```text
Original unvirtualized XV2:
Goku vs Vegeta
```

against:

```text
PowerScaler virtualization:
Doctrine = Vanilla
Goku vs Vegeta
```

The virtualized Vanilla build should reproduce the original statistical behavior closely enough to validate that PowerScaler has successfully captured the relevant character-stat mechanics.

A mismatch may reveal:

- an undiscovered stat channel;
- an undiscovered modifier;
- an authority leak;
- a formula stage that remains native;
- a resource/recovery rule;
- a transformation path;
- another missing dependency.

---

# 29. Scaling Doctrine resolution pipeline

```text
Character Identity
       │
       ▼
Character Definition
       │
       ├── Canonical relationships
       ├── Transformation relationships
       ├── Encounter / villain relationships
       ├── Character-specific stat rules
       └── Vanilla source behavior
       │
       ▼
Selected Scaling Doctrine
       │
       ▼
Hierarchy Solver
       │
       ▼
Resolved Virtual Character Profile
       │
       ▼
VirtualFighterState
```

Avoid three unrelated copies of every character where relationships can be shared.

Prefer:

```text
Character Definition
+
Scaling Doctrine
=
Resolved Character
```

---

# 30. Moderate compression is above virtualization; native flattening is below it

These two systems solve different problems.

```text
HIERARCHY / DESIGN LAYER
    Canonical / Moderate / Vanilla
    transformation edges
    villain relationships
    resolved virtual profile

             ↓

VIRTUALIZATION LAYER
    authoritative VirtualFighterState
    virtual calculations
    event interception

             ↓

NATIVE HOST LAYER
    flat safe stats
    proxy resources
    game mechanics/presentation
```

Moderate compression exists because the user wants a less explosive Dragon Ball progression curve.

Native flattening exists to remove Xenoverse's character-stat limits and authority.

Do not mix these concepts.

---

# 31. Mechanical Target Catalog

The Research page must continue evolving toward a **Mechanical Target Catalog**, not a hard-coded HP/Ki/Stamina selector.

A target is metadata describing something PowerScaler wants to discover, verify, characterize, neutralize, proxy, or virtualize.

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
    NativeAuthorityStatus
```

---

# 32. Target families

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

Do not encode an assumption that the final count is 3, 6, 51, or 120.

The final target catalog is discovered.

---

# 33. Target kinds

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
ProxyBinding
AuthorityBoundary
```

Not every virtualized mechanic is a persistent memory address.

---

# 34. Full character-stat research scope

The catalog must support the full fighter-stat surface.

## Resources / capacity

- Current Health
- Maximum Health
- Current Ki
- Maximum Ki
- Current Stamina
- Maximum Stamina
- resource initialization
- consumption
- recovery
- cost
- clamps
- normalized proxy behavior

Known live anchors remain useful evidence:

```text
Battle_Mob + 0x100  Current Health
Battle_Mob + 0x104  Maximum Health
Battle_Mob + 0x10C  Current Ki
Battle_Mob + 0x110  Maximum Ki
Battle_Mob + 0x16C  Current Stamina
Battle_Mob + 0x170  Maximum Stamina
```

They are research bindings, not the architecture.

## Offense

- Basic melee
- Basic Ki
- Strike
- Ki skill
- Ultimate
- Evasive damage
- Grab
- attack-rank application
- category coefficients
- final damage
- minimum/maximum damage paths
- zero-damage behavior
- DoT
- scripted/self/environmental damage
- exceptional damage routes

## Defense / durability

- durability-rank application
- incoming damage reduction/multipliers
- guard
- guard damage
- stamina damage
- stamina break
- break recovery
- immunity/invulnerability
- defensive transformation modifiers
- defensive Super Soul modifiers

## Recovery / revival

- healing
- regeneration
- Ki recovery
- stamina recovery
- context-specific recovery
- downed state
- revival state/progress
- revival rate
- restoration
- training reset
- scripted restoration

## Movement-related character stats

- speed-rank application
- ground/air/boost movement
- dash/step
- acceleration
- boost consumption
- movement buffs/debuffs
- transformation movement modifiers
- Super Soul movement modifiers

## Scaling / formula stages

- attack rank
- durability rank
- speed rank
- Ki rank
- stamina rank
- health scalar
- rank differences
- clamps
- raw/final multipliers
- final damage
- zero/min/max handling

## Transformations / modifiers

- transformation stage
- awoken state
- statistical modifiers
- temporary buffs/debuffs
- Super Soul state/modifiers
- PUP-linked modifiers
- character-specific form behavior
- shared form behavior

## Identity / context

- character
- costume/preset
- skill set
- team
- fighter generation
- battle instance
- mission/quest context
- boss/giant state
- body swap
- training reset
- forced transition
- other exceptional contexts

---

# 35. Research mission: find authority, not only values

The research question is not merely:

```text
Where is Stamina?
```

It is:

```text
Where does native Stamina acquire authority?
Who writes it?
Who reads it?
What consumes it?
What recovers it?
What clamps it?
What UI mirrors it?
What transformation/Super Soul paths modify it?
Where can PowerScaler safely intercept that authority?
```

For every target, research should map:

```text
SOURCE
  ↓
LOAD
  ↓
RUNTIME REPRESENTATION
  ↓
COPIES / DERIVATIONS
  ↓
MODIFIERS
  ↓
CONSUMERS
  ↓
CALCULATION
  ↓
BATTLE EFFECT
```

The desired result is an **authority map**, not only an address list.

---

# 36. Generic causal research instruments

The proven HP watchpoint workflow is one reusable instrument.

The research toolkit may include:

```text
Passive field observation
Hardware memory write watch
Hardware memory access watch when justified
Execution anchor
GPR context capture
XMM/SIMD value-flow capture
Caller/callee characterization
Code-window capture/decoding
Fighter-lifetime correlation
Action/context correlation
Negative-control correlation
Controlled intervention when explicitly authorized
Native-independence perturbation testing
```

Instrument choice depends on `TargetKind`.

Do not create a separate architecture for every stat.

---

# 37. Research arms

The UI should expose selectable **research arms** backed by the same experiment architecture.

Example:

```text
Resources
    Health.Current
    Health.Maximum
    Ki.Current
    Ki.Maximum
    Stamina.Current
    Stamina.Maximum

Damage
    BasicMelee
    BasicKi
    Strike
    KiBlast
    Ultimate
    ...

Defense
Recovery
Revival
Movement
Transformations
Modifiers
...
```

Each arm defines:

```text
Target semantic
Expected owner
Stimulus
Expected temporal behavior
Expected direction
Negative controls
Cross-fighter controls
Relevant instrumentation
Intervention criteria
Authority-leak tests
```

This is one generalized research architecture with many semantic targets.

---

# 38. Evidence lifecycle

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
AuthorityValidated
VirtualizationCandidate
ProductionReady
```

Evidence dimensions remain independent.

Examples:

```text
Stored field known             != writer known
Writer known                   != formula known
Formula known                  != source inputs known
Read verified                  != safe write verified
Safe write verified            != restoration verified
One character verified         != universal fighter mechanic
One battle mode verified       != all contexts verified
Virtual result correct         != native authority eliminated
```

Do not silently promote a target because a nearby target is understood.

---

# 39. Native-independence validation

For every fully virtualized statistic, PowerScaler must deliberately vary safe native values while holding the virtual value fixed.

Example:

```text
Virtual Basic Attack = X

Native Basic = safe A
Native Basic = safe B
Native Basic = safe C
```

Expected:

```text
actual virtual combat outcome remains unchanged
```

If the outcome changes, native influence remains.

This test family should become a first-class validation mechanism.

---

# 40. Profile-invariance validation

For each fighter and doctrine:

```text
Load vs weak opponent
Load vs peer
Load vs vastly stronger opponent
Load mirror match
```

The resolved base virtual profile must remain identical.

This permanently prevents accidental reintroduction of matchup normalization.

---

# 41. Doctrine validation

## Canonical

Verify:

- canonical transformation relationships;
- canonical hierarchy ordering;
- matchup ratios where source-supported;
- villain placement;
- fusion/absorption relationships;
- provenance/confidence.

## Moderate

Verify:

- configured transformation compression;
- related villain/opponent propagation;
- preserved local encounter relationships where intended;
- no accidental global compression;
- no hierarchy inversions;
- continuity across saga escalation transitions.

## Vanilla

Verify:

- reproduction of original Xenoverse fighter-stat behavior;
- transformation behavior;
- resource behavior;
- damage/defense relationships;
- recovery/revival relationships;
- absence of remaining native character authority.

---

# 42. Provenance

Hierarchy facts must retain provenance and confidence.

Suggested classes:

```text
Explicit Canon
Strongly Derived
Narrative / Matchup Derived
Estimated
Project Design Decision
```

Example:

```text
Super Saiyan CanonicalMultiplier:
    x50
    provenance = Explicit Canon

Super Saiyan ModerateMultiplier:
    x5
    provenance = Project Design Decision
```

Moderate must never masquerade as canon.

---

# 43. Static and runtime neutralization

Use two complementary systems.

## Static neutralization

Flatten installed character-stat data to known safe values before battle.

Purpose:

- consistent baseline;
- eliminate obvious native character differences;
- reduce runtime work;
- make leaks easier to detect.

## Runtime neutrality enforcement

Verify/reapply neutrality when:

- fighter spawns;
- preset loads;
- transformation occurs;
- Super Soul activates;
- temporary modifier applies;
- CAC modifiers resolve;
- boss/mission modifiers activate;
- any other native character-stat path changes.

Together:

```text
Static Neutral Baseline
+
Runtime Neutrality Enforcement
=
Universal Native Statistical Substrate
```

---

# 44. Neutralization Manifest

Native flattening must be data-driven.

Conceptually:

```text
NeutralizationManifest

Health.Maximum
    safeNeutralValue = ...

Ki.Maximum
    safeNeutralValue = ...

Stamina.Maximum
    safeNeutralValue = ...

Damage.BasicMelee
    safeNeutralValue = ...

Damage.BasicKi
    safeNeutralValue = ...

Damage.Strike
    safeNeutralValue = ...

Damage.KiBlast
    safeNeutralValue = ...

Defense.*
Recovery.*
Revival.*
Movement.*
TransformationModifiers.*
SuperSoulStatModifiers.*
...
```

As research discovers new native statistical channels, the manifest expands.

Avoid scattered hard-coded neutrality constants.

---

# 45. Semantic Event Bridge

The runtime should progress from raw memory observations toward semantic fighter-stat events.

Examples:

```text
CharacterSpawn
CharacterDespawn

TransformationEnter
TransformationExit

DamageAttempt
DamageResolved

HealingAttempt
HealingResolved

KiSpendAttempt
KiRecovery

StaminaSpendAttempt
StaminaRecovery

BuffApplied
BuffRemoved

RevivalProgress
RevivalCompleted
```

Desired authority flow:

```text
XV2 Mechanical Event
       │
       ▼
Semantic Event Bridge
       │
       ▼
Virtual Calculation
       │
       ▼
VirtualFighterState
       │
       ▼
Native Proxy Update
```

---

# 46. Stat Contracts

Every proven target should eventually produce a durable semantic contract.

Example:

```text
StatContract: Stamina.Current

Owner:
    FighterGeneration

SemanticType:
    Resource.Current

Known Native Representation:
    dynamically discovered

Lifecycle:
    fighter battle instance

Known Mutation Sources:
    vanish
    evasive
    boost
    break
    recovery
    other discovered paths

Known Consumers:
    HUD
    action eligibility
    recovery
    other discovered systems

Virtual Authority:
    PowerScaler

Native Proxy:
    yes

Native Influence:
    zero / incomplete

Required Capabilities:
    ...

Validation Suite:
    ...
```

Offsets and executable details may change without changing the semantic contract.

---

# 47. Research Runtime and production Virtualization Runtime

Maintain a strong boundary.

```text
PowerScaler Labs Research Runtime
│
├── observation
├── instrumentation
├── causal tracing
├── experimentation
├── candidate validation
├── intervention
└── authority-leak discovery


PowerScaler Virtualization Runtime
│
├── proven contracts only
├── deterministic virtual state
├── minimal production hooks
├── event interception
├── virtual calculations
├── neutrality enforcement
└── proxy synchronization
```

Research discovers.

Production executes proven knowledge.

---

# 48. Existing runtime boundaries that remain binding

## PowerScalerLabs.Runtime

Remains external and read-only.

Owns:

- passive observation;
- fighter registry/lifetimes;
- structural BattleCore discovery;
- chronology;
- supporting evidence.

It must not become the privileged write/injection lane.

## PowerScalerLabs.ProbeHost

Remains the isolated privileged managed lane.

Owns:

- explicit attachment;
- NativeProbe lifecycle;
- instrumentation control;
- privileged enrichment.

## PowerScalerLabs.NativeProbe.dll

Contains only native code needed for in-process causal instrumentation and eventual narrowly scoped production interception.

Do not turn NativeProbe into the policy or hierarchy engine.

## HealthScale

Remains a sealed companion boundary unless explicitly changed.

Its proven health behavior is reference evidence and/or a migration source, not permission to architect the full project around health.

---

# 49. Version independence

Version numbers and hashes are diagnostic metadata, not architectural permission.

Never:

```text
ExpectedGameVersion == X
→ feature allowed
```

Instead:

```text
Capability.Health.Intercept            PROVEN
Capability.Health.ProxyWrite           PROVEN
Capability.Damage.Finalization         CANDIDATE
Capability.Ki.CostIntercept            UNKNOWN
...
```

Feature availability depends on proven capabilities.

Structural discovery and runtime evidence remain preferred over hard version-address tables.

---

# 50. Capability Graph

Example:

```text
FighterIdentity                       PROVEN

Health.Current.Read                   PROVEN
Health.ProxyWrite                     PROVEN
Health.KOBridge                       CANDIDATE
Health.HealingIntercept               UNKNOWN

Ki.Current.Read                       PROVEN
Ki.CostIntercept                      UNKNOWN

Stamina.Current.Read                  PROVEN

Damage.Finalization                   CANDIDATE
Damage.NativeInfluenceZero            UNKNOWN

Revival.Progress                      UNKNOWN
```

A virtualization feature declares dependencies against the capability graph.

---

# 51. Recommended software domains

```text
PowerScaler
│
├── Identity
│   ├── CharacterResolver
│   ├── PresetResolver
│   ├── FighterGeneration
│   ├── TransformationResolver
│   └── CACResolver
│
├── Hierarchy
│   ├── CharacterPowerGraph
│   ├── RelationshipTypes
│   ├── Provenance
│   ├── TransformationPolicies
│   └── HierarchySolver
│
├── ScalingDoctrines
│   ├── Canonical
│   ├── Moderate
│   └── Vanilla
│
├── VirtualStats
│   ├── VirtualScalar
│   ├── VirtualStatSchema
│   ├── VirtualCharacterProfile
│   ├── VirtualFighterState
│   └── VirtualModifierStack
│
├── Neutralization
│   ├── NeutralizationManifest
│   ├── StaticProfileFlattener
│   ├── RuntimeNeutralityEnforcer
│   └── NativeAuthorityLeakDetector
│
├── RuntimeDiscovery
│   ├── ProcessAccess
│   ├── StructuralDiscovery
│   ├── InstructionObservation
│   ├── ConsumerTracing
│   ├── FighterIdentity
│   └── CapabilityGraph
│
├── EventBridge
│   ├── DamageEvents
│   ├── ResourceEvents
│   ├── HealingEvents
│   ├── TransformationEvents
│   ├── ModifierEvents
│   └── RevivalEvents
│
├── VirtualCalculation
│   ├── DamageKernel
│   ├── DefenseKernel
│   ├── ResourceKernel
│   ├── RecoveryKernel
│   ├── HealingKernel
│   ├── RevivalKernel
│   └── TransformationKernel
│
├── Proxy
│   ├── HealthMirror
│   ├── KiMirror
│   ├── StaminaMirror
│   ├── KOBridge
│   └── NativeStateSynchronizer
│
├── Research
│   ├── MechanicalTargetCatalog
│   ├── ResearchArms
│   ├── ExperimentEngine
│   ├── CandidateScoring
│   ├── CausalTracing
│   ├── InterventionTesting
│   ├── AuthorityLeakTesting
│   └── StatContracts
│
└── Validation
    ├── NativeIndependence
    ├── ProfileInvariance
    ├── CanonicalDoctrine
    ├── ModerateDoctrine
    ├── VanillaDoctrine
    ├── TransformationRegression
    ├── ProxyIntegrity
    └── FullRosterRegression
```

Names may evolve.

The responsibilities must not collapse back into a health/resource-only architecture.

---

# 52. Revised development gates

Do not create one architecture gate per stat.

Recommended high-level architecture sequence:

```text
Gate 0
Mission, terminology, authority doctrine, version independence

Gate 1
Reliable fighter identity and generalized runtime discovery

Gate 2
General Mechanical Target Catalog / research-arm architecture

Gate 3
Semantic contracts and capability graph

Gate 4
Native neutralization and Authority Firewall foundation

Gate 5
VirtualScalar, VirtualCharacterProfile, VirtualFighterState

Gate 6
Proxy-state architecture and one-way synchronization

Gate 7
Semantic event interception

Gate 8
First fully virtualized character-stat channel with Native Influence = 0

Gate 9
Damage / defense virtual interaction architecture

Gate 10
Transformation and modifier authority replacement

Gate 11
Hierarchy graph and Scaling Doctrine integration

Gate 12
Vanilla behavioral reproduction

Gate 13
Moderate hierarchy compression and villain/tier propagation

Gate 14
Canonical hierarchy integration

Gate 15
Full fighter BattleCore stat virtualization

Gate 16
Roster-wide authority-leak elimination and regression
```

Multiple research arms may progress in parallel where evidence permits.

---

# 53. Explicitly rejected architectural models

The following models are superseded and must not re-enter implementation.

## Rejected: Battle-local power projection

Do not rescale Jiren based on whether he fights Hercule or UI Goku.

## Rejected: Native residual contribution

Do not let Xenoverse express part of the fighter's stat relationship and PowerScaler merely supply the remainder.

## Rejected: Engine-safe projection of authoritative virtual magnitude

Do not compress Jiren's real virtual stat because float32 cannot contain it.

Only proxy/presentation state is bounded.

## Rejected: Global roster compression as a runtime workaround

Moderate compression is a hierarchy doctrine, not float32 accommodation.

## Rejected: One compression factor for all transformations

Moderate transformation scaling is curated per transformation or transformation family.

## Rejected: Treating native files as live statistical authority in Vanilla mode

Vanilla is reconstructed virtually.

## Rejected: Flattening base stats while leaving transformation/Super Soul/native modifiers authoritative

That is partial virtualization and must be reported as such.

## Rejected: Narrow Health/Ki/Stamina project framing

The full fighter BattleCore stat surface remains the target.

---

# 54. Hard architectural invariants

## Invariant 1

```text
VirtualFighterState is authoritative.
```

## Invariant 2

```text
Native character-specific statistical influence = 0
for every fully virtualized stat.
```

## Invariant 3

```text
Native character-stat values are neutral substrate or proxy state only.
```

## Invariant 4

```text
A fighter's base virtual profile does not depend on the opponent.
```

## Invariant 5

```text
Canonical, Moderate, and Vanilla are Scaling Doctrines
over one virtualization runtime.
```

## Invariant 6

```text
Moderate compresses transformation-driven hierarchy escalation
and the villains/opponents tied to those tiers.
```

## Invariant 7

```text
Canonical source relationships remain preserved as metadata
even when Moderate applies different values.
```

## Invariant 8

```text
Vanilla reproduces original XV2 behavior virtually;
it does not bypass virtualization.
```

## Invariant 9

```text
Version information is diagnostic, not permission.
```

## Invariant 10

```text
Research maps the full character-stat authority surface,
not only visible HUD resources.
```

---

# 55. Immediate Codex direction

Before any implementation change, Codex must read this document together with the current project state, project-understanding protocol, active research brief, and relevant source.

For every proposed change, Codex must answer:

1. Which `MechanicalTarget` or virtualization capability does this serve?
2. Is this character-statistical or purely game-mechanical?
3. What native authority exists today?
4. Where is the intended Authority Firewall boundary?
5. Does the change move authority into `VirtualFighterState`, or merely manipulate a native float?
6. What proxy, if any, must remain for Xenoverse?
7. How will Native Influence = 0 be tested?
8. Is fighter profile invariance preserved across opponents?
9. Which Scaling Doctrine layer, if any, is involved?
10. Does the change accidentally reintroduce native residual contribution, battle-local scaling, or global engine-driven compression?
11. Does the implementation remain generic enough for the full Mechanical Target Catalog?
12. What evidence upgrades the target's semantic and authority status?

If #5 is merely "write a larger/smaller native character float," the implementation is not yet stat virtualization.

---

# 56. Final project definition

**PowerScaler Labs is a replacement character-stat runtime and causal-research system for Dragon Ball Xenoverse 2.**

Xenoverse remains the host for:

- fighter identity;
- models;
- animations;
- skills;
- hitboxes;
- move timing;
- camera;
- battle flow;
- other non-statistical mechanics.

PowerScaler becomes the authority for:

- fighter statistical magnitude;
- resource state;
- offensive statistics;
- defensive statistics;
- recovery;
- revival;
- transformation stat changes;
- character-owned movement statistics;
- buffs/debuffs;
- scaling-doctrine resolution;
- all additional fighter BattleCore statistics proven through research.

The mature runtime should behave conceptually as:

```text
SELECT CHARACTER
        │
        ▼
XV2 loads identity, assets, skills, and mechanics
        │
        ▼
Native fighter statistics are flattened to a safe neutral substrate
        │
        ▼
Selected Scaling Doctrine resolves the virtual character profile
        │
        ▼
VirtualFighterState becomes authoritative
        │
        ▼
Statistical battle interactions execute through virtual logic
        │
        ▼
Safe native proxy state keeps Xenoverse functioning and presenting state
```

The final success criterion is:

> **Changing or flattening Xenoverse's native character-stat values no longer changes the intended PowerScaler hierarchy or fighter-stat outcomes, because those native values no longer possess character-specific statistical authority.**

At that point, Xenoverse's native character-stat limits no longer define what a Xenoverse character can statistically be.
