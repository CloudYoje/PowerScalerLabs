#include "WatchpointManager.h"

#include <TlHelp32.h>

namespace
{
    class SuspendedThread final
    {
    public:
        explicit SuspendedThread(const DWORD thread_id) noexcept
            : handle_(OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION,
                FALSE, thread_id))
        {
            if (handle_ == nullptr) error_ = GetLastError();
            else if (SuspendThread(handle_) != static_cast<DWORD>(-1)) suspended_ = true;
            else error_ = GetLastError();
        }
        ~SuspendedThread()
        {
            if (suspended_) ResumeThread(handle_);
            if (handle_ != nullptr) CloseHandle(handle_);
        }
        bool IsReady() const noexcept { return handle_ != nullptr && suspended_; }
        HANDLE Get() const noexcept { return handle_; }
        DWORD Error() const noexcept { return error_; }

    private:
        HANDLE handle_ = nullptr;
        bool suspended_ = false;
        DWORD error_ = ERROR_SUCCESS;
    };
}

namespace psl::probe
{
    bool WatchpointManager::Arm(SharedMemoryContext& shared_memory, const DWORD excluded_thread_id,
        const std::uint64_t trace_session_id, const std::uint64_t watch_id, const std::uint64_t address,
        const std::uint32_t width, const std::uint32_t access_type, const std::uint32_t simd_register_0,
        const std::uint32_t simd_register_1, std::uint32_t& result_code) noexcept
    {
        result_code = 0;
        failure_thread_id_ = 0;
        eligible_thread_count_ = 0;
        exited_thread_count_ = 0;
        newly_armed_thread_count_ = 0;
        conflict_thread_count_ = 0;
        conflict_component_ = 0;
        expected_owned_value_ = 0;
        observed_owned_value_ = 0;
        non_owned_change_flags_ = 0;
        non_owned_change_thread_id_ = 0;
        if (armed_ || address == 0 || width != 4 || access_type != static_cast<std::uint32_t>(NativeAccessType::Write) ||
            simd_register_0 >= 16 || simd_register_1 >= 16 || simd_register_0 == simd_register_1)
        {
            result_code = 10;
            return false;
        }
        if (!tracer_.Install(shared_memory))
        {
            result_code = 11;
            return false;
        }

        excluded_thread_id_ = excluded_thread_id;
        target_address_ = address;
        trace_session_id_ = trace_session_id;
        watch_id_ = watch_id;
        std::array<DWORD, kMaximumThreads> thread_ids{};
        std::size_t thread_count = 0;
        if (!DiscoverThreads(thread_ids, thread_count) || thread_count == 0)
        {
            result_code = 12;
            tracer_.Remove();
            return false;
        }
        eligible_thread_count_ = static_cast<std::uint32_t>(thread_count);

        for (std::size_t index = 0; index < thread_count; ++index)
        {
            ThreadDebugState state{};
            if (!InstrumentThread(thread_ids[index], state, result_code))
            {
                std::uint32_t rollback_result = 0;
                while (state_count_ > 0) RestoreThread(states_[--state_count_], rollback_result);
                tracer_.Remove();
                target_address_ = 0;
                if (rollback_result != 0) result_code = 19;
                return false;
            }
            if (state.thread_id != 0) states_[state_count_++] = state;
        }

        if (state_count_ == 0) { result_code = 12; tracer_.Remove(); return false; }

        tracer_.Activate(trace_session_id, watch_id, address, simd_register_0, simd_register_1);
        armed_ = true;
        return true;
    }

    bool WatchpointManager::Disarm(std::uint32_t& result_code) noexcept
    {
        result_code = 0;
        tracer_.Deactivate();
        bool restored = true;
        while (state_count_ > 0)
        {
            std::uint32_t thread_result = 0;
            if (!RestoreThread(states_[--state_count_], thread_result))
            {
                restored = false;
                result_code = thread_result;
            }
        }
        if (!restored)
        {
            return false;
        }
        if (!tracer_.Remove())
        {
            result_code = 18;
            return false;
        }
        armed_ = false;
        target_address_ = 0;
        trace_session_id_ = 0;
        watch_id_ = 0;
        return true;
    }

