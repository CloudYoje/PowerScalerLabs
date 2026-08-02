#pragma once

#include <Windows.h>

namespace psl::probe
{
    void SetProbeModule(HMODULE module) noexcept;
    DWORD InitializeProbe(void* parameter) noexcept;
    DWORD PrepareProbeUnload() noexcept;
}
