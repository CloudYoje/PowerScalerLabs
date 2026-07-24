#include "logger.h"
#include <cstdarg>
#include <cstdio>
#include <filesystem>
#include <mutex>
#include <vector>
#include <versionhelpers.h>

namespace {
HMODULE g_module = nullptr;
std::mutex g_logMutex;

std::wstring Timestamp() {
    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t text[64]{};
    swprintf_s(text, L"%04u-%02u-%02u %02u:%02u:%02u.%03u", st.wYear, st.wMonth,
               st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    return text;
}
}

namespace hs {
void SetModule(HMODULE module) noexcept { g_module = module; }

std::wstring ModuleDirectory() {
    wchar_t path[MAX_PATH]{};
    const DWORD count = GetModuleFileNameW(g_module, path, MAX_PATH);
    if (count == 0 || count >= MAX_PATH) return L".";
    return std::filesystem::path(path).parent_path().wstring();
}

void Log(const wchar_t* format, ...) {
    wchar_t message[2048]{};
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, _countof(message), _TRUNCATE, format, args);
    va_end(args);

    const auto logPath = std::filesystem::path(ModuleDirectory()) / L"HealthScale.Runtime.log";
    std::lock_guard lock(g_logMutex);
    FILE* file = nullptr;
    if (_wfopen_s(&file, logPath.c_str(), L"a, ccs=UTF-8") != 0 || !file) return;
    fwprintf(file, L"[%ls] %ls\n", Timestamp().c_str(), message);
    fclose(file);
}

std::wstring ReadFileVersion(const std::wstring& path) {
    DWORD ignored = 0;
    const DWORD size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
    if (size == 0) return L"unknown";

    std::vector<BYTE> data(size);
    if (!GetFileVersionInfoW(path.c_str(), 0, size, data.data())) return L"unknown";

    VS_FIXEDFILEINFO* info = nullptr;
    UINT infoSize = 0;
    if (!VerQueryValueW(data.data(), L"\\", reinterpret_cast<void**>(&info), &infoSize) ||
        !info || infoSize < sizeof(VS_FIXEDFILEINFO)) return L"unknown";

    wchar_t version[64]{};
    swprintf_s(version, L"%u.%u.%u.%u",
               HIWORD(info->dwFileVersionMS), LOWORD(info->dwFileVersionMS),
               HIWORD(info->dwFileVersionLS), LOWORD(info->dwFileVersionLS));
    return version;
}
}
