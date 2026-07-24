#pragma once
#include <windows.h>
#include <string>

namespace hs {
void SetModule(HMODULE module) noexcept;
std::wstring ModuleDirectory();
void Log(const wchar_t* format, ...);
std::wstring ReadFileVersion(const std::wstring& path);
}
