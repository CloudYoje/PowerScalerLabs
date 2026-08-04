#pragma once

#include <Windows.h>

#include <array>
#include <cstdint>

#include "ExceptionTracer.h"

namespace psl::probe
{
    inline constexpr std::uint64_t kDr0SlotEnableMask = 0x0000000000000003ULL;
    inline constexpr std::uint64_t kOwnedDr7Mask = 0x00000000000F0001ULL;
    inline constexpr std::uint64_t kDr0FourByteWrite = 0x00000000000D0001ULL;
    constexpr std::uint64_t BuildDr0WriteControl(const std::uint64_t original) noexcept
    {
        return (original & ~kOwnedDr7Mask) | kDr0FourByteWrite;
    }
    constexpr std::uint64_t RestoreDr0Control(const std::uint64_t current, const std::uint64_t original) noexcept
    {
        return (current & ~kOwnedDr7Mask) | (original & kOwnedDr7Mask);
    }

    struct ThreadDebugState
    {
        DWORD thread_id = 0;
        DWORD64 dr0 = 0;
        DWORD64 dr1 = 0;
        DWORD64 dr2 = 0;
        DWORD64 dr3 = 0;
        DWORD64 dr6 = 0;
        DWORD64 dr7 = 0;
        DWORD64 armed_dr7 = 0;
    };

    class WatchpointManager final
    {
    public:
        bool Arm(SharedMemoryContext& shared_memory, DWORD excluded_thread_id, std::uint64_t trace_session_id,
            std::uint64_t watch_id, std::uint64_t address, std::uint32_t width, std::uint32_t access_type,
            std::uint32_t simd_register_0, std::uint32_t simd_register_1, std::uint32_t& result_code) noexcept;
        bool Disarm(std::uint32_t& result_code) noexcept;
        bool Reconcile(std::uint32_t& result_code) noexcept;
        bool IsArmed() const noexcept;
        std::uint32_t InstrumentedThreadCount() const noexcept;
        std::uint32_t EligibleThreadCount() const noexcept;
        std::uint32_t ExitedThreadCount() const noexcept;
        std::uint32_t NewlyArmedThreadCount() const noexcept;
        std::uint32_t ConflictThreadCount() const noexcept;
        DWORD FailureThreadId() const noexcept;
        std::uint32_t ConflictComponent() const noexcept;
        std::uint64_t ExpectedOwnedValue() const noexcept;
        std::uint64_t ObservedOwnedValue() const noexcept;
        std::uint32_t NonOwnedChangeFlags() const noexcept;
        DWORD NonOwnedChangeThreadId() const noexcept;
        std::uint64_t TraceSessionId() const noexcept;
        std::uint64_t WatchId() const noexcept;
        std::uint64_t TargetAddress() const noexcept;

    private:
        static constexpr std::size_t kMaximumThreads = 1024;
        bool DiscoverThreads(std::array<DWORD, kMaximumThreads>& thread_ids, std::size_t& count) const noexcept;
        bool InstrumentThread(DWORD thread_id, ThreadDebugState& state, std::uint32_t& result_code) noexcept;
        bool RestoreThread(const ThreadDebugState& state, std::uint32_t& result_code) noexcept;
        bool ValidateThread(const ThreadDebugState& state, bool& exited, std::uint32_t& result_code) noexcept;
        bool ContainsThread(DWORD thread_id) const noexcept;

        ExceptionTracer tracer_{};
        std::array<ThreadDebugState, kMaximumThreads> states_{};
        std::size_t state_count_ = 0;
        DWORD excluded_thread_id_ = 0;
        std::uint64_t target_address_ = 0;
        std::uint64_t trace_session_id_ = 0;
        std::uint64_t watch_id_ = 0;
        bool armed_ = false;
        DWORD failure_thread_id_ = 0;
        std::uint32_t eligible_thread_count_ = 0;
        std::uint32_t exited_thread_count_ = 0;
        std::uint32_t newly_armed_thread_count_ = 0;
        std::uint32_t conflict_thread_count_ = 0;
        std::uint32_t conflict_component_ = 0;
        std::uint64_t expected_owned_value_ = 0;
        std::uint64_t observed_owned_value_ = 0;
        std::uint32_t non_owned_change_flags_ = 0;
        DWORD non_owned_change_thread_id_ = 0;
    };
}
