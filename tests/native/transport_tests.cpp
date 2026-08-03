#include "../../src/native/PowerScalerLabs.NativeProbe/ProbeEvents.h"

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
}

int main()
{
    try
    {
        test_concurrent_wraparound_and_uniqueness();
        test_hole_overflow_and_reset();
        std::cout << "Native transport tests passed: MPSC uniqueness, ordered holes, wraparound, overflow/drop, reset/reuse.\n";
        return 0;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "Native transport tests failed: " << exception.what() << '\n';
        return 1;
    }
}
