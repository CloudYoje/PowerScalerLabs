#include <Windows.h>

#include "ProbeRuntime.h"

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        psl::probe::SetProbeModule(module);
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

extern "C" __declspec(dllexport) DWORD WINAPI PSL_Initialize(void* parameter)
{
    return psl::probe::InitializeProbe(parameter);
}

extern "C" __declspec(dllexport) DWORD WINAPI PSL_PrepareUnload(void*)
{
    return psl::probe::PrepareProbeUnload();
}
