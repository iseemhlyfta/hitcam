// --dshow: the DirectShow camera (Windows 10) end to end, without registering anything: HitCamDShow.dll built next
// to this test is loaded directly, frames go in through HitCamVCam.dll (as HitCam.exe sends them) and come out of a
// DirectShow graph like a video app would read them. Included by VCamTest.cpp.

#include <dshow.h>

namespace dshow_check {

// qedit.h (Sample Grabber) left the SDK but the component still ships with Windows.
MIDL_INTERFACE("0579154A-2B53-4994-B0D0-E773148EFF85")
ISampleGrabberCB : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SampleCB(double time, IMediaSample* sample) = 0;
    virtual HRESULT STDMETHODCALLTYPE BufferCB(double time, BYTE* buffer, long length) = 0;
};

MIDL_INTERFACE("6B652FFF-11FE-4fce-92AD-0266B5D7C78F")
ISampleGrabber : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetOneShot(BOOL oneShot) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetMediaType(const AM_MEDIA_TYPE* type) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetConnectedMediaType(AM_MEDIA_TYPE* type) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetBufferSamples(BOOL buffer) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentBuffer(long* size, long* buffer) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentSample(IMediaSample** sample) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetCallback(ISampleGrabberCB* callback, long method) = 0;
};

constexpr GUID kSampleGrabber = {0xC1F400A0, 0x3F08, 0x11d3, {0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37}};
constexpr GUID kNullRenderer = {0xC1F400A4, 0x3F08, 0x11d3, {0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37}};
constexpr GUID kHitCamDShow = {0xbcfb4029, 0x0d53, 0x4154, {0x96, 0x49, 0x22, 0x40, 0x73, 0x97, 0x84, 0xb3}};
constexpr int kWidth = 1920;
constexpr int kHeight = 1080;

bool g_passed = true;

void Check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "OK" : "FAIL", what);
    g_passed = g_passed && ok;
}

// The filter from HitCamDShow.dll next to this test, created through its class factory (no registry).
HRESULT CreateFilter(IBaseFilter** filter) {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring dll(path);
    dll = dll.substr(0, dll.find_last_of(L'\\') + 1) + L"HitCamDShow.dll";
    const HMODULE module = LoadLibraryW(dll.c_str());
    if (!module) return HRESULT_FROM_WIN32(GetLastError());
    using GetClassObject = HRESULT(__stdcall*)(REFCLSID, REFIID, void**);
    const auto get = reinterpret_cast<GetClassObject>(GetProcAddress(module, "DllGetClassObject"));
    if (!get) return E_NOINTERFACE;
    ComPtr<IClassFactory> factory;
    HRESULT hr = get(kHitCamDShow, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return hr;
    return factory->CreateInstance(nullptr, IID_PPV_ARGS(filter));
}

// The installed camera, found the way apps find cameras: the video input device list (the registered DLL loads).
HRESULT FindInstalledFilter(IBaseFilter** filter) {
    ComPtr<ICreateDevEnum> devices;
    HRESULT hr = CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&devices));
    if (FAILED(hr)) return hr;
    ComPtr<IEnumMoniker> monikers;
    hr = devices->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &monikers, 0);
    if (hr != S_OK) return E_FAIL;
    ComPtr<IMoniker> moniker;
    while (monikers->Next(1, &moniker, nullptr) == S_OK) {
        ComPtr<IPropertyBag> properties;
        VARIANT name;
        VariantInit(&name);
        if (SUCCEEDED(moniker->BindToStorage(nullptr, nullptr, IID_PPV_ARGS(&properties))) &&
            SUCCEEDED(properties->Read(L"FriendlyName", &name, nullptr))) {
            std::printf("      video input device: %ls\n", name.bstrVal);
            const bool hitcam = std::wcscmp(name.bstrVal, L"HitCam") == 0;
            VariantClear(&name);
            if (hitcam) return moniker->BindToObject(nullptr, nullptr, IID_PPV_ARGS(filter));
        }
        moniker.Reset();
    }
    return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
}

ComPtr<IPin> FirstPin(IBaseFilter* filter, PIN_DIRECTION wanted) {
    ComPtr<IEnumPins> pins;
    if (FAILED(filter->EnumPins(&pins))) return nullptr;
    ComPtr<IPin> pin;
    while (pins->Next(1, &pin, nullptr) == S_OK) {
        PIN_DIRECTION direction;
        if (SUCCEEDED(pin->QueryDirection(&direction)) && direction == wanted) return pin;
        pin.Reset();
    }
    return nullptr;
}

