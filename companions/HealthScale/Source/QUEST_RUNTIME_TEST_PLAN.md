# HealthScale 1.1.1 Runtime Test Plan

## Installation isolation

Keep the frozen HealthScale build backed up. Build this candidate as
`xinput_other.dll` and install only the candidate DLL plus its matching
`HealthScale.ini` for the test.

Clear these before each run:

- `HealthScale.Runtime.log`
- prior `HealthScaleScanner_Reports/HealthScaleNormalization_*.txt`

## Test A — Training Room regression

1. Enter Training with a base character.
2. Take damage.
3. Confirm the single bar represents the same percentage.
4. Transform and detransform once.
5. Confirm no full heal and no zero-HP revival.

## Test B — Kaioken x20 cancellation

1. Use the Frieza-Saga Goku preset with the x20 path.
2. Transform into Kaioken x20.
3. Lose enough health to make the percentage obvious, preferably 40–70%.
4. Cancel Kaioken without healing.
5. Confirm base-form health retains the same percentage.

Required log invariant:

```text
MAX-HP CHANGE ... old-ratio=X
CORRECTION QUEUED ... preserved-ratio=X
CORRECTION TARGET-DOMAIN REBASE ... held-ratio=X
CORRECTION APPLIED ... tracked-ratio=X
```

## Test C — Initial quest enemies

1. Start the same quest used in the video.
2. Lock onto every initial enemy.
3. Damage each enemy without defeating them immediately.
4. Confirm each target bar is a normalized single percentage bar.

## Test D — Reinforcements and stage transitions

1. Continue through the portal and dialogue/objective gates.
2. Lock onto newly spawned enemies in every wave.
3. Confirm their bars normalize without restarting the game.
4. Confirm the player bar remains correct across the transition.

## Test E — Quest transformations, revives, and replacements

When available in the quest:

- allow an enemy to transform;
- observe a revive/respawn;
- observe a replacement actor or new wave occupying an old slot.

Confirm no full heal unless scripted, no zero-HP revival, and no unnormalized
multi-layer target bar.

## Files to return after testing

- full or split MP4 footage;
- `HealthScale.Runtime.log`;
- newest `HealthScaleNormalization_*.txt` report.


## Test F — high-multiplier future-form regression

When Super Saiyan God, Super Saiyan Blue, or another transformation with a
health multiplier above the old 256x ceiling is available:

1. Enter the form at a clearly damaged percentage.
2. Remain transformed long enough for maximum HP to stabilize.
3. Take additional damage.
4. Revert to the prior form.
5. Confirm the same live percentage is preserved in both directions.

There must be no rejection or cancellation caused solely by the magnitude of
`current HP / maximum HP` during mixed-domain frames.
