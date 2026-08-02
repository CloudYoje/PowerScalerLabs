#pragma once

#include <Windows.h>

#include "ProbeSharedMemory.h"

namespace psl::probe
{
    DWORD WINAPI ProbeWorkerMain(void* parameter) noexcept;
}
