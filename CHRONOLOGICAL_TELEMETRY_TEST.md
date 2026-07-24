# Chronological Telemetry Gate 1 — Windows Test

## Scope

Test only the focused chronology lane and verify that the prior scanner-decluttering behavior remains intact.

Do not evaluate the temporary WPF overlay as the final overlay in this gate.

## 1. Build and launch

1. Extract the ZIP into a fresh folder.
2. Run `START_HERE.cmd`.
3. Confirm the publish log completes without warnings or errors.
4. Confirm the companion runtime reaches **Connected**.
5. Launch Xenoverse 2 version 1.25.2 with the validated XV2 Patcher setup.
6. Enter offline Training with two fighters.

## 2. Idle sampler audit

Open **Recording**.

Expected:

- Chronology reports **6 focused targets**.
- Interval reports **25 ms**.
- Sampling reports active after fighter acquisition.
- Initial values appear for both fighters: normally 12 rows total.
- Queue remains near zero.
- Dropped remains zero.
- Last poll duration remains well below 25 ms during ordinary operation.

Leave the game idle for two minutes.

Pass conditions:

- no new rows are continuously written for stable values;
- sample count remains mostly unchanged after the initial rows;
- no dropped samples;
- no game instability;
- no meaningful frame-rate or frame-pacing regression.

## 3. Start a recorded chronology epoch

1. Press **Start Recording**.
2. Wait for the Start action to confirm.

Expected:

- a fresh chronology epoch is logged;
- Start does not confirm until focused initial anchors are received when fighters are active;
- fresh rows marked `(initial)` appear after recording starts;
- the session folder contains `chronology-samples.jsonl` and `chronology-watchlist.json`.

## 4. Directional resource tests

Perform each action separately with a short idle interval between actions.

### Spend Ki

Expected focused change:

- attacker `+0x10C` decreases;
- `+0x110` normally remains stable.

### Regenerate or gain Ki

Expected focused change:

- attacker `+0x10C` increases.

### Spend stamina

Expected focused change:

- attacker `+0x16C` decreases;
- `+0x170` normally remains stable.

### Regenerate stamina

Expected focused change:

- attacker `+0x16C` increases.

### Take damage

Expected focused change:

- defender `+0x100` decreases.

The table should show millisecond timestamps, increasing sequence numbers, fighter slot, offset, previous value, current value, delta, and validation stage.

## 5. Stop and inspect

Press **Stop & Save**. The app should pause chronology, wait for any active poll and both delivery queues to drain, save the session, and then resume chronology automatically.

Inspect the newest session:

```text
session.json
frames.jsonl
events.jsonl
scanner-observations.jsonl
chronology-samples.jsonl
chronology-watchlist.json
timeline.jsonl
candidate-keys.jsonl
candidate-index.json
```

In `session.json`, the following should be zero:

- `chronologyOutOfOrderCount`;
- `chronologySequenceGapCount`;
- `droppedChronologySampleCount`;
- `droppedScannerObservationCount`.

`invalidatedChronologySampleCount` may be nonzero when stale pre-epoch samples were intentionally rejected; this is diagnostic and must not create sequence gaps.

Record these values:

- `chronologySampleCount`;
- `chronologyChangedSampleCount`;
- `chronologyInitialSampleCount`;
- `chronologyPollCount`;
- `chronologyReadCount`;
- `chronologyUnreadableReadCount`;
- `chronologyPollOverrunCount`;
- `maximumChronologyPollDurationMilliseconds`;
- `maximumChronologyReceiptLatencyMilliseconds`.

## 6. Declutter regression check

Open **Candidates & Findings** with **Research view** selected.

Expected focused rows remain approximately:

- 2 Known effects;
- 4 High-confidence correlated resource offsets;
- 6 focused rows shown.

The chronology gate must not restore the earlier flood of generic Strong/Correlated Health candidates.

## Return for the live audit

Return:

- `logs\publish.log`;
- the complete newest session folder;
- a screenshot of the Recording page after several changes;
- a screenshot of Candidates & Findings in Research view;
- a note about game FPS/frame pacing and any stutter.
