# PowerScaler Labs — Chronological Telemetry Gate 1 Audit

> Historical inherited-gate document: this records the original Gate 1 schema 6 implementation. The current checkpoint extends it with protocol/session schema 7; see `RUNTIME_ACCESS_ARCHITECTURE_GATE0_AUDIT.md`.


## Gate purpose

This gate adds a dedicated high-resolution, read-only chronology lane without accelerating or replacing the broad capability scanner.

The broad scanner remains responsible for discovery, baselines, comparisons, snapshots, pointer traversal, raw candidate evidence, and physical-offset grouping. The chronology lane answers a narrower question:

> For the small set of promoted resource anchors, what changed, in which fighter, and at what monotonic time?

## Bounded source changes

### Protocol schema 6

The runtime protocol now carries:

- chronology watch targets;
- chronology configuration;
- chronology samples;
- chronology runtime diagnostics;
- a global sample sequence;
- a poll capture ID;
- an epoch ID;
- the exact QPC sample tick;
- the start and completion QPC ticks of the poll containing the read.

The default watchlist contains six focused `Battle_Mob` Float32 anchors:

| Offset | Label | Validation stage |
|---|---|---|
| `+0x100` | Current health | Verified |
| `+0x104` | Maximum health | Verified |
| `+0x10C` | Current Ki candidate | Correlated |
| `+0x110` | Maximum Ki candidate | Correlated |
| `+0x16C` | Current stamina candidate | Correlated |
| `+0x170` | Maximum stamina candidate | Correlated |

No additional candidates are silently promoted by this gate.

### Isolated runtime worker

`ChronologySampler` runs independently from the 100 ms runtime status/IPC heartbeat. Its default polling interval is 25 ms.

The default maximum work is:

- 6 targets;
- 2 fighter objects;
- 12 scalar reads per poll;
- approximately 40 polls per second;
- approximately 480 scalar `ReadProcessMemory` calls per second when two fighters are active.

The sampler emits only:

- the first readable value for a target in an epoch; and
- a subsequent value whenever its raw representation changes.

Stable polling is counted in diagnostics but is not serialized. This avoids turning a 25 ms sampler into a large disk stream.

### Read-path optimization

The main scanner still validates arbitrary ranges through `VirtualQueryEx` before reading.

The chronology lane is different: it reads only small scalar offsets rooted in fighter objects already validated by the observer. `TryReadKnownReadable` therefore performs a fail-closed `ReadProcessMemory` call without a separate `VirtualQueryEx` call for every scalar sample.

If a fighter disappears between the 100 ms observer heartbeat and a 25 ms chronology poll, `ReadProcessMemory` fails and the sample is skipped. The chronology lane never writes, allocates remote memory, injects code, or changes the game.

### Runtime/app separation

Chronology commands are separated from scanner commands before the observer receives them. The broad scanner does not interpret chronology commands, and chronology configuration does not alter scanner range, stride, pointer traversal, cadence, or candidate classification.

### Session persistence

Session schema 6 adds:

- `chronology-samples.jsonl` — canonical ordered chronology stream;
- `chronology-watchlist.json` — exact targets and polling configuration;
- chronology baseline/change records in `timeline.jsonl`;
- receipt-latency measurements;
- sequence-gap detection;
- out-of-order detection;
- queue-drop count;
- intentionally invalidated stale-sample count;
- epoch-local poll/read/unreadable counts;
- poll-overrun count;
- maximum poll duration;
- maximum app receipt latency.

Each recording requests a new chronology epoch after the session writers are opened, rejects stale pre-session samples, and waits for fresh focused initial values. Normal stop requests a pause barrier, lets an active poll finish, drains delivery queues, closes the session, and then resumes sampling.

### Review interface

The Recording page now contains:

- sampler state;
- target and interval count;
- total samples and changes;
- pending and dropped samples;
- last and maximum poll duration;
- poll-overrun count;
- a millisecond-resolution chronological change table;
- a separate semantic-event tab.

The existing guided WPF test overlay remains temporary and is not accepted as the final native in-game overlay. This gate does not add ReShade, graphics hooks, injection, or game-window focus changes.

## Optimization invariants

- The broad runtime heartbeat remains 100 ms.
- The broad scanner polling floor remains unchanged.
- The 25 ms lane is restricted to at most 64 explicitly configured scalar targets.
- The default lane uses six targets and two fighters.
- Stable values are not queued or written repeatedly.
- The chronology queue is bounded at 20,000 samples.
- Delivery sequence numbers are assigned only after epoch validation, preventing intentional stale-sample rejection from creating false sequence gaps.
- IPC drains at most 4,096 chronology samples per status frame.
- Session writers retain 64 KiB sequential buffers and one-second batched flushing.
- WPF displays at most 1,000 chronology rows.

## Safety invariants

- External process only.
- `PROCESS_VM_READ` and query rights only.
- No `WriteProcessMemory`.
- No remote allocation.
- No remote thread creation.
- No hook installation.
- No DX11 interception in this gate.
- No game-folder installation.
- No dependency on or modification of `xinput_other.dll`.
- The chronology runtime remains independent from the sealed HealthScale companion; companion file management is desktop-app-only.

## Audit status

Static source, XML/XAML, protocol-integration, persistence, queue-bound, optimization, and safety audits are included in the package.

A Windows .NET build and live Xenoverse test are still required. This environment cannot claim:

- successful Windows compilation;
- live 25 ms cadence;
- live CPU or frame-time impact;
- live zero-drop operation;
- live game stability.

## Source files changed from Scanner Decluttering Gate 2

Core chronology implementation:

- `src/PowerScalerLabs.Protocol/RuntimeProtocol.cs`
- `src/PowerScalerLabs.Runtime/ChronologySampler.cs` (new)
- `src/PowerScalerLabs.Runtime/GameMemoryReader.cs`
- `src/PowerScalerLabs.Runtime/RuntimeHost.cs`
- `src/PowerScalerLabs.App/Models/TelemetryViewModels.cs`
- `src/PowerScalerLabs.App/MainWindow.xaml`
- `src/PowerScalerLabs.App/MainWindow.xaml.cs`
- `src/PowerScalerLabs.App/Recording/SessionRecorder.cs`
- `src/PowerScalerLabs.App/Overlay/ExperimentOverlayWindow.xaml.cs`

Build, verification, and documentation:

- `scripts/Deep-Audit-ChronologicalTelemetry.ps1` (new)
- `scripts/Deep-Audit-DeclutterChronology.ps1`
- `scripts/Verify-PowerScalerLabs.ps1`
- `scripts/Publish-Windows.ps1`
- `CHRONOLOGICAL_TELEMETRY_AUDIT.md` (new)
- `CHRONOLOGICAL_TELEMETRY_TEST.md` (new)
- `CHRONOLOGICAL_TELEMETRY_DEEP_AUDIT.txt` (new)
- `README.md`
- `VALIDATION_REPORT.txt`
- `START_HERE.cmd`
- `BUILD_ID.txt`

Candidate grouping/classification logic, scanner scan-range logic, pointer traversal, fighter discovery, game-build validation, and native access-right declarations were not broadened by this gate.
