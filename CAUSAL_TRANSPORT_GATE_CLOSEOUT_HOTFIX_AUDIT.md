# Causal Transport Gate Closeout Hotfix Audit

## Scope

This hotfix changes lifecycle and synthetic transport accounting only. Runtime Protocol remains 8, Probe Protocol remains 2, and Native ABI remains 2. No gameplay write, executable hook, hardware watchpoint, exception handler, debug-register manipulation, or thread suspension capability was added.

## WPF Shutdown

`MainWindow` now uses an explicit one-shot shutdown state machine. The first `Closing` event is canceled once and starts bounded correlated ProbeHost cleanup followed by Runtime shutdown and pipe/process disposal. The final close is posted with `Dispatcher.BeginInvoke`, after the original callback has unwound. Repeated close callbacks cannot start cleanup again.

## Dead Game Process

ProbeHost atomically claims dead-process cleanup and serializes it through the lifecycle semaphore shared with detach. A dead target is never asked to execute remote unload calls. The event consumer is stopped, IPC and process handles are disposed, and the session is cleared exactly once. `ProbeInjectionSession.Dispose` is idempotent.

## Overflow Settlement

Each synthetic run uses a unique native trace session. After native acknowledgement, the App waits until received events plus the native drop delta account for the acknowledged count and remain quiet for 500 ms, or until a bounded timeout. Reports distinguish generated, received, dropped, unaccounted, and settlement state. Overflow automatically runs a separate one-event recovery probe.

## Live Validation

The prior transport checkpoint was live-proven. This closeout build still requires the short App-close, game-close, and overflow-accounting sanity tests described in the implementation brief. Repository tests cannot reproduce WPF shutdown timing or DBXV2 process teardown.
