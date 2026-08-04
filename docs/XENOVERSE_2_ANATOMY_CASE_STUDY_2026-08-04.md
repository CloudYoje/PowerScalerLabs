# Dragon Ball Xenoverse 2: Technical Anatomy and Content Case Study

Research snapshot: 2026-08-04  
Local PC build observed: `DBXV2.exe` version `1.25.02.0`  
Local game root: `C:\Games\SteamLIBRARY\steamapps\common\DB Xenoverse 2`

## Purpose and limits

This document is a working model of how Dragon Ball Xenoverse 2 is organized as a game, a data set, and a running process. It is meant to support PowerScaler research, diagnostics, and future tooling.

It is not a claim that every internal field is known. Xenoverse 2 is proprietary software. Much of the technical vocabulary comes from long-running community reverse engineering, especially Eternity's open-source parsers. Fields that those parsers still call `unk` remain unknown here. Likewise, the local loose `data` directory is heavily modded: its counts and generated indexes describe this installation, not a clean or complete vanilla build.

Confidence labels used below:

- **Observed**: directly present in the local installation or extracted XML.
- **Source-backed**: represented by an open-source parser or loader implementation.
- **Community-described**: useful gameplay knowledge, but not an official binary specification.
- **Inference**: a reasoned connection that still needs runtime or clean-install proof.

## Executive model

Xenoverse 2 is best understood as five cooperating systems:

1. **Content mounting**: large CPK archives provide shipped content; patcher-supported loose files can override or extend it.
2. **Identity tables**: character, skill, item, stage, quest, and text tables assign numeric IDs and three-letter resource codes.
3. **Asset graphs**: models, skeletons, animations, materials, textures, effects, and audio are linked by table records and naming conventions.
4. **Battle definitions**: command inputs activate action timelines; timelines create hitboxes/projectiles/effects; damage definitions describe impact behavior.
5. **Runtime state**: the executable instantiates fighters, resolves presets and quest actors, and maintains transient health, Ki, stamina, transformations, AI, and battle state.

An ID is therefore not the object itself. A character ID can select a CMS record; a costume/preset ID can then select a CUS loadout and PSC parameter record; those records point into character and skill assets; a quest can instantiate that combination with its own AI, team, level, and overrides.

## Installed PC anatomy

The observed root contains:

```text
DB Xenoverse 2/
  START.exe
  bin/DBXV2.exe
  bin/xinput1_3.dll
  cpk/*.cpk
  data/*
  EasyAntiCheat/
  XV2PATCHER/
  winmm.dll
```

`START.exe` is the normal launcher-facing entry point. `bin/DBXV2.exe` is the game process PowerScaler detects. The local `bin` also contains middleware and platform libraries such as CRI/ADX-related audio dependencies, Iggy UI, Oodle, Steam/EOS libraries, rendering support, and XInput.

The local CPK set includes the large base archives `data.cpk`, `data0.cpk`, `data1.cpk`, smaller support archives, movie archives, and multiple DLC archives. CPK is a packaging boundary, not a gameplay category: a single archive can contain many logical asset types.

The loose-data tree currently exposes these top-level domains:

```text
adam_shader  battle  chara  demo  event  lighting  lobby  msg
pe           quest   skill  sound stage   system   ui     vfx
```

