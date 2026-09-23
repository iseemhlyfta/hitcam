// COM entry points: the camera frame server creates the media source through these.

#include <windows.h>
#include <mfapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <atomic>
#include <string>

#include "Activator.h"
#include "Shared.h"

using Microsoft::WRL::ComPtr;

namespace {

HMODULE g_module = nullptr;
std::atomic<long> g_locks{0};

class ClassFactory : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IClassFactory> {
public:
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        ComPtr<hitcam::Activator> activator;
        HRESULT hr = Microsoft::WRL::MakeAndInitialize<hitcam::Activator>(&activator);
        return SUCCEEDED(hr) ? activator.CopyTo(riid, object) : hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override {
        lock ? ++g_locks : --g_locks;
        return S_OK;
    }
};

std::wstring ClsidKey() { return std::wstring(L"SOFTWARE\\Classes\\CLSID\\") + hitcam::kMediaSourceClsidString; }

LSTATUS SetString(HKEY key, const wchar_t* name, const std::wstring& value) {
    return RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
                          static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object) {
    if (!object) return E_POINTER;
    *object = nullptr;
    if (clsid != hitcam::kMediaSourceClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = Microsoft::WRL::Make<ClassFactory>();
    return factory ? factory.CopyTo(riid, object) : E_OUTOFMEMORY;
}

// The frame server keeps the DLL for its lifetime; unloading while a source thread runs is never worth the risk.
STDAPI DllCanUnloadNow() { return S_FALSE; }

// Registers under HKLM, because the frame server runs as LOCAL SERVICE and does not see per-user classes.
STDAPI DllRegisterServer() {
    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(g_module, path, MAX_PATH);
    if (length == 0 || length == MAX_PATH) return HRESULT_FROM_WIN32(GetLastError());

    HKEY key = nullptr;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, ClsidKey().c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    status = SetString(key, nullptr, L"HitCam Virtual Camera");

    HKEY server = nullptr;
    if (status == ERROR_SUCCESS) status = RegCreateKeyExW(key, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &server, nullptr);
    if (status == ERROR_SUCCESS) status = SetString(server, nullptr, path);
    if (status == ERROR_SUCCESS) status = SetString(server, L"ThreadingModel", L"Both");
    if (server) RegCloseKey(server);
    RegCloseKey(key);
    return HRESULT_FROM_WIN32(status);
}

STDAPI DllUnregisterServer() {
    const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, ClsidKey().c_str());
    return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}
