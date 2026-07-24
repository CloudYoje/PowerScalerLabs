# Retained Guided Overlay - Compatibility Note

The existing Guided Test Overlay remains in this source gate only so the current scanner workflow can still issue recording, baseline, comparison, snapshot, repeat, and save commands.

It is a separate WPF window, not a true in-game overlay. It can take foreground focus and cause Xenoverse to pause. That behavior is acknowledged and is not accepted as the final overlay design.

This gate does not alter its rendering architecture because scanner decluttering and chronology must pass a separate deep audit before native DX11 rendering is introduced. Combining candidate migration, session-schema changes, video capture, graphics hooking, and input interception in one build would make regressions difficult to attribute.

The retained overlay still provides visible menu selection, mouse controls, Up/Down navigation, Left/Right panel switching, Enter confirmation, Escape/Backspace cancellation, and F11 show/hide. It communicates with the external read-only runtime through the established app command path and does not access game memory directly.

The later native-overlay gate must satisfy these additional requirements:

- render inside the game frame path;
- never pause the game by stealing desktop focus;
- keep the overlay component thin;
- relay commands/status through IPC;
- preserve the external canonical scanner and recorder;
- remain isolated from the frozen HealthScaler;
- pass frame-time, resize, fullscreen/borderless, input, reconnect, and long-session audits.
