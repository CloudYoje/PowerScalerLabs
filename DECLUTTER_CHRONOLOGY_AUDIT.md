# Scanner Declutter + Chronology Foundation Deep Audit

## Gate decision

This gate changes only the candidate interpretation/presentation layer and the timebase carried by the existing external scanner. It does not add video capture, a native graphics overlay, instruction hooks, injection, or causal writes. Those changes remain quarantined behind later gates so each performance and stability impact can be measured separately.

## Baseline examined

The input source was the Guided Overlay Build 2 tree. The supplied candidate archive contained 20,245 typed candidate records covering 2,561 unique `(region path, object offset)` locations.

The pre-change system treated typed interpretations as peer rows. One physical location could therefore appear as Float32, Int32, UInt32, Byte, or Pointer64 candidates at the same time.

## Files changed in this gate

```text
BUILD_ID.txt
README.md
CAPABILITY_SCANNER_TEST.md
DEEP_AUDIT_REPORT.md
DECLUTTER_CHRONOLOGY_AUDIT.md
VALIDATION_REPORT.txt
GUIDED_OVERLAY_AUDIT.md
GUIDED_OVERLAY_TEST.md
scripts/Deep-Audit-DeclutterChronology.ps1
scripts/Import-PreviousData.ps1
scripts/Publish-Windows.ps1
scripts/Verify-PowerScalerLabs.ps1
src/PowerScalerLabs.Protocol/RuntimeProtocol.cs
src/PowerScalerLabs.Runtime/RuntimeHost.cs
src/PowerScalerLabs.Runtime/ExternalCapabilityObserver.cs
src/PowerScalerLabs.Runtime/ObjectCapabilityScanner.cs
src/PowerScalerLabs.App/Models/TelemetryViewModels.cs
src/PowerScalerLabs.App/Recording/CandidateGroupBuilder.cs
src/PowerScalerLabs.App/Recording/CandidateStore.cs
src/PowerScalerLabs.App/Recording/SessionRecorder.cs
src/PowerScalerLabs.App/MainWindow.xaml
src/PowerScalerLabs.App/MainWindow.xaml.cs
PACKAGE_MANIFEST.sha256
```

`CandidateGroupBuilder.cs` and `Deep-Audit-DeclutterChronology.ps1` are new. `Import-PreviousData.ps1` changed only to normalize Windows PowerShell 5.1-compatible UTF-8 BOM encoding.

## Declutter result on supplied data

```text
Raw typed records                  20,245
Physical offset groups              2,561
Rows eliminated from normal view   17,684
Reduction                           87.35%
```

After preferred-type ranking and structured-pair promotion, the 2,561 grouped rows use these preferred interpretations:

```text
Float32       1,380
Int32           501
Pointer64       386
Byte            294
```

The signal tiers are:

```text
Known effect                         2
High-confidence                      4
Promising                        1,531
Needs another trial              1,024
Background noise                     0 in this archive
```

The default Research view displays Known effects and High-confidence correlated groups only:

```text
Default rows shown                    6
Reduction from raw rows          99.97%
```

This is a view reduction, not evidence destruction. All 20,245 raw records remain persisted and available for advanced inspection.

## Preferred-type audit

The ranking layer evaluates evidence count, change count, status, confidence, classification confidence, validation stage, value shape, and known references.

A specific pointer false-positive defense was added. A widened 32-bit numeric bit pattern is not rewarded as a credible x64 pointer unless its observed range reaches normal 64-bit user-address space and its value shape is pointer-like. This prevents Pointer64 interpretations from outranking useful Float32 or Int32 interpretations simply because the bit pattern is technically canonical.

Alternative interpretations are retained under each physical group and are never silently discarded. Pair assignment is non-overlapping, and verified known-effect groups are protected from being reused as an inferred capacity endpoint.

## Known-effect subtraction and validation stages

The known health anchors are assigned as Verified / Known effect:

```text
Battle_Mob +0x100 Float32  Current Health
Battle_Mob +0x104 Float32  Maximum Health
```

They are marked explained so later experiment review can separate them from unresolved effects.

The validation ladder is now explicit:

```text
Observed -> Correlated -> Code-anchored -> Causally validated -> Verified
```

Manual promotion reaches Correlated only. Durable APIs are present for later code-anchor, causal-validation, and verified evidence, but this gate does not fabricate those stronger stages.

A second source audit found and corrected a future-facing validation regression risk: ordinary evidence reevaluation could have overwritten a stronger validation stage. Validation is now monotonic. Code-anchored, causally validated, and verified records are protected from automatic downgrade and from ordinary Promote/Mark Noise actions. Validation evidence IDs are unique, causal evidence is quarantined until a code anchor exists, and `MarkVerified` is fail-closed unless the record has a code anchor, at least two causal passes, at least two observed actor objects, and repeated session or experiment coverage.

## Resource-pair audit

The pair detector requires:

