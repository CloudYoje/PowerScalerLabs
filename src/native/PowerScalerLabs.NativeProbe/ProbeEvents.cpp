#include "ProbeEvents.h"

namespace psl::probe
{
    bool TryCommitEvent(ProbeSharedRegion& region, HANDLE event_ready, const RawProbeEvent& event) noexcept
    {
        ProbeSharedHeader& header = region.header;
        std::uint64_t reserved = 0;
        for (;;)
        {
            const auto write = static_cast<std::uint64_t>(InterlockedCompareExchange64(
                reinterpret_cast<volatile LONG64*>(&header.event_write_sequence), 0, 0));
            const auto read = static_cast<std::uint64_t>(InterlockedCompareExchange64(
                reinterpret_cast<volatile LONG64*>(&header.event_read_sequence), 0, 0));
            if (write - read >= kEventCapacity)
            {
                InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&header.dropped_event_count));
                return false;
            }
            if (InterlockedCompareExchange64(
                    reinterpret_cast<volatile LONG64*>(&header.event_write_sequence),
                    static_cast<LONG64>(write + 1),
                    static_cast<LONG64>(write)) == static_cast<LONG64>(write))
            {
                reserved = write + 1;
                break;
            }
        }

        RawProbeEvent payload = event;
        payload.commit_sequence = 0;
        payload.sequence = reserved;
        RawProbeEvent& slot = region.events[(reserved - 1) % kEventCapacity];
        slot = payload;
        MemoryBarrier();
        InterlockedExchange64(
            reinterpret_cast<volatile LONG64*>(&slot.commit_sequence),
            static_cast<LONG64>(reserved));
        if (event_ready != nullptr)
        {
            SetEvent(event_ready);
        }
        return true;
    }
}
