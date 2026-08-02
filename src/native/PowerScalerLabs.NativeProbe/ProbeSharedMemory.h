#pragma once

#include <Windows.h>

#include "PowerScalerProbeAbi.h"

namespace psl::probe
{
    struct SharedMemoryContext
    {
        HANDLE mapping = nullptr;
        HANDLE command_event = nullptr;
        HANDLE event_ready = nullptr;
        ProbeSharedRegion* region = nullptr;
    };

    bool OpenSharedMemory(const ProbeInitializationArguments& arguments, SharedMemoryContext& context) noexcept;
    bool ValidateSharedMemory(const ProbeInitializationArguments& arguments, const SharedMemoryContext& context) noexcept;
    void CloseSharedMemory(SharedMemoryContext& context) noexcept;
}
