# Runtime Access Architecture Gate 0 — Audit

## Purpose

This gate renews the external telemetry access foundation before live DBXV2 testing. It does not add scaling, writes, hooks, injection, or direct-signature discovery.

## Implemented boundaries

### Provider-based BattleCore resolution

`ExternalCapabilityObserver` no longer owns XV2 Patcher-specific constants or pointer traversal. `BattleCoreLocatorCoordinator` runs registered `IBattleCoreLocator` implementations and fails closed if independent providers disagree.

The historical XV2 Patcher 1.25.2 route is retained as a versioned provider. Initial acquisition performs full structural validation until stable; after activation, full provider resolution is periodic rather than repeated every 100 ms. The direct DBXV2 signature provider is an inert research placeholder and performs no scan.

### Address provenance

`AddressProvenanceCatalog` assigns stable keys and metadata to the BattleCore root, mob array, fighter vtable, and six focused resource offsets. Provenance is transmitted in protocol schema 7 and written to each session.

### Fighter generations

Each acquisition is bound to a process instance, battle instance, slot, and acquisition generation. Actor addresses are evidence fields, not permanent identities. Chronology keys and samples now carry the identity key and slot generation.

### Raw observation separation

Selected low-level facts are represented as `RawMemoryObservationMessage` records. Slot-pointer attempts preserve success/failure; validated health reads preserve exact raw bits. A changed slot pointer is not mislabeled with the previous fighter generation—the pointer fact remains unbound until the new object is structurally validated and assigned a new identity. Semantic events remain separately interpreted, while the access metrics count all failed reads. Sessions write the factual stream to `raw-memory-observations.jsonl`.

### Compressed-scale comparisons

`TelemetryComparisonPolicy` replaces the observer’s one-point dead zone. Its semantic tolerance is `max(0.000001, scale × 0.000001)`. Exact raw-bit comparison remains in chronology.

### Read budgets

Every high-level read request, `ReadProcessMemory` call, and `VirtualQueryEx` operation is counted. Requests rejected before an OS read are distinct from failed OS reads; requested/completed bytes and query failures are also retained. Observer and chronology lanes report separate cumulative metrics.

## Safety findings

- Process access remains query + VM-read only.
- No game-memory write API is imported.
- No remote allocation or thread API is imported.
- No graphics/input hook is present.
- Provider disagreement results in no selected BattleCore.
- Unsupported game or patcher layouts fail closed.
- The direct-signature provider cannot silently activate.
- HealthScale remains source- and runtime-separated.

## Known limitations before live testing

- Only the historical XV2 Patcher provider can currently resolve BattleCore.
- Cumulative read metrics reset when a reader is recreated.
- Raw observer observations cover slot pointers and verified health fields; the high-rate chronology file remains the primary raw stream for Ki/stamina candidates.
- Character/preset identifiers are not yet part of fighter identity.
- No performance threshold has yet been approved because live frame-time data is required.
- No code-access anchor or causal proof is introduced.

## Audit conclusion

The source is ready for a Windows Release build, architecture self-test, and bounded in-game observation test. It is not ready for production scaling or invasive runtime access.
