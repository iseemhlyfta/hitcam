#pragma once

#include <windows.h>

#include <new>
#include <system_error>

namespace hitcam {

// C++ exceptions must never cross a COM or C boundary: in the frame server service std::terminate would take
// every camera down, in HitCam.exe the whole app. Every exported / COM entry point that may allocate or start
// a thread runs its body through this.
template <class Body>
HRESULT Guarded(Body&& body) noexcept {
    try {
        return body();
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (const std::system_error& error) {
        const int code = error.code().value();
        return error.code().category() == std::system_category() && code > 0 ? HRESULT_FROM_WIN32(static_cast<DWORD>(code)) : E_FAIL;
    } catch (...) {
        return E_FAIL;
    }
}

}  // namespace hitcam
