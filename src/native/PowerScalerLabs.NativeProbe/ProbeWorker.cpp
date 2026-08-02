#include "ProbeWorker.h"

namespace
{
    void WriteState(psl::probe::ProbeSharedHeader& header, psl::probe::NativeState state) noexcept
    {
        InterlockedExchange(
            reinterpret_cast<volatile LONG*>(&header.state),
            static_cast<LONG>(state));
    }

    void WriteCounter(std::uint64_t& target, std::uint64_t value) noexcept
    {
        InterlockedExchange64(
            reinterpret_cast<volatile LONG64*>(&target),
            static_cast<LONG64>(value));
    }
}

namespace psl::probe
{
    DWORD WINAPI ProbeWorkerMain(void* parameter) noexcept
    {
        auto* context = static_cast<SharedMemoryContext*>(parameter);
        if (context == nullptr || context->region == nullptr)
        {
            return 1;
        }

        ProbeSharedHeader& header = context->region->header;
        WriteState(header, NativeState::Ready);
        std::uint64_t heartbeat_sequence = 0;
        std::uint64_t last_command_sequence = header.command_ack_sequence;
        LARGE_INTEGER now{};

        for (;;)
        {
            QueryPerformanceCounter(&now);
            WriteCounter(header.probe_heartbeat_qpc, static_cast<std::uint64_t>(now.QuadPart));
            WriteCounter(header.probe_heartbeat_sequence, ++heartbeat_sequence);

            const std::uint64_t host_heartbeat = header.host_heartbeat_qpc;
            const std::uint64_t stale_limit = header.qpc_frequency * 5;
            if (host_heartbeat == 0 ||
                static_cast<std::uint64_t>(now.QuadPart) > host_heartbeat + stale_limit)
            {
                WriteState(header, NativeState::Inert);
            }
            else if (header.state == static_cast<std::uint32_t>(NativeState::Inert))
            {
                WriteState(header, NativeState::Ready);
            }

            const std::uint64_t command_sequence = header.command_sequence;
            if (command_sequence != last_command_sequence)
            {
                last_command_sequence = command_sequence;
                if (header.command == static_cast<std::uint32_t>(NativeCommand::Shutdown))
                {
                    WriteState(header, NativeState::ShuttingDown);
                    header.active_watchpoint_count = 0;
                    WriteCounter(header.command_ack_sequence, command_sequence);
                    return 0;
                }
                WriteCounter(header.command_ack_sequence, command_sequence);
            }

            WaitForSingleObject(context->command_event, 250);
        }
    }
}
