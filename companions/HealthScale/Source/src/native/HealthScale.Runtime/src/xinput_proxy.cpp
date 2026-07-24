#include "xinput_proxy.h"
#include "logger.h"
#include <mutex>
#include <string>

namespace {
HMODULE g_xinput = nullptr;
std::once_flag g_once;

void LoadSystemXInput() {
    wchar_t systemDir[MAX_PATH]{};
    const UINT n = GetSystemDirectoryW(systemDir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        hs::Log(L"GetSystemDirectoryW failed: %lu", GetLastError());
        return;
    }

    const wchar_t* candidates[] = {L"xinput1_4.dll", L"xinput9_1_0.dll"};
    for (const wchar_t* name : candidates) {
        std::wstring path(systemDir);
        path += L"\\";
        path += name;
        g_xinput = LoadLibraryW(path.c_str());
        if (g_xinput) {
            hs::Log(L"Forwarding controller calls to %ls", path.c_str());
            return;
        }
    }
    hs::Log(L"Could not load a system XInput DLL. Last error: %lu", GetLastError());
}

HMODULE Module() {
    std::call_once(g_once, LoadSystemXInput);
    return g_xinput;
}

template<typename T>
T Proc(const char* name, WORD ordinal = 0) {
    HMODULE module = Module();
    if (!module) return nullptr;
    FARPROC address = ordinal ? GetProcAddress(module, MAKEINTRESOURCEA(ordinal))
                              : GetProcAddress(module, name);
    return reinterpret_cast<T>(address);
}
}

extern "C" DWORD WINAPI Proxy_XInputGetState(DWORD user, XINPUT_STATE* state) {
    using Fn = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
    if (auto fn = Proc<Fn>("XInputGetState")) return fn(user, state);
    return ERROR_DEVICE_NOT_CONNECTED;
}
extern "C" DWORD WINAPI Proxy_XInputSetState(DWORD user, XINPUT_VIBRATION* vibration) {
    using Fn = DWORD(WINAPI*)(DWORD, XINPUT_VIBRATION*);
    if (auto fn = Proc<Fn>("XInputSetState")) return fn(user, vibration);
    return ERROR_DEVICE_NOT_CONNECTED;
}
extern "C" DWORD WINAPI Proxy_XInputGetCapabilities(DWORD user, DWORD flags, XINPUT_CAPABILITIES* caps) {
    using Fn = DWORD(WINAPI*)(DWORD, DWORD, XINPUT_CAPABILITIES*);
    if (auto fn = Proc<Fn>("XInputGetCapabilities")) return fn(user, flags, caps);
    return ERROR_DEVICE_NOT_CONNECTED;
}
extern "C" void WINAPI Proxy_XInputEnable(BOOL enable) {
    using Fn = void(WINAPI*)(BOOL);
    if (auto fn = Proc<Fn>("XInputEnable")) fn(enable);
}
extern "C" DWORD WINAPI Proxy_XInputGetBatteryInformation(DWORD user, BYTE devType, XINPUT_BATTERY_INFORMATION* battery) {
    using Fn = DWORD(WINAPI*)(DWORD, BYTE, XINPUT_BATTERY_INFORMATION*);
    if (auto fn = Proc<Fn>("XInputGetBatteryInformation")) return fn(user, devType, battery);
    return ERROR_DEVICE_NOT_CONNECTED;
}
extern "C" DWORD WINAPI Proxy_XInputGetKeystroke(DWORD user, DWORD reserved, PXINPUT_KEYSTROKE stroke) {
    using Fn = DWORD(WINAPI*)(DWORD, DWORD, PXINPUT_KEYSTROKE);
    if (auto fn = Proc<Fn>("XInputGetKeystroke")) return fn(user, reserved, stroke);
    return ERROR_EMPTY;
}
extern "C" DWORD WINAPI Proxy_XInputGetDSoundAudioDeviceGuids(DWORD user, GUID* render, GUID* capture) {
    using Fn = DWORD(WINAPI*)(DWORD, GUID*, GUID*);
    if (auto fn = Proc<Fn>("XInputGetDSoundAudioDeviceGuids")) return fn(user, render, capture);
    return ERROR_DEVICE_NOT_CONNECTED;
}
extern "C" DWORD WINAPI Proxy_XInputGetStateEx(DWORD user, XINPUT_STATE* state) {
    using Fn = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
    if (auto fn = Proc<Fn>(nullptr, 100)) return fn(user, state);
    return Proxy_XInputGetState(user, state);
}
extern "C" DWORD WINAPI Proxy_XInputWaitForGuideButton(DWORD user, DWORD flags, void* overlapped) {
    using Fn = DWORD(WINAPI*)(DWORD, DWORD, void*);
    if (auto fn = Proc<Fn>(nullptr, 101)) return fn(user, flags, overlapped);
    return ERROR_CALL_NOT_IMPLEMENTED;
}
extern "C" DWORD WINAPI Proxy_XInputCancelGuideButtonWait(DWORD user) {
    using Fn = DWORD(WINAPI*)(DWORD);
    if (auto fn = Proc<Fn>(nullptr, 102)) return fn(user);
    return ERROR_CALL_NOT_IMPLEMENTED;
}
extern "C" DWORD WINAPI Proxy_XInputPowerOffController(DWORD user) {
    using Fn = DWORD(WINAPI*)(DWORD);
    if (auto fn = Proc<Fn>(nullptr, 103)) return fn(user);
    return ERROR_CALL_NOT_IMPLEMENTED;
}
