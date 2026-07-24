# Retained Guided Overlay - Temporary Test Procedure

This WPF overlay is retained only as a temporary experiment controller for the Scanner Declutter + Chronology Windows Gate. It is not the approved final in-game overlay.

## Known limitation

Clicking or activating the WPF window can move foreground focus away from Xenoverse and pause the game. Do not treat continued gameplay while this window is interactive as a pass criterion. The native DX11 overlay will be tested in a later bounded gate.

## Controls still available

- Mouse: select categories, tests, and action buttons.
- Up/Down: navigate the focused list.
- Left/Right: switch Categories and Tests.
- Enter: confirm or run the next valid scanner step.
- Escape/Backspace: go back or cancel.
- F11: show/hide only.

## Scanner test use

1. Select a test.
2. Start recording.
3. Capture a baseline.
4. Wait for Pending to reach zero.
5. Return to Xenoverse and perform the action.
6. Return to the overlay and compare.
7. Wait for Pending to reach zero.
8. Repeat or Stop & Save.

The acceptance criteria for this gate are candidate grouping, chronology, persistence, scanner stability, and zero dropped observations. Overlay focus behavior remains a known defect scheduled for the native-overlay gate.