    bool WatchpointManager::Reconcile(std::uint32_t& result_code) noexcept
    {
        result_code = 0;
        if (!armed_) return true;
        std::array<DWORD, kMaximumThreads> thread_ids{};
        std::size_t thread_count = 0;
        if (!DiscoverThreads(thread_ids, thread_count))
        {
            result_code = 12;
            return false;
        }
        eligible_thread_count_ = static_cast<std::uint32_t>(thread_count);
        for (std::size_t state_index = 0; state_index < state_count_;)
        {
            bool still_exists = false;
            for (std::size_t thread_index = 0; thread_index < thread_count; ++thread_index)
            {
                if (states_[state_index].thread_id == thread_ids[thread_index]) { still_exists = true; break; }
            }
            if (!still_exists)
            {
                states_[state_index] = states_[--state_count_];
                ++exited_thread_count_;
                continue;
            }
            bool exited = false;
            if (!ValidateThread(states_[state_index], exited, result_code)) return false;
            if (exited)
            {
                states_[state_index] = states_[--state_count_];
                ++exited_thread_count_;
                continue;
            }
            ++state_index;
        }
        for (std::size_t index = 0; index < thread_count; ++index)
        {
            if (ContainsThread(thread_ids[index])) continue;
            if (state_count_ >= states_.size())
            {
                result_code = 13;
                return false;
            }
            ThreadDebugState state{};
            if (!InstrumentThread(thread_ids[index], state, result_code)) return false;
            if (state.thread_id != 0)
            {
                states_[state_count_++] = state;
                ++newly_armed_thread_count_;
            }
        }
        return true;
    }

    bool WatchpointManager::DiscoverThreads(std::array<DWORD, kMaximumThreads>& thread_ids, std::size_t& count) const noexcept
    {
        count = 0;
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return false;
        THREADENTRY32 entry{};
        entry.dwSize = sizeof(entry);
        BOOL available = Thread32First(snapshot, &entry);
        while (available != FALSE)
        {
            if (entry.th32OwnerProcessID == GetCurrentProcessId() && entry.th32ThreadID != excluded_thread_id_)
            {
                if (count >= thread_ids.size()) { CloseHandle(snapshot); return false; }
                thread_ids[count++] = entry.th32ThreadID;
            }
            available = Thread32Next(snapshot, &entry);
        }
        CloseHandle(snapshot);
        return true;
    }

