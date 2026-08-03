#pragma once

#include <Windows.h>

#include "ProbeSharedMemory.h"

namespace psl::probe
{
    class ExceptionTracer final
    {
    public:
        bool Install(SharedMemoryContext& shared_memory) noexcept;
        void Activate(std::uint64_t trace_session_id, std::uint64_t watch_id, std::uint64_t address) noexcept;
        void Deactivate() noexcept;
        bool Remove() noexcept;
        bool IsInstalled() const noexcept;

    private:
        static LONG CALLBACK HandleException(EXCEPTION_POINTERS* pointers) noexcept;

        void* handler_ = nullptr;
    };
}
