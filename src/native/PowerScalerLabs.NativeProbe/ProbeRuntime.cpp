#include "ProbeRuntime.h"

#include <cstring>

#include "ProbeSharedMemory.h"
#include "ProbeWorker.h"

namespace
{
    HMODULE g_module = nullptr;
    volatile LONG g_initialized = 0;
    psl::probe::SharedMemoryContext g_shared_memory{};
    HANDLE g_worker = nullptr;
}

namespace psl::probe
{
    void SetProbeModule(HMODULE module) noexcept
    {
        g_module = module;
    }

    DWORD InitializeProbe(void* parameter) noexcept
    {
        if (parameter == nullptr || g_module == nullptr ||
            InterlockedCompareExchange(&g_initialized, 1, 0) != 0)
        {
            return 0;
        }

        ProbeInitializationArguments arguments{};
        std::memcpy(&arguments, parameter, sizeof(arguments));
        if (arguments.structure_magic != kInitializationMagic ||
            arguments.abi_version != kAbiVersion ||
            arguments.structure_size != sizeof(ProbeInitializationArguments) ||
            arguments.game_process_id != GetCurrentProcessId())
        {
            return 0;
        }

        if (!OpenSharedMemory(arguments, g_shared_memory))
        {
            return 0;
        }
        g_shared_memory.region->header.state = static_cast<std::uint32_t>(NativeState::Initializing);
        if (!ValidateSharedMemory(arguments, g_shared_memory))
        {
            g_shared_memory.region->header.initialization_status = 2;
            g_shared_memory.region->header.state = static_cast<std::uint32_t>(NativeState::Faulted);
            CloseSharedMemory(g_shared_memory);
            return 0;
        }

        g_worker = CreateThread(nullptr, 0, ProbeWorkerMain, &g_shared_memory, 0, nullptr);
        if (g_worker == nullptr)
        {
            g_shared_memory.region->header.initialization_status = 3;
            g_shared_memory.region->header.state = static_cast<std::uint32_t>(NativeState::Faulted);
            CloseSharedMemory(g_shared_memory);
            return 0;
        }
        g_shared_memory.region->header.initialization_status = 1;
        return 1;
    }

    DWORD PrepareProbeUnload() noexcept
    {
        if (g_worker == nullptr || g_shared_memory.region == nullptr)
        {
            return 0;
        }
        if (WaitForSingleObject(g_worker, 10000) != WAIT_OBJECT_0)
        {
            return 0;
        }
        CloseHandle(g_worker);
        g_worker = nullptr;
        g_shared_memory.region->header.state = static_cast<std::uint32_t>(NativeState::SafeToUnload);
        CloseSharedMemory(g_shared_memory);
        return 1;
    }
}
