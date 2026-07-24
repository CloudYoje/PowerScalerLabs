# HealthScale Runtime 1.1.1 Candidate

Health-only native runtime for DBXV2 1.25.02.0 / XV2 Patcher 4.64.

## Added in 1.1.1

- removed every current/max health-domain multiplier ceiling;
- accepts arbitrarily large finite mixed-domain transformation frames;
- keeps ownership, vtable, memory-range, finite-value, positive-maximum, zero-HP,
  and transition-coherence checks as the safety boundary;
- retains the 1.1.0 quest/additional HUD-lane discovery and target-rebase repair.

## Safety

- exact writer signatures are checked before hooks activate;
- Battle_Mob candidates must be private writable game objects with game vtables;
- HUD hooks write only the displaced HudCockpit health destination;
- gameplay correction writes only current HP after live slot ownership checks;
- maximum HP is never written;
- zero HP is never revived;
- incoherent mixed-domain pairs cannot replace the frozen coherent ratio.

## Configuration

`NormalizeDiscoveredHudLanes=1` enables quest/additional lane normalization when
`NormalizeAllHudLanes=1` is also enabled.

## Build

Build `Release|x64` in Visual Studio. Output: `xinput_other.dll`.
