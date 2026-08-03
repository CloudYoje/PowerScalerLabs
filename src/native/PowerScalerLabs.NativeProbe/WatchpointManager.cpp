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
        const std::uint32_t width, const std::uint32_t access_type, std::uint32_t& result_code) noexcept
    {
        result_code = 0;
        failure_thread_id_ = 0;
        if (armed_ || address == 0 || width != 4 || access_type != static_cast<std::uint32_t>(NativeAccessType::Write))
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
            states_[state_count_++] = state;
        }

        tracer_.Activate(trace_session_id, watch_id, address);
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
        for (std::size_t state_index = 0; state_index < state_count_;)
        {
            bool still_exists = false;
            for (std::size_t thread_index = 0; thread_index < thread_count; ++thread_index)
            {
                if (states_[state_index].thread_id == thread_ids[thread_index]) { still_exists = true; break; }
            }
            if (still_exists) ++state_index;
            else states_[state_index] = states_[--state_count_];
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
            states_[state_count_++] = state;
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
        if (!thread.IsReady()) { failure_thread_id_ = thread_id; result_code = 14; return false; }
        CONTEXT context{};
        context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(thread.Get(), &context) == FALSE) { failure_thread_id_ = thread_id; result_code = 15; return false; }
        if ((context.Dr7 & 3ULL) != 0) { failure_thread_id_ = thread_id; result_code = 16; return false; }
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
        if (context.Dr0 != target_address_ || context.Dr1 != state.dr1 || context.Dr2 != state.dr2 ||
            context.Dr3 != state.dr3 || (context.Dr7 & kDr0ControlMask) != (state.armed_dr7 & kDr0ControlMask) ||
            (context.Dr7 & ~kDr0ControlMask) != (state.dr7 & ~kDr0ControlMask))
        {
            result_code = 20;
            return false;
        }
        context.Dr0 = state.dr0;
        context.Dr1 = state.dr1;
        context.Dr2 = state.dr2;
        context.Dr3 = state.dr3;
        context.Dr6 = state.dr6;
        context.Dr7 = state.dr7;
        if (SetThreadContext(thread.Get(), &context) == FALSE) { result_code = 17; return false; }
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
    DWORD WatchpointManager::FailureThreadId() const noexcept { return failure_thread_id_; }
    std::uint64_t WatchpointManager::TraceSessionId() const noexcept { return trace_session_id_; }
    std::uint64_t WatchpointManager::WatchId() const noexcept { return watch_id_; }
    std::uint64_t WatchpointManager::TargetAddress() const noexcept { return target_address_; }
}
