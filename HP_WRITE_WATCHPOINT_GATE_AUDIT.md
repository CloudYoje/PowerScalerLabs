# HP Write Watchpoint Gate Audit

## Target and Boundary

This gate observes one selected live fighter generation at `Battle_Mob + 0x100`, the existing `RuntimeProtocol.CurrentHealthOffset`. It configures a four-byte write-only x64 hardware data breakpoint in DR0. It does not write HP, damage, resources, stats, code, animation, skill, or HUD data. Runtime Protocol remains 8, Probe Protocol remains 2, and Native ABI remains 2.

## Debug-Register Design

`WatchpointManager` enumerates DBXV2 threads with Toolhelp, excludes the NativeProbe worker, and instruments every eligible thread using suspend/get-context/set-context/resume with structured resume cleanup. DR7 enables local DR0 with RW0=write and LEN0=four bytes while preserving unrelated control bits. Any pre-existing local or global DR0 enable fails closed. Original DR0-DR3, DR6, and DR7 are retained per thread.

Arming is transactional. A failure rolls back every previously modified thread and removes the VEH. Reconciliation runs on the worker cadence; a new thread that cannot be instrumented causes complete disarm, an `InstrumentationFault` event, and a faulted native state. Disarm refuses to overwrite external mutations to DR0-DR3 or non-owned DR7 fields.

## Exception Ownership

`ExceptionTracer` installs a vectored exception handler from worker-controlled command handling, never from `DllMain`. It handles only `EXCEPTION_SINGLE_STEP` with the owned DR0 status bit in DR6. The handler captures QPC, thread ID, TrapRip, RSP, RFLAGS, ordered GPRs, original DR6/DR7, and watch metadata into the existing allocation-free ring. It clears only the owned DR6 bit and continues execution. Unrelated exceptions continue searching.

Register ordering is RAX, RBX, RCX, RDX, RSI, RDI, RBP, R8, R9, R10, R11, R12, R13, R14, R15, reserved.

## Lifecycle and Identity

The App binds a trace to actor address, slot, slot generation, battle instance, and identity key. Release or replacement of that generation immediately requests disarm. A bounded fighter-lifetime ledger correlates captured GPR values at event QPC without assigning attacker or victim semantics.

Native shutdown and heartbeat loss disarm before unload or Inert state. `SafeToUnload` remains reachable only after the worker exits with no active watch and the VEH removed. Dead-process cleanup uses the established one-owner path and does not make remote calls.

## Host Evidence

ProbeHost normalizes TrapRip to containing module plus RVA and attempts a validated 48-byte code-context read spanning 32 bytes before and 16 bytes after TrapRip. Read failure is nonfatal. TrapRip is intentionally not called the writer instruction.

## Live Procedure and Limitations

In a simple 1v1 Training battle: verify synthetic transport, select the defender generation, arm, perform one basic light hit, disarm, and preserve App, Runtime, and ProbeHost logs. Zero, one, or multiple traps are valid observations. A first trap is `Observed`, not a proven writer or damage resolver. Exact instruction decoding, stack walking, attacker/victim semantics, XMM capture, damage categorization, and gameplay modification are explicitly deferred.