    bool WatchpointManager::InstrumentThread(const DWORD thread_id, ThreadDebugState& state, std::uint32_t& result_code) noexcept
    {
        SuspendedThread thread(thread_id);
        if (!thread.IsReady())
        {
            if (thread.Error() == ERROR_INVALID_PARAMETER) { state = {}; return true; }
            failure_thread_id_ = thread_id; result_code = 14; return false;
        }
        CONTEXT context{};
        context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(thread.Get(), &context) == FALSE) { failure_thread_id_ = thread_id; result_code = 15; return false; }
        if ((context.Dr7 & kDr0SlotEnableMask) != 0) { failure_thread_id_ = thread_id; result_code = 16; return false; }
        state = { thread_id, context.Dr0, context.Dr1, context.Dr2, context.Dr3, context.Dr6, context.Dr7, 0 };
        context.Dr0 = target_address_;
        context.Dr7 = BuildDr0WriteControl(context.Dr7);
        state.armed_dr7 = context.Dr7;
        if (SetThreadContext(thread.Get(), &context) == FALSE) { failure_thread_id_ = thread_id; result_code = 17; return false; }
        return true;
    }

    bool WatchpointManager::RestoreThread(const ThreadDebugState& state, std::uint32_t& result_code) noexcept
    {
        SuspendedThread thread(state.thread_id);
        if (!thread.IsReady())
        {
            if (thread.Error() == ERROR_INVALID_PARAMETER) return true;
            failure_thread_id_ = state.thread_id;
            result_code = 14;
            return false;
        }
        CONTEXT context{};
        context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(thread.Get(), &context) == FALSE) { result_code = 15; return false; }
        if (context.Dr0 != target_address_)
        {
            failure_thread_id_ = state.thread_id;
            conflict_component_ = 1;
            expected_owned_value_ = target_address_;
            observed_owned_value_ = context.Dr0;
            ++conflict_thread_count_;
            result_code = 20;
            return false;
        }
        if ((context.Dr7 & kOwnedDr7Mask) != (state.armed_dr7 & kOwnedDr7Mask))
        {
            failure_thread_id_ = state.thread_id;
            conflict_component_ = 2;
            expected_owned_value_ = state.armed_dr7 & kOwnedDr7Mask;
            observed_owned_value_ = context.Dr7 & kOwnedDr7Mask;
            ++conflict_thread_count_;
            result_code = 21;
            return false;
        }
        std::uint32_t non_owned_flags = 0;
        if (context.Dr1 != state.dr1) non_owned_flags |= 1U;
        if (context.Dr2 != state.dr2) non_owned_flags |= 2U;
        if (context.Dr3 != state.dr3) non_owned_flags |= 4U;
        if ((context.Dr7 & ~kOwnedDr7Mask) != (state.dr7 & ~kOwnedDr7Mask)) non_owned_flags |= 8U;
        if (non_owned_flags != 0)
        {
            non_owned_change_flags_ |= non_owned_flags;
            non_owned_change_thread_id_ = state.thread_id;
        }
        context.Dr0 = state.dr0;
        context.Dr7 = RestoreDr0Control(context.Dr7, state.dr7);
        if (SetThreadContext(thread.Get(), &context) == FALSE) { result_code = 17; return false; }
        return true;
    }

    bool WatchpointManager::ValidateThread(
        const ThreadDebugState& state, bool& exited, std::uint32_t& result_code) noexcept
    {
        exited = false;
        SuspendedThread thread(state.thread_id);
        if (!thread.IsReady())
        {
            if (thread.Error() == ERROR_INVALID_PARAMETER) { exited = true; return true; }
            failure_thread_id_ = state.thread_id; result_code = 14; return false;
        }
        CONTEXT context{};
        context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(thread.Get(), &context) == FALSE)
        {
            if (GetLastError() == ERROR_INVALID_PARAMETER) { exited = true; return true; }
            failure_thread_id_ = state.thread_id; result_code = 15; return false;
        }
        if (context.Dr0 != target_address_)
        {
            failure_thread_id_ = state.thread_id; conflict_component_ = 1;
            expected_owned_value_ = target_address_; observed_owned_value_ = context.Dr0;
            ++conflict_thread_count_; result_code = 20; return false;
        }
        if ((context.Dr7 & kOwnedDr7Mask) != (state.armed_dr7 & kOwnedDr7Mask))
        {
            failure_thread_id_ = state.thread_id; conflict_component_ = 2;
            expected_owned_value_ = state.armed_dr7 & kOwnedDr7Mask;
            observed_owned_value_ = context.Dr7 & kOwnedDr7Mask;
            ++conflict_thread_count_; result_code = 21; return false;
        }
        std::uint32_t non_owned_flags = 0;
        if (context.Dr1 != state.dr1) non_owned_flags |= 1U;
        if (context.Dr2 != state.dr2) non_owned_flags |= 2U;
        if (context.Dr3 != state.dr3) non_owned_flags |= 4U;
        if ((context.Dr7 & ~kOwnedDr7Mask) != (state.dr7 & ~kOwnedDr7Mask)) non_owned_flags |= 8U;
        if (non_owned_flags != 0)
        {
            non_owned_change_flags_ |= non_owned_flags;
            non_owned_change_thread_id_ = state.thread_id;
        }
        return true;
    }

    bool WatchpointManager::ContainsThread(const DWORD thread_id) const noexcept
    {
        for (std::size_t index = 0; index < state_count_; ++index)
            if (states_[index].thread_id == thread_id) return true;
        return false;
    }

    bool WatchpointManager::IsArmed() const noexcept { return armed_; }
    std::uint32_t WatchpointManager::InstrumentedThreadCount() const noexcept { return static_cast<std::uint32_t>(state_count_); }
    std::uint32_t WatchpointManager::EligibleThreadCount() const noexcept { return eligible_thread_count_; }
    std::uint32_t WatchpointManager::ExitedThreadCount() const noexcept { return exited_thread_count_; }
    std::uint32_t WatchpointManager::NewlyArmedThreadCount() const noexcept { return newly_armed_thread_count_; }
    std::uint32_t WatchpointManager::ConflictThreadCount() const noexcept { return conflict_thread_count_; }
    DWORD WatchpointManager::FailureThreadId() const noexcept { return failure_thread_id_; }
    std::uint32_t WatchpointManager::ConflictComponent() const noexcept { return conflict_component_; }
    std::uint64_t WatchpointManager::ExpectedOwnedValue() const noexcept { return expected_owned_value_; }
    std::uint64_t WatchpointManager::ObservedOwnedValue() const noexcept { return observed_owned_value_; }
    std::uint32_t WatchpointManager::NonOwnedChangeFlags() const noexcept { return non_owned_change_flags_; }
    DWORD WatchpointManager::NonOwnedChangeThreadId() const noexcept { return non_owned_change_thread_id_; }
    std::uint64_t WatchpointManager::TraceSessionId() const noexcept { return trace_session_id_; }
    std::uint64_t WatchpointManager::WatchId() const noexcept { return watch_id_; }
    std::uint64_t WatchpointManager::TargetAddress() const noexcept { return target_address_; }
}