struct Graph {
    ComPtr<IGraphBuilder> builder;
    ComPtr<IMediaControl> control;
    ComPtr<ISampleGrabber> grabber;
    AM_MEDIA_TYPE type{};
};

HRESULT BuildGraph(IBaseFilter* camera, Graph& graph) {
    HRESULT hr = CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&graph.builder));
    if (FAILED(hr)) return hr;
    ComPtr<IBaseFilter> grabberFilter, renderer;
    if (FAILED(hr = CoCreateInstance(kSampleGrabber, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&grabberFilter)))) return hr;
    if (FAILED(hr = CoCreateInstance(kNullRenderer, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&renderer)))) return hr;
    if (FAILED(hr = grabberFilter.As(&graph.grabber))) return hr;
    AM_MEDIA_TYPE wanted{};
    wanted.majortype = MEDIATYPE_Video;
    wanted.subtype = MEDIASUBTYPE_RGB24;
    graph.grabber->SetMediaType(&wanted);
    graph.grabber->SetBufferSamples(TRUE);
    graph.grabber->SetOneShot(FALSE);
    graph.builder->AddFilter(camera, L"HitCam");
    graph.builder->AddFilter(grabberFilter.Get(), L"Grabber");
    graph.builder->AddFilter(renderer.Get(), L"Null");
    if (FAILED(hr = graph.builder->ConnectDirect(FirstPin(camera, PINDIR_OUTPUT).Get(), FirstPin(grabberFilter.Get(), PINDIR_INPUT).Get(), nullptr))) return hr;
    if (FAILED(hr = graph.builder->ConnectDirect(FirstPin(grabberFilter.Get(), PINDIR_OUTPUT).Get(), FirstPin(renderer.Get(), PINDIR_INPUT).Get(), nullptr))) return hr;
    graph.grabber->GetConnectedMediaType(&graph.type);
    if (FAILED(hr = graph.builder.As(&graph.control))) return hr;
    return graph.control->Run();
}

// The newest frame as top-down BGR (DirectShow RGB24 is bottom-up).
bool ReadFrame(Graph& graph, std::vector<uint8_t>& bgr) {
    long size = 0;
    if (FAILED(graph.grabber->GetCurrentBuffer(&size, nullptr)) || size < kWidth * kHeight * 3) return false;
    std::vector<uint8_t> raw(static_cast<size_t>(size));
    if (FAILED(graph.grabber->GetCurrentBuffer(&size, reinterpret_cast<long*>(raw.data())))) return false;
    bgr.resize(static_cast<size_t>(kWidth) * kHeight * 3);
    for (int y = 0; y < kHeight; ++y) std::memcpy(&bgr[static_cast<size_t>(y) * kWidth * 3], &raw[static_cast<size_t>(kHeight - 1 - y) * kWidth * 3], kWidth * 3);
    return true;
}

int Gray(const std::vector<uint8_t>& bgr, int x, int y) {
    const uint8_t* p = &bgr[(static_cast<size_t>(y) * kWidth + x) * 3];
    return (p[0] + p[1] + p[2]) / 3;
}

// Waits until the camera's picture satisfies `ok` (frames pass through a decoder and two threads).
template <class Predicate>
bool WaitFor(Graph& graph, std::vector<uint8_t>& bgr, Predicate ok, int timeoutMs = 4000) {
    for (int waited = 0; waited < timeoutMs; waited += 50) {
        if (ReadFrame(graph, bgr) && ok(bgr)) return true;
        Sleep(50);
    }
    return false;
}

