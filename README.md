# PowerScaler Labs — Runtime Access Architecture Gate 0 + HealthScale Companion 1

> **Build Hotfix 1:** Completes the protocol-7 `TelemetryEventMessage` constructor migration for scanner-control events and explicitly imports `System.IO` in the HealthScale companion manager. This corrects the first Windows compiler failures reported after Publish Verifier Hotfix 2; runtime access and companion behavior are unchanged.

> **Publish Verifier Hotfix 2:** The HealthScale companion audit now verifies the publisher's composed `Companions\HealthScale\Payload` path through its actual variables and staging commands instead of requiring that derived path to appear as one contiguous literal. This is a build-gate correction only; runtime and companion behavior are unchanged.

> **Publish Verifier Hotfix 1:** The source verifier checks the centralized `ValidatedRuntimeLayout.SupportedGameVersion` invariant instead of the retired `ExpectedGameVersion` token.

This checkpoint deliberately pauses feature expansion before the first renewed in-game test. It rebuilds the way PowerScaler Labs reaches into Dragon Ball Xenoverse 2 so later telemetry, code anchoring, causal validation, and scaling work do not grow around a single hidden pointer route.

The starting point is **Chronological Telemetry Gate 1 — Nullability Hotfix 1**. Its broad scanner, isolated 25 ms chronology lane, candidate store, guided experiment controller, recording barriers, and sealed HealthScale companion are retained. The runtime-access foundation underneath them is renewed.

## Gate boundary

Included:

- external query/read-only process access;
- provider-based BattleCore resolution;
- fail-closed provider conflict handling;
- serialized address provenance registry;
- stable fighter identity across pointer reuse;
- factual raw-memory observation stream separated from semantic events;
- centralized compressed-scale comparison policy;
- cumulative `ReadProcessMemory` and `VirtualQueryEx` budgets for observer and chronology lanes;
- strict startup stabilization followed by periodic full provider revalidation, avoiding a full BattleCore structural scan every 100 ms;
- protocol schema 7 and session schema 7;
- offline architecture self-test;
- HealthScale 1.1.1 retained as a sealed companion app.

Not included:

- game-memory writes;
- DLL injection;
- graphics hooks;
- passive code-access hooks;
- direct DBXV2 signature scanning;
- controlled causal probes;
- production combat scaling;
- a claim of successful live DBXV2 operation.

## Safety boundary

The PowerScaler Labs runtime remains external and read-only. Its native import surface is restricted to:

```text
OpenProcess
ReadProcessMemory
VirtualQueryEx
CreateToolhelp32Snapshot
Module32FirstW
Module32NextW
```

There is no `WriteProcessMemory`, remote allocation, remote thread, input hook, detour library, or graphics hook in this gate.

HealthScale remains a separate native product. PowerScaler Labs can explicitly install, adopt, verify, and uninstall its packaged payload through the Companion Apps page, but the PowerScaler runtime does not link against it or share live state with it.

## Renewed access flow

```text
PowerScalerLabs.Runtime.exe
        ↓
Find DBXV2.exe
        ↓
Open process with query + VM-read rights only
        ↓
Enumerate modules and validate DBXV2 version
        ↓
Run registered IBattleCoreLocator providers
        ↓
Fail closed on provider disagreement
        ↓
Stabilize the selected BattleCore candidate
        ↓
Acquire fighter slots and assign generation identities
        ↓
Emit factual raw reads
        ↓
Interpret selected fields in observer/scanner/chronology layers
        ↓
Persist provenance, identity, chronology, and read-budget evidence
```

## BattleCore providers

BattleCore acquisition is no longer implemented directly inside `ExternalCapabilityObserver`.

The runtime now uses:

```csharp
interface IBattleCoreLocator
{
    string ProviderId { get; }
    string DisplayName { get; }
    BattleCoreLocatorResult Locate(GameMemoryReader reader);
}
```

Registered providers in this gate:

1. `xv2-patcher-1.25.2-layout`
   - retains the historical validated XV2 Patcher route;
   - requires the supported patcher image layout;
   - scores structural BattleCore candidates;
   - reports its evidence and failure state.

2. `dbxv2-direct-signature-research`
   - intentionally performs no scan;
   - exists as an explicit future provider boundary;
   - cannot select an address until a direct DBXV2 signature is separately researched and approved.

If two future providers resolve different BattleCore addresses, the coordinator reports a conflict and selects neither address.

During initial acquisition, the provider performs full structural validation on every heartbeat until the candidate is stable. After activation, full provider resolution is refreshed periodically (approximately once per second at the 100 ms observer heartbeat); fighter-slot reads remain fail-closed every heartbeat. This keeps provider confidence without repeatedly rescanning the entire structure ten times per second.

## Address provenance

All approved offsets and RVAs are registered in `AddressProvenanceCatalog.cs`. Each entry records:

- stable key;
- owner/layer;
- module or object region;
- RVA or object-relative offset;
- data type and byte count;
- bounded meaning;
- source of knowledge;
- supported game version;
- provider;
- validation stage;
- read cadence;
- failure behavior.

The initial registry includes:

```text
xinput1_3.dll + 0x2080C8  BattleCore pointer-chain root
BattleCore   + 0x3A58     14-slot Battle_Mob pointer array
Battle_Mob  + 0x000      vtable/type evidence
Battle_Mob  + 0x100      current health
Battle_Mob  + 0x104      maximum health
Battle_Mob  + 0x10C      current Ki candidate
Battle_Mob  + 0x110      maximum Ki candidate
Battle_Mob  + 0x16C      current stamina candidate
Battle_Mob  + 0x170      maximum stamina candidate
```

