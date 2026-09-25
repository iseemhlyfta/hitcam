// Native crash log for HitCam.exe: a first vectored exception handler that writes where a native fault happened
// (exception code, module + offset, the faulting data address, the stack as module + offset) to a file, since such
// crashes can end the process before Windows Error Reporting records anything. Diagnostics only: the exception then
// goes on as if nothing was here.

#include <windows.h>

#include <atomic>
#include <cstdio>
#include <cwchar>

namespace {

HANDLE g_file = INVALID_HANDLE_VALUE;
std::atomic<int> g_written{0};
constexpr int kMaxReports = 20;

// Module base name and offset of an address; false for addresses outside any module (e.g. managed JIT code).
bool Describe(const void* address, char* out, size_t size) {
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            static_cast<LPCWSTR>(address), &module) || !module) {
        std::snprintf(out, size, "%p", address);
        return false;
    }
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(module, path, MAX_PATH);
    const wchar_t* name = std::wcsrchr(path, L'\\');
    name = name ? name + 1 : path;
    std::snprintf(out, size, "%ls+0x%llx", name,
                  static_cast<unsigned long long>(reinterpret_cast<const char*>(address) - reinterpret_cast<const char*>(module)));
    return true;
}

void Write(const char* text) {
    DWORD written = 0;
    WriteFile(g_file, text, static_cast<DWORD>(std::strlen(text)), &written, nullptr);
}

LONG CALLBACK Handler(EXCEPTION_POINTERS* info) {
    const DWORD code = info->ExceptionRecord->ExceptionCode;
    const bool fatal = code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_STACK_OVERFLOW || code == EXCEPTION_ILLEGAL_INSTRUCTION ||
                       code == EXCEPTION_IN_PAGE_ERROR || code == STATUS_HEAP_CORRUPTION || code == STATUS_STACK_BUFFER_OVERRUN;
    if (!fatal || g_file == INVALID_HANDLE_VALUE) return EXCEPTION_CONTINUE_SEARCH;

    char where[320];
    // Faults in managed code (null references) are normal exceptions for .NET: only native modules are reported.
    if (!Describe(info->ExceptionRecord->ExceptionAddress, where, sizeof(where))) return EXCEPTION_CONTINUE_SEARCH;
    if (g_written.fetch_add(1) >= kMaxReports) return EXCEPTION_CONTINUE_SEARCH;

    SYSTEMTIME now;
    GetLocalTime(&now);
    char line[1024];
    std::snprintf(line, sizeof(line), "%04d-%02d-%02d %02d:%02d:%02d.%03d [native %lu] exception 0x%08lX at %s", now.wYear, now.wMonth,
                  now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, GetCurrentThreadId(), code, where);
    Write(line);
    if (code == EXCEPTION_ACCESS_VIOLATION && info->ExceptionRecord->NumberParameters >= 2) {
        const ULONG_PTR kind = info->ExceptionRecord->ExceptionInformation[0];
        std::snprintf(line, sizeof(line), ", %s 0x%llx", kind == 0 ? "reading" : kind == 1 ? "writing" : "executing",
                      static_cast<unsigned long long>(info->ExceptionRecord->ExceptionInformation[1]));
        Write(line);
    }
    Write("\r\n");

    void* frames[48];
    const USHORT count = RtlCaptureStackBackTrace(0, 48, frames, nullptr);
    for (USHORT i = 0; i < count; ++i) {
        Describe(frames[i], where, sizeof(where));
        std::snprintf(line, sizeof(line), "    %s\r\n", where);
        Write(line);
    }
    FlushFileBuffers(g_file);
    return EXCEPTION_CONTINUE_SEARCH;
}

}  // namespace

extern "C" {

// Starts writing native faults of this process to `path` (appended). Call once, early. Any thread.
__declspec(dllexport) BOOL __stdcall HitCam_InstallCrashLog(const wchar_t* path) {
    if (!path || g_file != INVALID_HANDLE_VALUE) return FALSE;
    g_file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (g_file == INVALID_HANDLE_VALUE) return FALSE;
    return AddVectoredExceptionHandler(1, Handler) != nullptr;
}

}  // extern "C"
