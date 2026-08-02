#include "ProbeSharedMemory.h"

namespace psl::probe
{
    bool OpenSharedMemory(const ProbeInitializationArguments& arguments, SharedMemoryContext& context) noexcept
    {
        context.mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, arguments.mapping_name);
        if (context.mapping == nullptr)
        {
            return false;
        }
        context.region = static_cast<ProbeSharedRegion*>(MapViewOfFile(
            context.mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(ProbeSharedRegion)));
        context.command_event = OpenEventW(SYNCHRONIZE, FALSE, arguments.command_event_name);
        context.event_ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, arguments.event_ready_name);
        if (context.region == nullptr || context.command_event == nullptr || context.event_ready == nullptr)
        {
            CloseSharedMemory(context);
            return false;
        }
        return true;
    }

    bool ValidateSharedMemory(const ProbeInitializationArguments& arguments, const SharedMemoryContext& context) noexcept
    {
        if (context.region == nullptr)
        {
            return false;
        }
        const ProbeSharedHeader& header = context.region->header;
        LARGE_INTEGER frequency{};
        return QueryPerformanceFrequency(&frequency) != FALSE &&
            header.magic == kSharedMagic &&
            header.abi_version == kAbiVersion &&
            header.header_size == sizeof(ProbeSharedHeader) &&
            header.event_size == sizeof(RawProbeEvent) &&
            header.capacity == kEventCapacity &&
            header.host_process_id == arguments.host_process_id &&
            header.game_process_id == arguments.game_process_id &&
            header.game_process_id == GetCurrentProcessId() &&
            header.nonce_low == arguments.nonce_low &&
            header.nonce_high == arguments.nonce_high &&
            header.qpc_frequency == static_cast<std::uint64_t>(frequency.QuadPart);
    }

    void CloseSharedMemory(SharedMemoryContext& context) noexcept
    {
        if (context.region != nullptr)
        {
            UnmapViewOfFile(context.region);
            context.region = nullptr;
        }
        if (context.event_ready != nullptr)
        {
            CloseHandle(context.event_ready);
            context.event_ready = nullptr;
        }
        if (context.command_event != nullptr)
        {
            CloseHandle(context.command_event);
            context.command_event = nullptr;
        }
        if (context.mapping != nullptr)
        {
            CloseHandle(context.mapping);
            context.mapping = nullptr;
        }
    }
}
