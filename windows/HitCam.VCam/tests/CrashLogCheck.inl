// --crashlog: the native crash log (HitCam_InstallCrashLog) records a fault in a native module with its module and
// stack, and lets the exception go on to the program's own handler. Included by VCamTest.cpp.

namespace crashlog_check {

__declspec(noinline) int Fault(volatile int* pointer) { return *pointer; }

// SEH only: no C++ objects in this function (C2712).
bool FaultHandled() {
    __try {
        Fault(reinterpret_cast<volatile int*>(static_cast<uintptr_t>(0x10)));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return true;
    }
    return false;
}

int Run() {
    wchar_t path[MAX_PATH];
    GetTempPathW(MAX_PATH, path);
    wcscat_s(path, L"hitcam-crashlog-check.log");
    DeleteFileW(path);

    bool ok = HitCam_InstallCrashLog(path) != FALSE;
    std::printf("%s: crash log installed\n", ok ? "OK" : "FAIL");

    const bool handled = FaultHandled();
    std::printf("%s: the program's own handler still runs\n", handled ? "OK" : "FAIL");

    std::string text;
    // Shared: the crash log keeps the file open for appending (_wfopen_s would open it exclusively).
    if (FILE* file = _wfsopen(path, L"rb", _SH_DENYNO)) {
        char buffer[4096];
        size_t read;
        while ((read = std::fread(buffer, 1, sizeof(buffer), file)) > 0) text.append(buffer, read);
        std::fclose(file);
    }
    const bool recorded = text.find("exception 0xC0000005 at HitCamVCamTest.exe+") != std::string::npos &&
                          text.find("reading 0x10") != std::string::npos;
    std::printf("%s: fault recorded with module and address\n", recorded ? "OK" : "FAIL");
    const bool stack = text.find("\r\n    HitCamVCamTest.exe+") != std::string::npos;
    std::printf("%s: stack recorded\n", stack ? "OK" : "FAIL");
    std::printf("%s", text.c_str());
    DeleteFileW(path);
    const bool passed = ok && handled && recorded && stack;
    std::printf(passed ? "ALL PASSED\n" : "SOME CHECKS FAILED\n");
    return passed ? 0 : 1;
}

}  // namespace crashlog_check
