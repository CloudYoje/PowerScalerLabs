# Runtime Access Architecture Gate 0 — Test Plan

## Phase A: offline Windows validation

1. Extract into a new folder.
2. Run `TEST_RUNTIME_ACCESS_ARCHITECTURE.cmd`.
3. Confirm all four self-test assertions pass.
4. Run `PUBLISH_WINDOWS.cmd`.
5. Confirm all deep audits, Release builds, the self-test, and HealthScale source-integrity checks pass.

## Phase B: attach without battle

1. Start PowerScaler Labs.
2. Start its runtime.
3. Launch DBXV2 1.25.2.0 with the validated XV2 Patcher layout.
4. Stay outside battle for at least one minute.
5. Confirm the provider report transitions from module detection to waiting for BattleCore without errors.
6. Record observer/chronology read calls, bytes, failures, and queries.

## Phase C: fighter identity

1. Enter offline Training mode with two fighters.
2. Confirm one stable BattleCore address and two fighter identities.
3. Record both identity keys, actor addresses, vtables, and slot generations.
4. Return to character select, choose different fighters, and re-enter Training.
5. Confirm newly acquired fighters have new generation identities even if an actor address is reused.
6. Repeat a rematch and confirm release/acquire events are ordered.

## Phase D: compressed health change

1. Use a controlled setup where a health change of approximately `0.01` can occur.
2. Confirm `chronology-samples.jsonl` records the exact raw transition.
3. Confirm the semantic observer emits a health change rather than suppressing it.
4. Confirm raw and semantic records share the same identity, offset, address, and value.

## Phase E: access budget

During at least five minutes of Training mode:

- capture observer and chronology read-call growth;
- capture bytes completed;
- capture failed reads;
- capture query count;
- capture chronology poll duration and overruns;
- observe DBXV2 frame pacing;
- note whether menus, transformations, knockouts, and rematches cause spikes.

No hard pass threshold is claimed in source. Review measured rates and frame-time behavior before setting one.

## Phase F: detach and shutdown

1. Stop recording.
2. Confirm chronology pauses, drains, and resumes correctly.
3. Stop the PowerScaler runtime.
4. Close DBXV2.
5. Confirm no orphan runtime process remains.
6. Confirm session files close cleanly and parse as JSON/JSONL.

## Failure rules

Stop the test and preserve logs if:

- providers disagree;
- BattleCore changes repeatedly without a scene transition;
- identity generations fail to change after release/reacquire;
- read failures climb continuously while objects are stable;
- DBXV2 stutters or becomes unstable;
- chronology drops or overruns become sustained;
- any write/hook/injection behavior appears.
