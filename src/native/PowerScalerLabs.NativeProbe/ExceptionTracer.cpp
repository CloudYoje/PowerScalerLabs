#include "ExceptionTracer.h"

#include "ProbeEvents.h"

namespace
{
    psl::probe::SharedMemoryContext* g_shared_memory = nullptr;
    volatile LONG g_active = 0;
    volatile LONG64 g_trace_session_id = 0;
    volatile LONG64 g_watch_id = 0;
    volatile LONG64 g_watched_address = 0;
}

namespace psl::probe
{
    bool ExceptionTracer::Install(SharedMemoryContext& shared_memory) noexcept
    {
        if (handler_ != nullptr || shared_memory.region == nullptr)
        {
            return false;
        }
        g_shared_memory = &shared_memory;
        handler_ = AddVectoredExceptionHandler(1, HandleException);
        if (handler_ == nullptr)
        {
            g_shared_memory = nullptr;
            return false;
        }
        return true;
    }

    void ExceptionTracer::Activate(
        const std::uint64_t trace_session_id,
        const std::uint64_t watch_id,
        const std::uint64_t address) noexcept
    {
        InterlockedExchange64(&g_trace_session_id, static_cast<LONG64>(trace_session_id));
        InterlockedExchange64(&g_watch_id, static_cast<LONG64>(watch_id));
        InterlockedExchange64(&g_watched_address, static_cast<LONG64>(address));
        MemoryBarrier();
        InterlockedExchange(&g_active, 1);
    }

    void ExceptionTracer::Deactivate() noexcept
    {
        InterlockedExchange(&g_active, 0);
        MemoryBarrier();
    }

    bool ExceptionTracer::Remove() noexcept
    {
        Deactivate();
        if (handler_ == nullptr)
        {
            g_shared_memory = nullptr;
            return true;
        }
        const ULONG removed = RemoveVectoredExceptionHandler(handler_);
        if (removed == 0)
        {
            return false;
        }
        handler_ = nullptr;
        g_shared_memory = nullptr;
        return true;
    }

    bool ExceptionTracer::IsInstalled() const noexcept
    {
        return handler_ != nullptr;
    }

    LONG CALLBACK ExceptionTracer::HandleException(EXCEPTION_POINTERS* pointers) noexcept
    {
        if (pointers == nullptr || pointers->ExceptionRecord == nullptr || pointers->ContextRecord == nullptr ||
            pointers->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP ||
            InterlockedCompareExchange(&g_active, 0, 0) == 0)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }

        CONTEXT& context = *pointers->ContextRecord;
        const DWORD64 original_dr6 = context.Dr6;
        if ((original_dr6 & 1ULL) == 0 || g_shared_memory == nullptr || g_shared_memory->region == nullptr)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }

        LARGE_INTEGER qpc{};
        QueryPerformanceCounter(&qpc);
        RawProbeEvent event{};
        event.qpc = static_cast<std::uint64_t>(qpc.QuadPart);
        event.trace_session_id = static_cast<std::uint64_t>(InterlockedCompareExchange64(&g_trace_session_id, 0, 0));
        event.watch_id = static_cast<std::uint64_t>(InterlockedCompareExchange64(&g_watch_id, 0, 0));
        event.rip = context.Rip;
        event.rsp = context.Rsp;
        event.rflags = context.EFlags;
        event.registers[0] = context.Rax;
        event.registers[1] = context.Rbx;
        event.registers[2] = context.Rcx;
        event.registers[3] = context.Rdx;
        event.registers[4] = context.Rsi;
        event.registers[5] = context.Rdi;
        event.registers[6] = context.Rbp;
        event.registers[7] = context.R8;
        event.registers[8] = context.R9;
        event.registers[9] = context.R10;
        event.registers[10] = context.R11;
        event.registers[11] = context.R12;
        event.registers[12] = context.R13;
        event.registers[13] = context.R14;
        event.registers[14] = context.R15;
        event.dr6 = original_dr6;
        event.dr7 = context.Dr7;
        event.watched_address = static_cast<std::uint64_t>(InterlockedCompareExchange64(&g_watched_address, 0, 0));
        event.thread_id = GetCurrentThreadId();
        event.event_type = static_cast<std::uint32_t>(NativeEventType::HardwareWriteTrap);
        event.access_width = 4;
        event.access_type = static_cast<std::uint32_t>(NativeAccessType::Write);
        TryCommitEvent(*g_shared_memory->region, g_shared_memory->event_ready, event);
        context.Dr6 = original_dr6 & ~1ULL;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
}
