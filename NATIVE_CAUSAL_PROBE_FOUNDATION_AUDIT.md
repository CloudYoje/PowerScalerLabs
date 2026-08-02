# Native Causal Probe Foundation Audit

## Scope

This gate adds the transport and lifecycle foundation for causal research. It does not add hardware watchpoints, exception handlers, hooks, stack capture, combat interpretation, or gameplay writes.

## Components added

- `PowerScalerLabs.ProbeHost`: a separate managed win-x64 executable that alone owns explicit injection privileges.
- `PowerScalerLabs.NativeProbe`: a native x64 DLL with a minimal `DllMain`, explicit `PSL_Initialize` export, ABI validation, heartbeat worker, inert state, and safe-to-unload state.
- `ProbeProtocol` version 1: a named-pipe protocol independent from Runtime protocol 8.
- `PowerScalerProbeAbi.h`: a fixed-width, size-checked shared-memory ABI with session identity, two heartbeats, command mailbox, counters, and a bounded future event ring.

## Privilege boundaries

`PowerScalerLabs.Runtime` remains an external read-only observer. It did not acquire VM-write, VM-operation, remote-thread, injection, hook, or gameplay-write behavior. Its access status remains truthful for that passive lane.

`PowerScalerLabs.ProbeHost` is the only managed component that requests query, read, write, VM-operation, and remote-thread access. It does so only after the user presses **Attach Probe** and only for a live native-x64 `DBXV2.exe` PID supplied by the passive Runtime.

The sealed HealthScale companion source is unchanged and remains independently audited and built.

## Attach lifecycle

1. The App starts ProbeHost in an unattached `Idle` state.
2. The user explicitly requests attachment to the currently observed DBXV2 PID.
3. ProbeHost validates process identity and x64 architecture.
4. ProbeHost creates a nonce-bound mapping and synchronization events.
5. ProbeHost loads the native DLL and confirms its remote module by enumeration.
6. ProbeHost resolves and calls `PSL_Initialize` by remote module RVA.
7. Both sides validate ABI dimensions, PIDs, nonce, and QPC frequency.
8. ProbeHost reports `Ready` only after a native heartbeat is observed.

No instrumentation is armed and the active-watchpoint count remains zero.

## Detach and failure behavior

Detach sends a shared-memory shutdown command. NativeProbe enters `ShuttingDown`, neutralizes the currently empty instrumentation set, acknowledges the command, and enters `SafeToUnload`. ProbeHost calls remote `FreeLibrary` only after that state and confirms the module disappeared.

Wrong PID, wrong executable, non-x64 target, missing DLL, stale loaded probe, ABI mismatch, handshake timeout, or unsafe unload all fail closed. If a host heartbeat becomes stale, NativeProbe enters an inert state and remains loaded. If DBXV2 exits, host-side session resources are discarded.

## Deferred work

- HP writer discovery and all watchpoints;
- vectored exception handling and debug-register management;
- trace event production and interpretation;
- register, XMM, stack, attacker, victim, or damage semantics;
- signatures and permanent hooks;
- all stat, resource, damage, HUD, and gameplay writes.

## Verification

```powershell
.\scripts\Verify-PowerScalerLabs.ps1
.\scripts\Deep-Audit-HealthScaleCompanion.ps1
dotnet run --project .\src\PowerScalerLabs.Runtime\PowerScalerLabs.Runtime.csproj -c Release -- --architecture-self-test
dotnet run --project .\src\PowerScalerLabs.ProbeHost\PowerScalerLabs.ProbeHost.csproj -c Release -- --architecture-self-test
.\scripts\Publish-Windows.ps1
```

Static/build verification cannot prove injection stability. Repeated attach/detach and DBXV2 survival remain mandatory in-game acceptance tests.