// Encodes `rounds` flat frames of the given size and feeds them through the decoder bridge.
bool Feed(void* bridge, UINT32 width, UINT32 height, uint8_t luma, int rounds) {
    TestEncoder encoder;
    if (FAILED(encoder.Initialize(width, height))) return false;
    for (int round = 0; round < rounds; ++round) {
        std::vector<std::vector<uint8_t>> units;
        if (FAILED(encoder.Encode(luma, units))) return false;
        for (const auto& unit : units) HitCam_BridgeDecode(bridge, unit.data(), static_cast<uint32_t>(unit.size()), round * 333'333LL);
        Sleep(33);
    }
    return true;
}

// `installed`: the camera registered in the system (--dshow-installed) instead of the DLL next to this test.
int Run(bool installed) {
    HRESULT hr = HitCam_DShowStart();
    if (FAILED(hr)) return Fail("HitCam_DShowStart", hr);
    Check(HitCam_DShowStart() == S_OK, "starting again is harmless");

    ComPtr<IBaseFilter> camera;
    hr = installed ? FindInstalledFilter(&camera) : CreateFilter(&camera);
    if (FAILED(hr)) return Fail(installed ? "find the installed \"HitCam\" camera" : "create the HitCamDShow filter", hr);
    Graph graph;
    hr = BuildGraph(camera.Get(), graph);
    if (FAILED(hr)) return Fail("build the DirectShow graph", hr);

    const auto* info = reinterpret_cast<const VIDEOINFOHEADER*>(graph.type.pbFormat);
    Check(graph.type.subtype == MEDIASUBTYPE_RGB24 && info && info->bmiHeader.biWidth == kWidth && info->bmiHeader.biHeight == kHeight,
          "camera offers 1920x1080 RGB24");

    std::vector<uint8_t> bgr;
    Check(WaitFor(graph, bgr, [](auto& f) { return std::abs(Gray(f, 960, 540) - 19) <= 3; }), "no signal: dark gray picture");

    void* bridge = nullptr;
    hr = HitCam_BridgeCreate(&bridge);
    if (FAILED(hr)) return Fail("HitCam_BridgeCreate", hr);

    // 1080p fills the frame; luma 120 with neutral chroma is gray ~121.
    Feed(bridge, 1920, 1080, 120, 30);
    Check(WaitFor(graph, bgr, [](auto& f) { return std::abs(Gray(f, 960, 540) - 121) <= 8 && std::abs(Gray(f, 5, 5) - 121) <= 8; }),
          "1080p frame reaches the camera");
    std::printf("      center %d, corner %d\n", Gray(bgr, 960, 540), Gray(bgr, 5, 5));

    // 720p is scaled up to fill the whole picture.
    Feed(bridge, 1280, 720, 200, 30);
    Check(WaitFor(graph, bgr, [](auto& f) { return std::abs(Gray(f, 960, 540) - 214) <= 10 && std::abs(Gray(f, 3, 3) - 214) <= 10; }),
          "720p frame scaled to fill 1080p");
    std::printf("      center %d, corner %d\n", Gray(bgr, 960, 540), Gray(bgr, 3, 3));

    // Portrait (the phone rotated 90 degrees) gets black bars left and right.
    Feed(bridge, 1080, 1920, 200, 30);
    Check(WaitFor(graph, bgr, [](auto& f) { return std::abs(Gray(f, 960, 540) - 214) <= 10 && Gray(f, 20, 540) <= 2 && Gray(f, 1900, 540) <= 2; }),
          "portrait frame pillarboxed");
    std::printf("      center %d, left %d, right %d\n", Gray(bgr, 960, 540), Gray(bgr, 20, 540), Gray(bgr, 1900, 540));

    // Orientation: the scene's left half is a gradient from dark (top) to bright (bottom), the right half stripes.
    {
        TestEncoder encoder;
        encoder.UseScene();
        if (SUCCEEDED(encoder.Initialize(1920, 1080))) {
            for (int round = 0; round < 30; ++round) {
                std::vector<std::vector<uint8_t>> units;
                if (FAILED(encoder.Encode(0, units))) break;
                for (const auto& unit : units) HitCam_BridgeDecode(bridge, unit.data(), static_cast<uint32_t>(unit.size()), round * 333'333LL);
                Sleep(33);
            }
        }
        Check(WaitFor(graph, bgr, [](auto& f) { return Gray(f, 480, 1000) - Gray(f, 480, 80) > 120 && std::abs(Gray(f, 1440, 540) - 120) < 40; }),
              "picture is upright and not mirrored");
        std::printf("      left top %d, left bottom %d, right %d\n", Gray(bgr, 480, 80), Gray(bgr, 480, 1000), Gray(bgr, 1440, 540));
    }

    // Phone gone: back to "no signal".
    HitCam_BridgeClearSignal(bridge);
    Check(WaitFor(graph, bgr, [](auto& f) { return std::abs(Gray(f, 960, 540) - 19) <= 3; }), "cleared signal: dark gray again");

    graph.control->Stop();
    HitCam_BridgeDestroy(bridge);
    HitCam_DShowStop();
    CoTaskMemFree(graph.type.pbFormat);
    return g_passed ? 0 : 1;
}

}  // namespace dshow_check
