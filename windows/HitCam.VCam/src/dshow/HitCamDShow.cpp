// The "HitCam" DirectShow camera for Windows 10 (Windows 11 uses the Media Foundation one in MediaSource.cpp).
// Based on Softcam's softcam.cpp (third_party/softcam, MIT): HitCam's own CLSID and name and no sender API
// (HitCam.exe sends frames through HitCamVCam.dll, see DShowOutput.cpp). Built for x64 and x86 (32-bit apps).

#include <olectl.h>
#include <initguid.h>

#include <softcamcore/DShowSoftcam.h>

// {BCFB4029-0D53-4154-9649-2240739784B3}: HitCam's own id, so it never collides with other Softcam-based cameras.
DEFINE_GUID(CLSID_HitCamDShow, 0xbcfb4029, 0x0d53, 0x4154, 0x96, 0x49, 0x22, 0x40, 0x73, 0x97, 0x84, 0xb3);

namespace {

const wchar_t kFilterName[] = L"HitCam";

const AMOVIESETUP_MEDIATYPE kPinTypes[] = {
    {&MEDIATYPE_Video, &MEDIASUBTYPE_NULL},
};

const AMOVIESETUP_PIN kPins[] = {
    {
        const_cast<LPWSTR>(L"Output"),  // name
        FALSE,                          // rendered
        TRUE,                           // output
        FALSE,                          // can have none
        FALSE,                          // can have many
        &CLSID_NULL,                    // connects to filter
        nullptr,                        // connects to pin
        1,                              // media types
        kPinTypes,
    },
};

const REGFILTER2 kRegFilter = {1, MERIT_DO_NOT_USE, 1, kPins};

CUnknown* WINAPI CreateInstance(LPUNKNOWN outer, HRESULT* hr) {
    return softcam::Softcam::CreateInstance(outer, CLSID_HitCamDShow, hr);
}

// Registers (or removes) the filter in the video capture device category, where camera apps look for cameras.
HRESULT RegisterInCategory(bool add) {
    HRESULT hr = CoInitialize(nullptr);
    if (FAILED(hr)) return hr;
    IFilterMapper2* mapper = nullptr;
    hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER, IID_IFilterMapper2, reinterpret_cast<void**>(&mapper));
    if (SUCCEEDED(hr)) {
        mapper->UnregisterFilter(&CLSID_VideoInputDeviceCategory, nullptr, CLSID_HitCamDShow);
        if (add) hr = mapper->RegisterFilter(CLSID_HitCamDShow, kFilterName, nullptr, &CLSID_VideoInputDeviceCategory, kFilterName, &kRegFilter);
        mapper->Release();
    }
    CoFreeUnusedLibraries();
    CoUninitialize();
    return hr;
}

}  // namespace

// COM class table used by the DirectShow base classes.
CFactoryTemplate g_Templates[] = {
    {kFilterName, &CLSID_HitCamDShow, &CreateInstance, nullptr, nullptr},
};
int g_cTemplates = sizeof(g_Templates) / sizeof(g_Templates[0]);

STDAPI DllRegisterServer() {
    const HRESULT hr = AMovieDllRegisterServer2(TRUE);
    return FAILED(hr) ? hr : RegisterInCategory(true);
}

STDAPI DllUnregisterServer() {
    const HRESULT hr = RegisterInCategory(false);
    const HRESULT removed = AMovieDllRegisterServer2(FALSE);
    return FAILED(hr) ? hr : removed;
}

extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE, ULONG, LPVOID);

BOOL APIENTRY DllMain(HANDLE module, DWORD reason, LPVOID reserved) {
    return DllEntryPoint(static_cast<HINSTANCE>(module), reason, reserved);
}
