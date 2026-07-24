# Windows PowerShell compatibility correction

The first Guided Overlay Gate package contained UTF-8 arrow characters inside a no-BOM PowerShell verifier. Windows PowerShell could decode those bytes through the active ANSI code page, causing the verifier parser to treat a byte from an arrow as quotation syntax.

Build 2 corrects this by:

- keeping PowerShell script source ASCII-only;
- saving PowerShell scripts with a UTF-8 BOM;
- reading inspected source files with strict UTF-8 APIs;
- checking ASCII portions of the overlay help text instead of Unicode glyphs.

No scanner, HealthScaler, game-memory, telemetry, or candidate-persistence behavior was changed by this compatibility correction.