The open-source XV2 Patcher describes itself as a DLL that improves modding functionality and includes explicit CPK, character, item, quest, stage, and UI patch modules. Its main build can act as an XInput proxy, which explains why proxy ownership and forwarding are critical to PowerScaler compatibility. See [xv2patcher](https://github.com/eterniti/xv2patcher).

## Content resolution

The safe conceptual order is:

```text
shipped CPK data -> updates/DLC -> patcher virtual filesystem -> loose/modded data
```

The exact precedence is patcher- and version-dependent. Never assume that a loose file is vanilla merely because the game loads it. For reproducible research, record:

- executable version and hash;
- CPK inventory;
- patcher version/configuration;
- loose-file path and hash;
- X2M installation state;
- whether the same result occurs on a clean installation.

## Character identity, roster entries, and presets

### CMS: character resource identity

`data/system/char_model_spec.cms` is the central character model/resource table. The observed extracted XML contains 258 entries. Each record has:

- numeric character ID;
- short resource code, commonly three characters, such as `GOK`;
- character/model resource name;
- animation (`EAN`) resource;
- facial and camera animation resources;
- action (`BAC`) and command (`BCM`) resources;
- AI resource;
- additional model, effect, voice, and unknown fields.

The three-letter code is a resource namespace, not necessarily a unique human-readable character name. Variants may use separate codes; CaC races use codes such as `HUM`, `HUF`, `SYM`, `SYF`, `NMC`, `FRI`, `MAM`, and `MAF`.

### Costume ID is also a battle preset selector

In cast-character tables, `COSTUME_ID` frequently means more than clothing. It selects a roster preset/version. For Goku, for example, different costume IDs can choose different clothing, skill loadouts, model presets, stats, transformations, or DLC/festival variants.

This distinction is essential:

- **Character ID** answers which character family/resource record.
- **Costume/preset ID** answers which selectable or scripted variant.
- **Model preset** answers which model/part presentation within that variant.
- **Transformation state** is a runtime state and may select further part sets or parameters.

Do not collapse those into a single "character" key in PowerScaler evidence.

### CUS: loadouts and skill definitions

`custom_skill.cus` contains both cast-character skill sets and skill catalog records. The local XML contains 619 skill sets and 1,130 skill records across super, ultimate, evasive, blast, awoken, and unknown categories.

A skill-set record binds:

```text
character ID + costume ID -> nine skill slots + model preset
```

The nine observed slots represent four supers, two ultimates, evasive, blast/basic projectile behavior, and awoken, with `65535` commonly serving as an absent/unset sentinel.

CUS skill records include a numeric ID, secondary ID, short code, race lock, type flags, paths, power-up/aura references, transformation part sets, models, and possible replacement skill sets. The parser source explicitly warns that CUS skill types are not the same numbering as IDB item types. See [CusFile.h](https://github.com/eterniti/eternity_common/blob/main/DBXV2/CusFile.h).

### PSC: per-preset battle parameters

`parameter_spec_char.psc` maps character IDs to one or more costume/preset parameter records. The observed file has two configurations, 406 character entries, and 1,314 preset specifications. Fields include:

- health, Ki, and stamina parameters;
- Ki and stamina recharge/drain behavior;
- basic attack, basic Ki, strike, and Ki-blast offense;
- corresponding defenses;
- ground, air, boost, and dash movement;
- body size and camera values;
- talisman/Super Soul and additional flags.

These values are configuration inputs, not necessarily the final bars or damage seen in battle. Level scaling, quest overrides, transformations, Super Souls, buffs, difficulty, and runtime formulas may modify the instantiated result.

### CST, CSO, aura, and partner customization

- `chara_select_table.cst`: character-select roster/slot organization.
- `chara_sound.cso`: character voice/sound-resource association.
- `aura_setting.aur`: aura definitions and character links.
- `CameraLimitValue.cml`: camera/body-shape related values.
- `.oco/.ocs/.pso/.odf/.ocp/.oct`: partner customization and related option tables in the observed system tree.

Partner customization exposes costume, skill set, stats, and Super Soul choices in game, a useful gameplay confirmation that those dimensions are independent. See the community-maintained [Partner Customization overview](https://dbxv2.fandom.com/wiki/Partner_Customization).

## Character asset anatomy

The local `data/chara` tree contains 252 code directories because it includes loose modded content. A typical character graph uses:

| Extension | Meaning | Research relevance |
|---|---|---|
| `.emd` | Mesh/model geometry | Visible body, costume, hair, accessory geometry |
| `.esk` | Skeleton/bones | Rig hierarchy and transforms |
| `.ean` | Skeletal animation | Movement, attacks, camera, face animation depending on suffix/context |
| `.emo` | Model container/association | Groups model-oriented data |
| `.emm` | Material definitions | Shader/material parameters |
| `.emb` | Embedded file/texture container | Often contains DDS textures or related resources |
| `.ema` | Material/UV animation | Animated material properties |
| `.bcs` | Body/costume specification | Part sets, colors, bodies, skeleton links |
| `.bac` | Action timeline | Animation, hitbox, movement, effect, invulnerability, and control events |
| `.bcm` | Command/input map | Conditions and input chains that activate BAC entries |
| `.bdm` | Damage behavior | Damage, stun, knockback, effects, camera shake, ailments |
| `.bsa` | Projectile/skill action behavior | Shot or skill behavior linked with other skill assets |
| `.eepk` | Effect package | Visual-effect resources and effect IDs |

Eternity's DBXV2 source tree contains maintained parsers for these and many neighboring formats, making it the strongest public source for structure-level claims: [eternity_common/DBXV2](https://github.com/eterniti/eternity_common/tree/main/DBXV2). LibXenoverse independently provides model, skeleton, animation, texture-container, and conversion code: [LibXenoverse](https://github.com/DarioSamo/LibXenoverse).

### How a basic attack becomes damage

A practical, simplified chain is:

```text
controller/input state
  -> BCM command conditions
  -> BAC action entry and timeline
  -> hitbox or projectile activation
  -> BDM damage/stun/knockback definition
  -> executable combat calculation
  -> runtime Battle_Mob health mutation
  -> HUD/audio/effects/reactions
```

This is a model, not proof that every move uses every stage. PowerScaler's live evidence has already identified a common final HP subtraction writer. File analysis can explain which content definition led toward that writer, while runtime tracing establishes what actually executed.

## Skills

The loose skill tree uses these observed category directories:

- `SPA`: super attacks;
- `ULT`: ultimate attacks;
- `ESC`: evasive skills;
- `MET`: awoken/transformation or "metamorphosis" skills;
- `BLT`: blast/basic Ki projectile definitions;
- `CMN`: shared/common skill resources.

Observed skill folder names follow patterns such as `2560_GVT_BLB`: a numeric identity plus character/owner-style and skill-code components. Treat the name as a convention, not a complete schema; CUS remains the authoritative local linkage.

A skill can combine its own BAC, BCM, BDM, BSA, animations, effects, materials, textures, and audio. The separation explains why "skill damage" is not safely inferred from one file:

- BAC may choose when and how many hitboxes occur.
- BDM may define base impact behavior for an entry.
- CUS may associate PUP/aura/transformation state.
- PSC and runtime state supply attacker/defender parameters.
- buffs, Super Souls, quest scaling, and engine formulas change the result.

Skill gameplay categories include supers, ultimates, evasives, awoken skills, and character/basic blast behavior. Supers and ultimates can be strike-, Ki-blast-, power-up-, counter-, or character-specific in practical use; those labels are gameplay taxonomy, not necessarily one binary enum. A community category map is available at the [XV2 Skills index](https://dbxv2.fandom.com/wiki/Category%3ASkills).

## Equipment, costumes, items, QQ Bangs, and Super Souls

The observed `data/system/item` directory contains separate IDB tables for:

- costume tops, bottoms, gloves, and shoes;
- accessories;
- skill items;
- talismans/Super Souls;
- materials, extra items, and gallery entries.

An IDB record can contain ID/type, rarity, text-message IDs, DLC/availability flags, race lock, prices, model ID, and an effect block. Effect fields can include health, Ki, stamina, recovery, speed, attack, damage, and defense modifiers.

**Costume ID warning:** an equipment ID in `costume_top_item.idb` is not interchangeable with a cast character's `COSTUME_ID` in CUS/PSC. They live in different tables and solve different problems.

Clothing has four equippable body regions plus accessories. QQ Bangs override clothing stat effects, allowing appearance and stats to be decoupled. Super Souls add conditional battle effects rather than merely selecting geometry. See the community [Equipment overview](https://dbxv2.fandom.com/wiki/Equipment).

The local item index records 4,150 entries across ten tables. Empty names indicate missing comments, placeholder entries, or names that must be resolved through MSG text IDs. IDs may include DLC and installed-mod additions.

## Text and localization

`.msg` files are language-specific string databases. The observed tree includes suffixes such as `_en`, `_ja`, `_fr`, `_de`, `_es`, `_it`, `_pt`, `_ru`, `_pl`, `_kr`, `_zh`, and `_tw`.

Tables often store `NAME_ID`, `DESC_ID`, or similar numeric references rather than inline prose. Therefore a complete canonical name requires:

```text
owning table + record type + numeric text ID + correct MSG family + language
```

The same numeric value can mean different strings in different MSG families. Never build a global name dictionary keyed only by number.

## Quest anatomy

The observed loose quest families are `TPQ`, `TMQ`, `BAQ`, `TCQ`, `HLQ`, `OSQ`, and `XTALK`. Eternity's QXD enum documents a wider engine taxonomy:

| Code | Meaning in parser source |
|---|---|
| `TPQ` | Main/story quests, including Legend Patrol |
| `TMQ` | Parallel Quests |
| `BAQ` | Time Rift quests |
| `TCQ` | Teacher/instructor quests and tests |
| `HLQ` | Expert Missions |
| `RBQ` | Raid quests |
| `CHQ` | training quests |
| `LEQ` | Frieza Siege quests |
| `TTQ/TFB/TNB` | Hero Colosseum story/free/NPC battles |
| `OSQ` | Fu/Extra Scenario quests |
| `PRB/PRD` | player raid boss / Crystal Raid |
| `RBD/RBS` | extra/million raid variants |
| `GBB` | Cross Versus |
| `EVT` | Festival of Universes |
| `CBF` | Cheelai/Broly friendship content |

This taxonomy is source-backed by [QxdFile.h](https://github.com/eterniti/eternity_common/blob/main/DBXV2/QxdFile.h), but availability depends on game version and installed content.

### Quest file family

| Extension | Primary role |
|---|---|
| `.qxd` | Quest catalog metadata, type, episode, flags, rewards, collections, and quest character definitions |
| `.qml` | Actors instantiated in battle: stage, team, AI, QXD character reference, and skills |
| `.qsl` | Stage layouts, spawn positions, interactive characters/items, poses, and QML links |
| `.qed` | Event script: conditions and actions grouped by state/event indices |
| `.qbt` | Normal, interactive, and special dialogue/event records with speaker portraits and costume/transformation fields |
| `.ttb/.ttc` | Cross-character talk/dialogue configuration used by the observed `XTALK` data |
| `.msg` | Localized quest names, objectives, dialogue, and UI text |
| voice `.acb/.awb` | Cue metadata and audio wave bank content |

A quest is thus a small program and content bundle, not a single level file. QED controls progression, QML defines participants, QSL places them, QBT sequences conversations, QXD supplies catalog/reward metadata, and MSG/audio supply presentation.

Parallel Quests are replayable side scenarios with normal and hidden/ultimate completion conditions, rewards, time limits, and optional variations. The community-maintained list describes 100 base-game PQs plus DLC additions, but counts can change with new content and should be version-stamped. See [Parallel Quests](https://dbxv2.fandom.com/wiki/Parallel_Quests).

## Stages, UI, effects, and audio

- `data/stage`: stage geometry, collision, lighting, effects, and stage definitions.
- `xv2_stage_def.xml`: patcher-oriented extensible stage catalog in the observed install.
- `data/ui`: Iggy/UI assets, portraits, icons, HUD resources, and menus.
- `data/vfx` and `.eepk`: effect packages and their resources.
- `data/sound`: CRI-style `.acb` cue sheets and `.awb` wave banks; individual encoded streams may be HCA.
- `data/lighting` and `adam_shader`: lighting and rendering/shader resources.
- `data/demo` and `data/event`: cutscene and scripted presentation data.

`.emb` is a container, so "edit the EMB" is incomplete: the embedded DDS/resource identity and the consumer of that container also matter. Similarly, `.acb` generally supplies cue metadata while `.awb` contains associated wave data; replacing one without preserving their relationship can fail.

## Core gameplay systems

### Created characters (CaCs)

CaCs combine race/gender, body and face parts, colors, equipment, QQ Bang, Super Soul, attributes, skills, level, and progression. Race/gender affects base parameters and available appearance assets. Their data path differs from a fixed cast preset even when both become battle fighters at runtime.

### Combat resources

- **Health**: depleted by damage and restored by healing/recovery. Training settings can auto-restore it, which can mask a successful hit if only end-state delta is observed.
- **Ki**: spent on many supers, ultimates, and awoken states; gained through combat, charge skills, and effects.
- **Stamina**: spent on evasives, vanishes, movement, and defensive systems; broken stamina changes recovery and vulnerability.

PowerScaler must distinguish resource capacity, current value, recharge/drain rates, HUD-normalized values, and transient transformed copies. A table field named `KI` does not prove it is the live current-Ki address.

### Transformations and power-ups

Awoken skills can change model part sets, aura, skill set, parameters, animations, and runtime state. `powerup_parameter.pup` contains additive/multiplicative combat and movement fields used by power-up states; the local file has 99 entries. A transformed fighter should be treated as a new evidence generation when pointer identity or resource bindings change.

### Super Souls and conditional effects

Super Souls can trigger on battle conditions and alter stats, recovery, damage, movement, skills, or other behavior. Their IDB effect structure and executable logic mean static loadout inspection alone cannot prove an active multiplier at a specific instant.

### Quests and scaling

Quest definitions can select character variants, teams, AI, levels, skills, and scripted state changes. The local files `level_character_parameter.lcp`, `quest_level_parameter.lcp`, PSC, and QXD-related data demonstrate multiple scaling layers. A fighter's visible roster preset is not enough to predict quest-instantiated health or damage.

## Current official content horizon

Xenoverse 2 remains an actively extended title. Bandai Namco's current DLC page groups Super, Extra, Ultra, Legendary, Conton City Vote, Hero of Justice, DAIMA, and Future Saga content, while the official news page reports Future Saga Chapter 4 as the final chapter as of July 2026. Version-stamp all roster, quest, skill, and costume totals rather than treating any web list as timeless. See [official downloadable content](https://www.bandainamcoent.com/games/dragon-ball-xenoverse-2/downloadable-content) and the [official Xenoverse 2 media/news page](https://www.bandainamcoent.com/games/dragon-ball-xenoverse-2/media/6809).

## What the local indexes contain

The companion files are searchable evidence snapshots:

- `xv2-data/local-character-index.csv`: 258 CMS records and resource links.
- `xv2-data/local-character-presets.csv`: 619 CUS character/costume loadouts.
- `xv2-data/local-skill-index.csv`: 1,130 CUS skill records.
- `xv2-data/local-item-index.csv`: 4,150 IDB entries across ten item tables.

These were generated from local loose binary/XML data using Eternity `genser` 4.2 where required. Comments in generated XML supply many English labels. Those comments are tooling annotations and can be blank, stale, or mod-authored; numeric fields and source hashes should be retained for rigorous use.

## ID hygiene rules for PowerScaler

1. Always store an ID with its table/domain: `CMS character 0`, `CUS super skill 0`, and `IDB costume-top 0` are unrelated keys.
2. Store both decimal and hexadecimal renderings, but use one numeric value internally.
3. Pair character ID with costume/preset ID and model preset.
4. Pair runtime fighter evidence with slot, actor address, battle generation, and transformation generation.
5. Pair names with MSG family and language.
6. Record executable/CPK/loose-data versions and hashes.
7. Treat `0xFFFF`/`65535` and `0xFFFFFFFF` as likely sentinels only in the context of the owning field.
8. Never promote parser comments or folder naming guesses to runtime truth without corroboration.
9. Keep installed-mod IDs separate from clean-game IDs.
10. Preserve unknown fields during round trips; unknown does not mean disposable.

## Implications for PowerScaler architecture

### Build a versioned knowledge graph

The useful internal key is not simply a display name. A future read-only catalog should model:

```text
GameBuild
  -> Character(CMS id, code)
  -> Preset(COSTUME_ID, MODEL_PRESET)
  -> ParameterSet(PSC)
  -> SkillSet(CUS)
  -> Skill(CUS id/type/code)
  -> Action(BAC)
  -> DamageDefinition(BDM)
  -> Effect/Animation/Audio assets
```

Quest nodes should link QXD quest identity to QML actor instances, QSL stages/positions, QED events, QBT dialogue, and localized MSG keys.

### Separate static intent from causal evidence

Static files answer what content is configured. Runtime tracing answers what executed. The strongest finding combines both:

```text
quest actor + preset + skill/action definition
  AND
observed thread/RIP/register values + fighter generation + HP/resource delta
```

Neither side should silently substitute for the other.

### Recommended next research passes

- Capture a clean 1.25.02 loose extraction and diff it against the current modded tree.
- Add SHA-256 and source-path columns to generated catalogs.
- Resolve English MSG names into domain-qualified tables.
- Compile QXD/QML/QSL/QED/QBT into a quest graph with unresolved references reported.
- Map CUS skill IDs to folder assets and BAC/BDM entry counts.
- Map PSC preset parameters to live spawned fighter observations.
- Track transformations as explicit before/after asset and runtime generations.
- Add a read-only catalog browser to PowerScaler only after the extraction pipeline is deterministic.

## Sources and provenance

Primary technical sources:

- [Eternity common DBXV2 parsers](https://github.com/eterniti/eternity_common/tree/main/DBXV2)
- [XV2 Patcher source](https://github.com/eterniti/xv2patcher)
- [XV2 Mods Installer source](https://github.com/eterniti/xv2ins)
- [LibXenoverse source](https://github.com/DarioSamo/LibXenoverse)

Official product/content sources:

- [Bandai Namco Xenoverse 2 DLC catalog](https://www.bandainamcoent.com/games/dragon-ball-xenoverse-2/downloadable-content)
- [Bandai Namco Xenoverse 2 media/news](https://www.bandainamcoent.com/games/dragon-ball-xenoverse-2/media/6809)
- [Steam product page](https://store.steampowered.com/app/454650/DRAGON_BALL_XENOVERSE_2/)

Community gameplay cross-checks:

- [Characters and presets](https://dbxv2.fandom.com/wiki/Characters)
- [Parallel Quests](https://dbxv2.fandom.com/wiki/Parallel_Quests)
- [Equipment](https://dbxv2.fandom.com/wiki/Equipment)
- [Skills](https://dbxv2.fandom.com/wiki/Category%3ASkills)
- [Partner Customization](https://dbxv2.fandom.com/wiki/Partner_Customization)

Local evidence:

- installed `DBXV2.exe` and CPK inventory;
- loose `data` directory inventory;
- extracted CMS, CUS, PSC, PUP, and IDB XML;
- Eternity Tools `genser` 4.2 conversion output.

## Bottom line

Xenoverse 2 is deeply table-driven, but the tables are only the recipe. Characters are CMS resource identities plus preset-specific CUS/PSC data; costumes can mean equipment items or cast presets depending on domain; skills are graphs of action, command, damage, animation, effect, audio, and power-up data; quests are event programs assembled from several coordinated files. The executable turns all of that into short-lived fighter objects whose resource values can be altered by level, quest, transformation, Super Soul, buff, and training rules.

That model gives PowerScaler a disciplined path forward: catalog static identities, bind runtime evidence to full fighter generations, and require causal observations before claiming that any file field or memory address controls gameplay.
