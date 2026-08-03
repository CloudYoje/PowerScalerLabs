# Native Causal Trace Transport Gate Audit

## Boundary

This checkpoint implements lifecycle hardening and synthetic native-event transport only. Runtime Protocol remains 8; Probe Protocol and Native ABI are 2. It adds no gameplay writes, executable hooks, hardware watchpoints, exception handlers, debug-register manipulation, thread suspension, or single-step handling.

## Architecture

The App sends a correlated command envelope to ProbeHost. ProbeHost forwards `EmitSyntheticEvent` through the ABI-2 native mailbox. The NativeProbe worker stamps events with native QPC, worker thread ID, logical sequence, trace session, and watch ID. Producers reserve ring capacity with CAS, copy the payload, publish the commit sequence with a release barrier, and signal `EventReady`. An independent ProbeHost consumer drains only the next committed sequence, verifies commit before and after copying, advances the read sequence, and forwards an event envelope to the App.

## Failure Behavior

An injected session remains private until handshake and heartbeat succeed. Failed candidates are synchronously asked to shut down and unload. A PID is quarantined when module removal cannot be proven. App disconnect triggers immediate host detach. App close sends correlated shutdown and waits for bounded unload confirmation; timeout is reported without claiming cleanup.

## Verification

Managed self-tests assert protocol, dimensions, and mailbox offsets. Native compile-time assertions fix header and event offsets. The source verifier requires the ring producer, consumer, envelope, correlated commands, synthetic controls, and bounded shutdown, and rejects all prohibited pre-watchpoint tokens.

Live DBXV2 validation remains required for 25-event delivery, two ring wraparounds, deliberate overflow recovery, heartbeat continuity, confirmed detach, reattach, and final detach. Offline tests and builds do not establish live-game stability or any HP writer address.