- a Float32 current candidate;
- an adjacent Float32 candidate at `current + 4`;
- at least two current-value changes;
- a stable, positive capacity value;
- current values that remain within the capacity envelope;
- a sensible bounded range;
- directional action evidence with a minimum score and margin over the runner-up family.

The verified Health pair is excluded from unknown-pair inference. Health is not allowed as a fallback family for unknown adjacent pairs, preventing compound damage/KO sessions from incorrectly turning Ki into Health.

The supplied evidence produced exactly two inferred resource pairs:

```text
+0x10C / +0x110  Ki
Directional score: 6.3814
Runner-up score:   -1.0000

+0x16C / +0x170  Stamina
Directional score: 5.4240
Runner-up score:    0.0000
```

Observed ranges and behavior:

```text
+0x10C Current Ki       range 0..560, 200 changes, 70 stable observations
+0x110 Maximum Ki       range 400..560, 0 changes, 122 stable observations
+0x16C Current Stamina  range 0..776, 88 changes, 117 stable observations
+0x170 Maximum Stamina  range 560..776, 0 changes, 122 stable observations
```

These are promoted to Correlated / High-confidence, not Verified.

## Chronology design audit

Protocol version 5 carries a common high-resolution timebase:

- runtime status: UTC, monotonic ticks, and monotonic frequency;
- fighter snapshots: UTC and monotonic ticks;
- telemetry events: UTC and monotonic ticks;
- scanner observations: UTC and monotonic ticks;
- scanner status: last-capture UTC and monotonic ticks.

The runtime captures `Stopwatch.GetTimestamp()` once per observation cycle and propagates that value through the observer and scanner. The app stores the frequency and session start tick, allowing stable relative milliseconds to be calculated later for synchronized video-frame indexing.

## Recording optimization audit

A new `timeline.jsonl` provides a sparse chronological index. It records:

- session start;
- runtime frames;
- telemetry events;
- scanner changes;
- session stop.

It deliberately does not duplicate every stable scanner observation. Complete raw acquisition remains in `scanner-observations.jsonl`.

This separation provides both:

```text
Lossless evidence stream          scanner-observations.jsonl
Fast chronological review index  timeline.jsonl
```

The design avoids doubling the largest stream, reduces disk writes, lowers JSON serialization load, and keeps future video review responsive.

## Static source audit results

The packaging audit passed all 15 primary checks. A second post-change validation-ladder audit also passed the known-group protection, non-overlapping pair assignment, unique evidence, monotonic-stage, and fail-closed verification invariants:

```text
PASS strict UTF-8 source decoding
PASS source binary exclusion
PASS XML/XAML parse (9 files)
PASS XAML event-handler resolution
PASS C# lexical delimiter scan
PASS read-only runtime boundary
PASS runtime required read APIs
PASS HealthScaler source boundary
PASS monotonic chronology propagation
PASS sparse chronology index
PASS candidate declutter + validation-stage implementation
PASS candidate grid bindings (21 properties)
PASS Windows PowerShell 5.1 encoding compatibility
PASS real-data physical grouping metrics
PASS resource anchor/pair coverage
```

The static audit confirmed:

```text
Raw candidates:             20,245
Physical groups:             2,561
Physical-group reduction:   87.35%
```

## Safety audit

Runtime source contains query/read access only. The audit rejects tokens associated with process-memory writes, remote allocation, remote threads, broad process access, and Windows input hooks.

The source tree contains no generated DLL, EXE, PDB, OBJ, or LIB files. `xinput_other.dll` is absent from application/runtime source ownership. XV2 Patcher detection remains read-only through `xinput1_3.dll` module inspection.

## Unavailable and not claimed

The packaging environment does not contain the .NET SDK, MSBuild, Windows PowerShell, Windows Desktop runtime, DirectX, or Xenoverse 2. Therefore this report does not claim:

- successful WPF compilation;
- successful Windows publish;
- successful PowerShell execution;
- live DBXV2 attachment;
- live scanner throughput or CPU measurements;
- live timeline ordering;
- live candidate-store migration;
- native in-game overlay behavior;
- gameplay video capture;
- passive instruction hooks;
- causal writes or restoration.

## Required Windows gate before the next subsystem

1. Run `START_HERE.cmd` and require a clean Release build and publish.
2. Import the previous data archive through the supplied import workflow.
3. Confirm 20,245 raw candidates become 2,561 physical groups.
4. Confirm the default Research view shows exactly 6 groups on the supplied archive: 2 Known Health anchors and 4 Correlated Ki/Stamina resource fields.
5. Run one controlled Training-mode session with baseline, comparison, and repeated resource actions.
6. Confirm pending and dropped counts return to zero.
7. Confirm `timeline.jsonl` timestamps are monotonic and scanner changes align with `scanner-observations.jsonl`.
8. Confirm session save, app restart, and candidate reload preserve raw and grouped data.
9. Confirm Xenoverse and the frozen HealthScaler behave exactly as before.
10. Return the publish log, one new session folder, Candidates, and Findings for the next deep audit.

Only after this gate passes should native overlay/video capture work begin.
