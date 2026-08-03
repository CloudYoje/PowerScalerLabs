#include "ProbeWorker.h"

#include "ProbeEvents.h"

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

    void UpdateHeartbeat(psl::probe::ProbeSharedHeader& header, std::uint64_t& sequence) noexcept
    {
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        WriteCounter(header.probe_heartbeat_qpc, static_cast<std::uint64_t>(now.QuadPart));
        WriteCounter(header.probe_heartbeat_sequence, ++sequence);
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

        for (;;)
        {
            UpdateHeartbeat(header, heartbeat_sequence);

            const std::uint64_t host_heartbeat = header.host_heartbeat_qpc;
            const std::uint64_t stale_limit = header.qpc_frequency * 5;
            LARGE_INTEGER now{};
            QueryPerformanceCounter(&now);
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
                if (header.command == static_cast<std::uint32_t>(NativeCommand::EmitSyntheticEvent))
                {
                    const std::uint32_t count = header.command_event_count;
                    const std::uint32_t interval = header.command_interval_milliseconds;
                    std::uint32_t generated = 0;
                    if (count == 0 || count > 10000 || interval > 1000)
                    {
                        header.command_result_code = 1;
                    }
                    else
                    {
                        for (std::uint32_t index = 0; index < count; ++index)
                        {
                            LARGE_INTEGER event_qpc{};
                            QueryPerformanceCounter(&event_qpc);
                            RawProbeEvent event{};
                            event.qpc = static_cast<std::uint64_t>(event_qpc.QuadPart);
                            event.trace_session_id = header.command_trace_session_id;
                            event.watch_id = header.command_watch_id;
                            event.thread_id = GetCurrentThreadId();
                            event.event_type = static_cast<std::uint32_t>(NativeEventType::Synthetic);
                            TryCommitEvent(*context->region, context->event_ready, event);
                            ++generated;
                            UpdateHeartbeat(header, heartbeat_sequence);
                            if (interval != 0 && index + 1 < count)
                            {
                                Sleep(interval);
                            }
                        }
                        header.command_result_code = 0;
                    }
                    header.command_generated_event_count = generated;
                    WriteCounter(header.command_ack_sequence, command_sequence);
                    continue;
                }
                header.command_result_code = 2;
                WriteCounter(header.command_ack_sequence, command_sequence);
            }

            WaitForSingleObject(context->command_event, 250);
        }
    }
}