The registry does not promote Ki or stamina beyond their existing `Correlated` stage.

## Fighter identity

A fighter is no longer identified only by its heap address. Every acquisition gets a `FighterIdentityMessage` containing:

```text
process instance ID
battle instance ID
fighter slot
slot acquisition generation
actor address
vtable address
first-seen UTC timestamp
first-seen monotonic tick
```

The identity key follows the lifetime rather than the pointer. If DBXV2 releases an object and later reuses the same address, the new acquisition receives a new slot generation and chronology epoch.

## Raw facts versus semantic interpretation

The observer now produces selected `RawMemoryObservationMessage` records independently from semantic `TelemetryEventMessage` records. Slot-pointer attempts include their success state; validated health reads retain exact raw bits. A changed pointer is not assigned to the previous fighter generation while the new object is still unvalidated. All lower-level failures remain counted in the access metrics.

A raw observation records:

- provenance key;
- region and offset;
- fighter identity when available;
- exact address;
- data type and byte count;
- raw bits;
- numeric decoding;
- read success;
- monotonic sequence and timestamps.

The semantic layer can still say “Current health changed,” but the recording retains the factual read that supported that interpretation.

## Compressed-scale comparison policy

The old observer used a minimum tolerance of one full point. That could erase all changes at the intended baseline of approximately `10 HP` and `0.01 damage`.

Protocol schema 7 centralizes semantic/scanner comparisons under:

```text
policy: compressed-scale-v1
absolute tolerance: 0.000001
relative tolerance: 0.000001
```

A change from `10.00` to `9.99` is observable. Sub-tolerance floating-point noise remains equivalent.

The chronology lane continues to use exact raw-bit equality because its job is to preserve ordering and raw transitions, not decide semantic significance.

## Read budgets

`GameMemoryReader` now counts cumulative:

- high-level read requests;
- `ReadProcessMemory` calls;
- bytes requested;
- bytes completed;
- failed OS reads and requests rejected before an OS read;
- `VirtualQueryEx` calls;
- failed queries;
- module refreshes.

Observer and chronology readers report separate metrics through `RuntimeAccessStatusMessage`. The Runtime Connection page displays both lanes so the live test can measure access cost instead of merely observing whether the game launches.

## Recording schema 7

A session now contains:

```text
session.json
frames.jsonl
events.jsonl
raw-memory-observations.jsonl
scanner-observations.jsonl
chronology-samples.jsonl
chronology-watchlist.json
runtime-access-architecture.json
timeline.jsonl
candidate-keys.jsonl
candidate-index.json
```

`runtime-access-architecture.json` snapshots the active provider reports, address provenance, comparison policy, and read budgets. `raw-memory-observations.jsonl` is the factual read stream. Existing chronology and scanner evidence remain separate.

## Offline architecture self-test

Run on Windows before opening DBXV2:

```text
TEST_RUNTIME_ACCESS_ARCHITECTURE.cmd
```

or:

```text
dotnet run --project src\PowerScalerLabs.Runtime\PowerScalerLabs.Runtime.csproj -c Release -- --architecture-self-test
```

The test checks that:

- a `0.01` health change is detected;
- sub-tolerance noise is suppressed;
- provenance keys are unique and cover the six focused offsets;
- the native import surface remains read/query/module-enumeration only;
- fighter-generation identity survives protocol serialization.

The Windows publisher runs this self-test automatically after the Release build.

## Build and launch on Windows

Extract into a fresh folder and run:

```text
START_HERE.cmd
```

The publisher performs structural audits, the Runtime Access Architecture deep audit, chronology audits, the HealthScale companion integrity audit, a Release build, and the architecture self-test before publishing.

Expected outputs:

```text
artifacts\PowerScalerLabs\PowerScalerLabs.exe
artifacts\PowerScalerLabs\Runtime\PowerScalerLabs.Runtime.exe
artifacts\PowerScalerLabs\Companions\HealthScale\Payload\xinput_other.dll
artifacts\PowerScalerLabs\Companions\HealthScale\Payload\HealthScale.ini
```

## Required live test after this gate

The first in-game test should be an observation test, not a scaling test. It should verify:

1. DBXV2 remains stable and responsive with the external runtime attached.
2. The XV2 Patcher provider resolves one stable BattleCore address.
3. Fighter generations change correctly across character reloads and rematches.
4. `0.01` health changes appear in chronology and semantic events.
5. Raw observations and semantic events agree on address, identity, and value.
6. Observer and chronology read budgets remain bounded.
7. Poll overruns and failed reads are measured.
8. No HealthScale source or runtime state is coupled to PowerScaler telemetry.

Do not begin code hooks, causal writes, or production scaling until this live observation test has been reviewed.

## Audit documents

- `RUNTIME_ACCESS_ARCHITECTURE_GATE0_AUDIT.md`
- `RUNTIME_ACCESS_ARCHITECTURE_GATE0_TEST.md`
- `RUNTIME_ACCESS_ARCHITECTURE_GATE0_DEEP_AUDIT.txt`
- `CHRONOLOGICAL_TELEMETRY_AUDIT.md`
- `CHRONOLOGICAL_TELEMETRY_TEST.md`
- `HEALTHSCALE_COMPANION_AUDIT.md`
- `HEALTHSCALE_COMPANION_TEST.md`

No Windows compilation or live Xenoverse success is claimed from the packaging environment. Those validations must run on the target Windows system.
