#include "../../src/native/PowerScalerLabs.NativeProbe/ProbeEvents.h"
#include "../../src/native/PowerScalerLabs.NativeProbe/WatchpointManager.h"

#include <atomic>
#include <cstring>
#include <iostream>
#include <thread>
#include <unordered_set>
#include <vector>

using namespace psl::probe;

namespace
{
    void require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    bool consume_next(ProbeSharedRegion& region, std::vector<std::uint64_t>& sequences)
    {
        const std::uint64_t expected = region.header.event_read_sequence + 1;
        RawProbeEvent& slot = region.events[(expected - 1) % kEventCapacity];
        if (slot.commit_sequence != expected) return false;
        const RawProbeEvent copy = slot;
        MemoryBarrier();
        if (slot.commit_sequence != expected || copy.sequence != expected) return false;
        sequences.push_back(copy.sequence);
        region.header.event_read_sequence = expected;
        return true;
    }

    void test_concurrent_wraparound_and_uniqueness()
    {
        ProbeSharedRegion region{};
        constexpr int producer_count = 4;
        constexpr int events_per_producer = 256;
        constexpr int total = producer_count * events_per_producer;
        std::atomic<int> producers_done = 0;
        std::vector<std::uint64_t> consumed;
        consumed.reserve(total);
        std::thread consumer([&]
        {
            while (static_cast<int>(consumed.size()) < total || producers_done.load() != producer_count)
            {
                if (!consume_next(region, consumed)) std::this_thread::yield();
            }
        });
        std::vector<std::thread> producers;
        for (int producer = 0; producer < producer_count; ++producer)
        {
            producers.emplace_back([&, producer]
            {
                for (int index = 0; index < events_per_producer; ++index)
                {
                    RawProbeEvent event{};
                    event.trace_session_id = static_cast<std::uint64_t>(producer + 1);
                    while (!TryCommitEvent(region, nullptr, event)) std::this_thread::yield();
                }
                ++producers_done;
            });
        }
        for (auto& producer : producers) producer.join();
        consumer.join();
        require(consumed.size() == total, "concurrent delivery count mismatch");
        require(std::unordered_set<std::uint64_t>(consumed.begin(), consumed.end()).size() == total, "duplicate logical sequence");
        for (int index = 0; index < total; ++index) require(consumed[index] == static_cast<std::uint64_t>(index + 1), "out-of-order consumption");
    }

    void test_hole_overflow_and_reset()
    {
        ProbeSharedRegion region{};
        region.header.event_write_sequence = 2;
        region.events[1].sequence = 2;
        region.events[1].commit_sequence = 2;
        std::vector<std::uint64_t> consumed;
        require(!consume_next(region, consumed), "consumer skipped an uncommitted hole");
        region.events[0].sequence = 1;
        region.events[0].commit_sequence = 1;
        require(consume_next(region, consumed) && consume_next(region, consumed), "committed hole did not resume in order");

        std::memset(&region, 0, sizeof(region));
        RawProbeEvent event{};
        for (std::uint32_t index = 0; index < kEventCapacity; ++index) require(TryCommitEvent(region, nullptr, event), "ring filled early");
        require(!TryCommitEvent(region, nullptr, event), "overflow did not remain nonblocking");
        require(region.header.dropped_event_count == 1, "drop count mismatch");
        consumed.clear();
        while (consume_next(region, consumed)) { }
        require(TryCommitEvent(region, nullptr, event), "ring could not be reused after drain");
    }

    void test_watchpoint_abi_and_dr7_control()
    {
        constexpr std::uint64_t unrelated = 0x0000000000A00000ULL;
        constexpr std::uint64_t configured = BuildDr0WriteControl(unrelated);
        require((configured & 1ULL) != 0, "DR0 local enable is missing");
        require(((configured >> 16) & 3ULL) == 1ULL, "DR0 access is not write-only");
        require(((configured >> 18) & 3ULL) == 3ULL, "DR0 length is not four bytes");
        require((configured & ~kOwnedDr7Mask) == unrelated, "DR7 unrelated state was not preserved");
        constexpr std::uint64_t externally_changed = configured | 0x0000000000100000ULL | 0x0000000000000004ULL;
        constexpr std::uint64_t restored = RestoreDr0Control(externally_changed, unrelated);
        require((restored & kOwnedDr7Mask) == (unrelated & kOwnedDr7Mask), "owned DR0 control was not restored");
        require((restored & ~kOwnedDr7Mask) == (externally_changed & ~kOwnedDr7Mask),
            "non-owned DR7 state was overwritten during restore");
        require((kOwnedDr7Mask & 2ULL) == 0, "PowerScaler must not claim DR0 global enable ownership");
        RawProbeEvent event{};
        event.event_type = static_cast<std::uint32_t>(NativeEventType::HardwareWriteTrap);
        event.access_type = static_cast<std::uint32_t>(NativeAccessType::Write);
        event.access_width = 4;
        event.registers[2] = 0x1111;
        event.registers[3] = 0x2222;
        event.simd_register_0 = 0;
        event.simd_register_1 = 6;
        event.simd_scalar_bits_0 = 0x3F800000;
        event.simd_scalar_bits_1 = 0x40000000;
        require(sizeof(event) == kEventSize && event.access_width == 4 && event.registers[2] == 0x1111 &&
            event.registers[3] == 0x2222, "hardware trap ABI fixture mismatch");
        require(offsetof(RawProbeEvent, simd_register_0) == 240 && event.simd_register_1 == 6 &&
            event.simd_scalar_bits_0 == 0x3F800000 && event.simd_scalar_bits_1 == 0x40000000,
            "selected SIMD evidence ABI fixture mismatch");
    }
}

int main()
{
    try
    {
        test_concurrent_wraparound_and_uniqueness();
        test_hole_overflow_and_reset();
        test_watchpoint_abi_and_dr7_control();
        std::cout << "Native tests passed: transport, DR7 construction, and hardware-trap ABI fields.\n";
        return 0;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "Native transport tests failed: " << exception.what() << '\n';
        return 1;
    }
}
