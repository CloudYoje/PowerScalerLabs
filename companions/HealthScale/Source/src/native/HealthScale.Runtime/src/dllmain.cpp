#include "health_overhaul_runtime.h"
#include "hud_health_normalizer.h"
#include "logger.h"

#include <windows.h>
#include <filesystem>

namespace {
HMODULE gLoaderModule = nullptr;

DWORD WINAPI RuntimeThread(void*) {
    Sleep(250);

    wchar_t exePath[MAX_PATH]{};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    const auto version = hs::ReadFileVersion(exePath);

    hs::Log(L"============================================================");
    hs::Log(L"HealthScale runtime initialized.");
    hs::Log(L"Process: %ls", exePath);
    hs::Log(L"Detected DBXV2 file version: %ls", version.c_str());
    hs::Log(L"DBXV2.exe base: 0x%p", GetModuleHandleW(nullptr));
    hs::Log(L"xinput1_3.dll base: 0x%p", GetModuleHandleW(L"xinput1_3.dll"));
    hs::Log(L"xinput_other.dll base: 0x%p", GetModuleHandleW(L"xinput_other.dll"));

    const std::wstring processName = std::filesystem::path(exePath).filename().wstring();
    if (_wcsicmp(processName.c_str(), L"DBXV2.exe") != 0) {
        hs::Log(L"WARNING: Loaded outside DBXV2.exe; unified runtime disabled.");
        return 0;
    }

    if (version != L"1.25.2.0" && version != L"1.25.02.0") {
        hs::Log(L"WARNING: Expected game version 1.25.02.0. No executable hook or fighter memory access will occur.");
        return 0;
    }

    hs::Log(L"Version check passed.");
    if (!hs::InitializeNativeHudHealthNormalizer(gLoaderModule)) {
        hs::Log(L"Native health normalization did not activate. Review HealthScale.Runtime.log.");
    }
    return hs::RunHealthOverhaulRuntime();
}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        gLoaderModule = module;
        DisableThreadLibraryCalls(module);
        hs::SetModule(module);
        HANDLE thread = CreateThread(nullptr, 0, RuntimeThread, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    } else if (reason == DLL_PROCESS_DETACH && reserved == nullptr) {
        // Dynamic unload only. During normal process termination Windows is
        // already dismantling threads and modules, so no teardown work is run.
        hs::ShutdownNativeHudHealthNormalizer();
    }
    return TRUE;
}
