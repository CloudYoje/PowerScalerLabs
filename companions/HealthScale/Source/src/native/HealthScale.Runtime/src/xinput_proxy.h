#pragma once

#include <windows.h>
#include <Xinput.h>

// These functions intentionally use internal Proxy_* names. The module-definition
// file exports them under the real XInput names. This avoids colliding with the
// declarations already supplied by newer Windows SDK versions of Xinput.h.
extern "C" {
DWORD WINAPI Proxy_XInputGetState(DWORD, XINPUT_STATE*);
DWORD WINAPI Proxy_XInputSetState(DWORD, XINPUT_VIBRATION*);
DWORD WINAPI Proxy_XInputGetCapabilities(DWORD, DWORD, XINPUT_CAPABILITIES*);
void  WINAPI Proxy_XInputEnable(BOOL);
DWORD WINAPI Proxy_XInputGetBatteryInformation(DWORD, BYTE, XINPUT_BATTERY_INFORMATION*);
DWORD WINAPI Proxy_XInputGetKeystroke(DWORD, DWORD, PXINPUT_KEYSTROKE);
DWORD WINAPI Proxy_XInputGetDSoundAudioDeviceGuids(DWORD, GUID*, GUID*);
DWORD WINAPI Proxy_XInputGetStateEx(DWORD, XINPUT_STATE*);
DWORD WINAPI Proxy_XInputWaitForGuideButton(DWORD, DWORD, void*);
DWORD WINAPI Proxy_XInputCancelGuideButtonWait(DWORD);
DWORD WINAPI Proxy_XInputPowerOffController(DWORD);
}
