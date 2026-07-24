# PowerScaler Labs - Declutter + Chronology Windows Gate

This is the required build and live-session test before native overlay, gameplay video, or validation-hook work begins.

## 1. Publish gate

1. Extract the source package completely.
2. Run `START_HERE.cmd`.
3. Require the deep audit, restore, Release build, and both self-contained x64 publishes to finish successfully.
4. Keep `logs\publish.log`.
5. Confirm these files exist:

```text
artifacts\PowerScalerLabs\PowerScalerLabs.exe
artifacts\PowerScalerLabs\Runtime\PowerScalerLabs.Runtime.exe
artifacts\PowerScalerLabs\BUILD_INFO.txt
```

Stop on any compiler warning promoted to an error, manifest failure, audit failure, or missing artifact.

## 2. Previous-data migration gate

Use `IMPORT_PREVIOUS_DATA.cmd` to import the previous PowerScaler Labs Data folder or extracted Candidates/Findings/Sessions folders.

After launch, confirm:

```text
Raw typed candidates:       20,245
Physical groups:             2,561
Default Research view:           6
Known effects:                   2
High-confidence:                60
Promising:                   1,475
Needs another trial:         1,024
```

Confirm the raw candidate store still exists. Grouping must not overwrite or delete `Candidates\candidates.json`.

## 3. Candidate-structure gate

In Candidates & Findings, verify:

- one row represents one physical region/offset;
- the preferred type and other types are visible separately;
- Research view shows Known effects and High-confidence correlated rows only;
- All tiers restores all physical groups;
- validation filters include Observed, Correlated, Code-anchored, Causally validated, and Verified;
- manual promotion produces Correlated rather than falsely producing Verified;
- Known Health effects are separated from unresolved candidates;
- +0x10C/+0x110 appear as the Ki current/capacity pair;
- +0x16C/+0x170 appear as the Stamina current/capacity pair.

Expected initial stages:

```text
+0x100 Current Health       Verified
+0x104 Maximum Health       Verified
+0x10C Current Ki           Correlated
+0x110 Maximum Ki           Correlated
+0x16C Current Stamina      Correlated
+0x170 Maximum Stamina      Correlated
```

## 4. Controlled live scan

Use offline Training mode. Keep the frozen HealthScaler installed and unchanged.

Recommended scanner configuration:

```text
Root range: +0x000 through +0x1000
Stride: 4
Types: Float32, Int32, UInt32, Pointer64
Maximum fighters: 2
Pointer depth: 1
Child scan size: 0x200
Maximum child objects: 4
Continuous tracking: enabled
Batch: 400
```

Record one session with three repetitions each of:

```text
Idle / Stable
Spend Ki
Regenerate or Gain Ki
Spend Stamina
Regenerate Stamina
Take Damage
Heal / Recover Health
```

For every repetition:

1. Capture a baseline.
2. Wait for Pending to reach zero.
3. Perform the selected action.
4. Compare results.
5. Wait for Pending to reach zero.
6. Do not stop recording while observations remain queued.

The retained WPF Guided Overlay can still issue these commands, but it is not the final native in-game overlay and may pause Xenoverse when it receives focus. This gate measures scanner and chronology behavior, not overlay acceptance.

## 5. Chronology gate

The saved session must contain:

```text
session.json
frames.jsonl
events.jsonl
scanner-observations.jsonl
timeline.jsonl
candidate-keys.jsonl
candidate-index.json
```

Validate:

- `session.json` reports schema version 5;
- monotonic frequency is positive;
- start, last, and end ticks are ordered;
- every `relativeMilliseconds` value is non-negative;
- timeline records are nondecreasing by monotonic tick;
- scanner-change entries map to changed raw scanner observations;
- stable raw scanner observations remain in `scanner-observations.jsonl` but are not duplicated into `timeline.jsonl`;
- no scanner observations are dropped;
- pending count returns to zero before save.

## 6. Persistence and recovery gate

1. Close PowerScaler Labs normally.
2. Relaunch it.
3. Confirm raw candidates, grouped candidates, validation stages, pair relationships, and findings reload.
4. Confirm these exports exist:

```text
Candidates\physical-groups.json
Candidates\unresolved-index.json
Candidates\ByTier\*.json
Candidates\ByValidation\*.json
Findings\verified-findings.json
```

5. Confirm the latest session remains readable after restart.

## 7. Safety and optimization gate

During the session confirm:

- heartbeat remains stable;
- app remains responsive while observations drain;
- pending returns to zero;
- dropped remains zero;
- Xenoverse remains stable;
- HealthScaler behavior is unchanged;
- no file is installed into or replaced in the Xenoverse `bin` folder;
- no memory-write or injection behavior occurs;
- session and candidate storage growth is reasonable;
- `timeline.jsonl` is materially smaller than the raw scanner stream.

Return these artifacts for audit before the next gate:

```text
logs\publish.log
one complete new session folder
Candidates folder
Findings folder
```
