#pragma once

#include <windows.h>

namespace hs {
struct HealthRuntimeStatus {
    bool running = false;
    bool writeHealthEnabled = false;
};

// Runs the final fighter lifecycle, transformation-safe percentage correction,
// and target ownership needed by the health HUD transition bridge.
DWORD RunHealthOverhaulRuntime();

[[nodiscard]] HealthRuntimeStatus SnapshotHealthRuntimeStatus() noexcept;
}
