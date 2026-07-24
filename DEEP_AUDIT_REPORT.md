# Deep Compatibility and Optimization Audit

## Established scanner foundation retained

- External process discovery and read permission remain separate from the WPF app.
- Supported DBXV2 and XV2 Patcher layouts are validated before BattleCore acquisition.
- BattleCore candidates are structurally scored and stabilized before fighter observation.
- All scanning remains query/read only.
- Baselines are invalidated on fighter identity changes.
- Whole-region reads are attempted before chunk fallback.
- Pointer traversal remains bounded by depth, child count, child size, readable private memory, and visited-address tracking.
- Float comparisons retain relative tolerance.
- Complete and continuous capture limits remain enforced.
- Runtime command and observation queues remain bounded and count drops visibly.

## Declutter optimization

The flat typed-candidate presentation was replaced by a physical-offset grouping layer. Raw typed records remain append-only and durable.

On the supplied candidate archive:

```text
20,245 raw typed records -> 2,561 physical groups
87.35% physical-row reduction
6 rows in the default focused Research view on the supplied archive
```

Preferred-type ranking now penalizes low widened numeric values masquerading as Pointer64 values. Alternative interpretations remain attached to the group. Verified known-effect groups cannot be consumed by inferred-pair logic, and an offset already assigned to a resource pair cannot be assigned to an overlapping pair. The DataGrid uses grouped rows, virtualization, bulk replacement, and focused tier filtering.

Known effects are separated from unresolved signals, allowing future experiment analysis to subtract explained Health changes instead of mixing them into the target candidate list.

## Classification correction

Directional action evidence is used for resource hypotheses:

- Spend Ki expects decrease.
- Gain/Regenerate Ki expects increase.
- Spend Stamina expects decrease.
- Regenerate Stamina expects increase.
- Opposite-direction and unrelated evidence reduce specificity.

An adjacent stable-capacity relationship can promote both ends of a Float32 current/capacity pair. Unknown pairs do not infer Health, because compound damage and KO sessions can make unrelated resources co-occur with Health changes.

The supplied evidence promotes:

```text
+0x10C/+0x110 -> Correlated Ki pair
+0x16C/+0x170 -> Correlated Stamina pair
```

Verified Health remains:

```text
+0x100/+0x104 -> Verified Health pair
```

## Evidence-grade correction

The store distinguishes:

```text
Observed
Correlated
Code-anchored
Causally validated
Verified
```

Manual promotion cannot skip directly to causal or verified certainty. APIs for later code-anchor and causal-test evidence exist, but this gate does not use them without the required in-game validation subsystem.

Validation stages are monotonic. Automatic candidate reevaluation cannot downgrade Code-anchored, Causally validated, or Verified records. Ordinary promotion and noise rejection are blocked for protected stages. Duplicate evidence IDs do not increase validation counts. Causal evidence is quarantined until a code anchor exists. Final verification is fail-closed behind one code anchor, two unique causal passes, two observed actor objects, and repeated session or experiment coverage.

## Chronology foundation

A shared high-resolution monotonic clock now travels through protocol version 5. Runtime status, fighter snapshots, telemetry events, scanner observations, and scanner status carry monotonic ticks. Runtime frames also carry the frequency.

Session schema version 5 writes a sparse `timeline.jsonl` alongside the lossless raw streams. The timeline indexes changed scanner observations rather than duplicating all stable observations. This avoids doubling the largest stream and prepares exact nearest-frame matching for the later gameplay recorder.

## Recording and persistence optimization retained

- Session streams use large buffered sequential writers and timed flushes.
- Raw scanner evidence remains JSONL and append-only.
- Candidate keys are incremental; the sorted index is produced at completion.
- Session metadata rewrites counts rather than a growing key array.
- Candidate checkpoints remain adaptive.
- Derived exports run at flush/manual boundaries rather than every frame.
- Candidate and session metadata use atomic replacement.
- Partial writer initialization is cleaned up on failure.
- Recording cannot stop while runtime observations remain queued.

New grouped exports are generated outside the raw store:

```text
physical-groups.json
unresolved-index.json
ByTier
ByValidation
verified-findings.json
```

## UI optimization retained

- DataGrid row and column virtualization with recycling remains enabled.
- Scanner observation display remains bounded.
- Candidate display uses bulk replacement and grouped rows.
- Candidate refresh remains throttled during recording.
- Logs and event rows remain bounded.
- Window sizing remains work-area aware.
- Startup crashes remain logged under `%LOCALAPPDATA%\PowerScaler Labs\Logs`.

## Current overlay limitation

The retained Guided Test Overlay is still a separate WPF window. It can take focus and pause Xenoverse. It is not accepted as the final in-game overlay. Replacing it with a native DX11 overlay is deliberately deferred until this scanner/chronology gate passes live testing, so graphics-hook overhead and stability can be audited independently.

## Safety conclusion

The runtime source contains query/read APIs only. No game-memory write, DLL injection, remote thread, graphics hook, game-bin installer, or HealthScaler access path is present in this gate.

## Static audit conclusion

All 15 packaging checks passed, including UTF-8, XML/XAML, event handlers, lexical delimiters, candidate bindings, read-only boundaries, chronology propagation, sparse timeline behavior, real-data grouping metrics, and resource-pair coverage.

The definitive .NET build, Windows publish, live scanner performance, storage growth, chronology ordering, and game stability remain target-machine requirements.
