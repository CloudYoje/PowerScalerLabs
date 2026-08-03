#pragma once

#include <Windows.h>

#include <array>
#include <cstdint>

#include "ExceptionTracer.h"

namespace psl::probe
{
    inline constexpr std::uint64_t kDr0ControlMask = 0x00000000000F0003ULL;
    inline constexpr std::uint64_t kDr0FourByteWrite = 0x00000000000D0001ULL;
    constexpr std::uint64_t BuildDr0WriteControl(const std::uint64_t original) noexcept
    {
        return (original & ~kDr0ControlMask) | kDr0FourByteWrite;
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
            std::uint32_t& result_code) noexcept;
        bool Disarm(std::uint32_t& result_code) noexcept;
        bool Reconcile(std::uint32_t& result_code) noexcept;
        bool IsArmed() const noexcept;
        std::uint32_t InstrumentedThreadCount() const noexcept;
        DWORD FailureThreadId() const noexcept;
        std::uint64_t TraceSessionId() const noexcept;
        std::uint64_t WatchId() const noexcept;
        std::uint64_t TargetAddress() const noexcept;

    private:
        static constexpr std::size_t kMaximumThreads = 1024;
        bool DiscoverThreads(std::array<DWORD, kMaximumThreads>& thread_ids, std::size_t& count) const noexcept;
        bool InstrumentThread(DWORD thread_id, ThreadDebugState& state, std::uint32_t& result_code) noexcept;
        bool RestoreThread(const ThreadDebugState& state, std::uint32_t& result_code) noexcept;
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
    };
}
