# Scanner Decluttering Gate 2 — Semantic Focus Audit

## Live audit finding

The first Windows screenshot proved physical deduplication worked: 20,245 typed interpretations collapsed to 2,561 physical offsets (87.3%). It also exposed a semantic-tier defect: generic repeatable `Strong` records were being treated as `Correlated / High-confidence`, causing dozens of unrelated offsets to appear as Health findings.

## Bounded correction

This hotfix changes only candidate validation/tiering and the default candidate filter. It does not change the external memory reader, scan range, sampling cadence, IPC, recording, game process permissions, or HealthScaler boundary.

- Generic repeatability (`Strong`) now means `Promising / Observed`, not semantic correlation.
- `Correlated / High-confidence` is reserved for explicit manual promotion, structured current/capacity pairing, code anchoring, causal validation, or verification.
- Legacy automatic `Strong/Correlated` records are demoted during load while manual and stronger validation remains protected.
- The default Research view now shows `Known effect + High-confidence` only. Promising hypotheses remain available through the signal-tier dropdown.
- Raw typed evidence and all 2,561 physical groups remain lossless.

## Expected result on the supplied archive

```text
Raw interpretations          20,245
Physical offsets              2,561
Known effects                     2
High-confidence groups            4
  +0x10C / +0x110 Ki
  +0x16C / +0x170 Stamina
Default focused rows              6
```

The exact Promising count can increase because the 56 generic Strong groups are correctly moved out of High-confidence. No evidence is discarded.

## Safety and optimization

The change reduces default WPF row materialization from 1,535 rows to six on the supplied data. That lowers sorting, binding, layout, and rendering work on every candidate refresh. No scanner sampling or persistence work is added.

## Gate boundary

This remains the Scanner Decluttering Gate. Native overlay, gameplay video, passive instruction hooks, and causal writes are not present in this build.
