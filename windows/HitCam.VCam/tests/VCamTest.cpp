// End-to-end check of the installed virtual camera without a phone:
// adds the camera, opens it like a video app would, checks that frames written to shared memory come out,
// then sends real H.264 through the decoder bridge and checks the decoded picture reaches the camera.
// Requires the COM class to be registered: HitCam's "Install camera", or as admin a copy of HitCamVCam.dll in
// %ProgramFiles%\HitCam and `regsvr32 "%ProgramFiles%\HitCam\HitCamVCam.dll"` (the DLL refuses to register from
// any other folder, since the frame server service would load it). The camera then uses that registered copy,
// not the one built next to this test.
//   --list            print the cameras apps can see
//   --process         picture processing (temporal noise reduction, colour, sharpness, NVIDIA artifact reduction)
//                     of the decoder bridge; no camera and no camera output needed
//   --overlay         detection boxes burnt into the camera frames (HitCam_BridgeSetOverlay); no camera output
//   --shot            the finger-gun shot effect in the camera frames (HitCam_BridgeShot); no camera output
//   --source          the media source of the DLL built next to this test, created in-process without
//                     registration; also checks the shared-memory permissions with the registered camera
//   --dshow           the DirectShow camera (Windows 10) of the DLLs built next to this test, without registration
//   --dshow-installed the same with the DirectShow camera installed in the system, found as apps find it

#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mfvirtualcamera.h>
#include <codecapi.h>
#include <mftransform.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <mutex>
#include <utility>
#include <vector>

#include "../src/Overlay.h"
#include "../src/Processing.h"
#include "../src/ShotEffect.h"
#include "../src/Shared.h"

using Microsoft::WRL::ComPtr;

extern "C" HRESULT __stdcall HitCam_VirtualCameraStart(const wchar_t* friendlyName, void** handle);
extern "C" void __stdcall HitCam_VirtualCameraStop(void* handle);
extern "C" HRESULT __stdcall HitCam_BridgeCreate(void** handle);
extern "C" HRESULT __stdcall HitCam_BridgeDecode(void* handle, const uint8_t* data, uint32_t length, int64_t timestamp);
extern "C" void __stdcall HitCam_BridgeDestroy(void* handle);
extern "C" void __stdcall HitCam_BridgeClearSignal(void* handle);
extern "C" HRESULT __stdcall HitCam_DShowStart();
extern "C" void __stdcall HitCam_DShowStop();
extern "C" void __stdcall HitCam_DShowConvert(const uint8_t* nv12, uint32_t width, uint32_t height, uint8_t* bgr);
extern "C" void __stdcall HitCam_BridgePreviewInfo(void* handle, uint32_t* width, uint32_t* height, uint64_t* frame);
extern "C" BOOL __stdcall HitCam_BridgeCopyPreview(void* handle, uint8_t* destination, uint32_t stride, uint32_t width, uint32_t height);
extern "C" BOOL __stdcall HitCam_ArtifactReductionAvailable();
extern "C" void __stdcall HitCam_BridgeSetProcessing(void* handle, const HitCamProcessing* settings);
extern "C" void __stdcall HitCam_BridgeProcessingStats(void* handle, double* gpuMs, double* artifactMs, int* artifactError);
extern "C" void __stdcall HitCam_BridgeProcessFrame(void* handle, uint8_t* nv12, uint32_t width, uint32_t height);
extern "C" void __stdcall HitCam_BridgePreviewOnly(void* handle);
extern "C" void __stdcall HitCam_BridgeSetOverlay(void* handle, const HitCamOverlayBox* boxes, int32_t count);
extern "C" void __stdcall HitCam_BridgeShot(void* handle, const HitCamShot* shot);
extern "C" void __stdcall HitCam_TestShotFrame(const HitCamShot* shot, double elapsedMs, uint8_t* nv12, uint32_t width, uint32_t height);
using TestOutputCallback = void(__stdcall*)(void* context, const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height);
extern "C" void __stdcall HitCam_BridgeTestOutput(void* handle, TestOutputCallback callback, void* context);
extern "C" void __stdcall HitCam_BridgeTestPublish(void* handle, const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height);

namespace {

int Fail(const char* step, HRESULT hr) {
    std::printf("FAIL: %s (0x%08lX)\n", step, static_cast<unsigned long>(hr));
    return 1;
}

// Produces real H.264 (Annex-B, like the phone sends) with the Windows software encoder.
class TestEncoder {
public:
    HRESULT Initialize(UINT32 width, UINT32 height, UINT32 bitrate = 4'000'000) {
        width_ = width;
        height_ = height;
        HRESULT hr = CoCreateInstance(CLSID_CMSH264EncoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&transform_));
        ComPtr<IMFMediaType> output, input;
        if (SUCCEEDED(hr)) hr = MFCreateMediaType(&output);
        if (SUCCEEDED(hr)) hr = output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = output->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        if (SUCCEEDED(hr)) hr = output->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(output.Get(), MF_MT_FRAME_SIZE, width, height);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(output.Get(), MF_MT_FRAME_RATE, 30, 1);
        if (SUCCEEDED(hr)) hr = output->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = output->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
        if (SUCCEEDED(hr)) hr = transform_->SetOutputType(0, output.Get(), 0);
        if (SUCCEEDED(hr)) hr = MFCreateMediaType(&input);
        if (SUCCEEDED(hr)) hr = input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(input.Get(), MF_MT_FRAME_SIZE, width, height);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(input.Get(), MF_MT_FRAME_RATE, 30, 1);
        if (SUCCEEDED(hr)) hr = input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = transform_->SetInputType(0, input.Get(), 0);
        if (SUCCEEDED(hr)) hr = transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        return hr;
    }

    // Encodes a flat frame, optionally with random luma noise of +-`noise`; `units` receives the access units
    // the encoder produced (possibly none yet).
    HRESULT Encode(uint8_t luma, std::vector<std::vector<uint8_t>>& units, int noise = 0) {
        const DWORD size = width_ * height_ * 3 / 2;
        ComPtr<IMFMediaBuffer> buffer;
        HRESULT hr = MFCreateMemoryBuffer(size, &buffer);
        BYTE* data = nullptr;
        if (SUCCEEDED(hr)) hr = buffer->Lock(&data, nullptr, nullptr);
        if (FAILED(hr)) return hr;
        if (scene_) {
            DrawScene(data);
        } else {
            std::memset(data, luma, width_ * height_);
            std::memset(data + width_ * height_, 128, width_ * height_ / 2);
        }
        for (UINT32 i = 0; noise > 0 && i < width_ * height_; ++i) {
            seed_ = seed_ * 1664525u + 1013904223u;
            data[i] = static_cast<uint8_t>(std::clamp(data[i] + static_cast<int>(seed_ >> 24) % (2 * noise + 1) - noise, 16, 235));
        }
        buffer->Unlock();
        buffer->SetCurrentLength(size);

        ComPtr<IMFSample> sample;
        hr = MFCreateSample(&sample);
        if (SUCCEEDED(hr)) hr = sample->AddBuffer(buffer.Get());
        if (SUCCEEDED(hr)) hr = sample->SetSampleTime(time_);
        if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(333'333);
        if (SUCCEEDED(hr)) hr = transform_->ProcessInput(0, sample.Get(), 0);
        if (FAILED(hr)) return hr;
        time_ += 333'333;

        while (true) {
            MFT_OUTPUT_STREAM_INFO info{};
            hr = transform_->GetOutputStreamInfo(0, &info);
            if (FAILED(hr)) return hr;
            ComPtr<IMFSample> out;
            ComPtr<IMFMediaBuffer> outBuffer;
            hr = MFCreateMemoryBuffer(info.cbSize ? info.cbSize : 4 * 1024 * 1024, &outBuffer);
            if (SUCCEEDED(hr)) hr = MFCreateSample(&out);
            if (SUCCEEDED(hr)) hr = out->AddBuffer(outBuffer.Get());
            if (FAILED(hr)) return hr;

            MFT_OUTPUT_DATA_BUFFER output{};
            output.pSample = out.Get();
            DWORD status = 0;
            hr = transform_->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents) output.pEvents->Release();
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
            if (FAILED(hr)) return hr;

            ComPtr<IMFMediaBuffer> contiguous;
            hr = out->ConvertToContiguousBuffer(&contiguous);
            BYTE* bytes = nullptr;
            DWORD length = 0;
            if (SUCCEEDED(hr)) hr = contiguous->Lock(&bytes, nullptr, &length);
            if (FAILED(hr)) return hr;
            units.emplace_back(bytes, bytes + length);
            contiguous->Unlock();
        }
    }

private:
    ComPtr<IMFTransform> transform_;
    UINT32 width_ = 0;
    UINT32 height_ = 0;
    LONGLONG time_ = 0;
    uint32_t seed_ = 1;
    bool scene_ = false;

public:
    // Instead of a flat frame: colour gradients (left half) and fine texture with thin lines (right half).
    void UseScene() { scene_ = true; }

private:
    void DrawScene(uint8_t* data) const {
        const UINT32 half = width_ / 2;
        for (UINT32 y = 0; y < height_; ++y) {
            for (UINT32 x = 0; x < width_; ++x) {
                int value;
                if (x < half) {
                    value = 40 + static_cast<int>(180.0 * y / height_);
                } else {
                    // Stripes 3 px wide plus a grid of 1 px lines, like fabric and text.
                    value = ((x / 3) % 2 == 0 ? 150 : 90) + ((x % 40 == 0 || y % 40 == 0) ? -60 : 0);
                }
                data[static_cast<size_t>(y) * width_ + x] = static_cast<uint8_t>(std::clamp(value, 16, 235));
            }
        }
        uint8_t* chroma = data + static_cast<size_t>(width_) * height_;
        for (UINT32 y = 0; y < height_ / 2; ++y) {
            for (UINT32 x = 0; x < width_ / 2; ++x) {
                const bool left = x * 2 < half;
                // Left half: blue-to-yellow and green-to-magenta gradients; right half: skin-like warm tone.
                chroma[(static_cast<size_t>(y) * (width_ / 2) + x) * 2] = static_cast<uint8_t>(left ? 64 + 128 * x * 2 / half : 110);
                chroma[(static_cast<size_t>(y) * (width_ / 2) + x) * 2 + 1] = static_cast<uint8_t>(left ? 64 + 128 * y * 2 / height_ : 150);
            }
        }
    }
};

HRESULT OpenCamera(const wchar_t* name, IMFSourceReader** reader) {
    ComPtr<IMFAttributes> query;
    HRESULT hr = MFCreateAttributes(&query, 1);
    if (SUCCEEDED(hr)) hr = query->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    IMFActivate** devices = nullptr;
    UINT32 count = 0;
    if (SUCCEEDED(hr)) hr = MFEnumDeviceSources(query.Get(), &devices, &count);
    if (FAILED(hr)) return hr;

    hr = MF_E_NOT_FOUND;
    for (UINT32 i = 0; i < count; ++i) {
        wchar_t* friendly = nullptr;
        UINT32 length = 0;
        if (SUCCEEDED(devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &friendly, &length))) {
            std::wprintf(L"  camera: %s\n", friendly);
            if (hr == MF_E_NOT_FOUND && std::wstring(friendly).starts_with(name)) {
                ComPtr<IMFMediaSource> source;
                hr = devices[i]->ActivateObject(IID_PPV_ARGS(&source));
                if (SUCCEEDED(hr)) hr = MFCreateSourceReaderFromMediaSource(source.Get(), nullptr, reader);
            }
            CoTaskMemFree(friendly);
        }
        devices[i]->Release();
    }
    CoTaskMemFree(devices);
    return hr;
}

// Windows appends " (Windows Virtual Camera)" (localized) to the name, hence the prefix match above.
// Reads one frame and returns the average luma of its NV12 Y plane.
HRESULT ReadAverageLuma(IMFSourceReader* reader, UINT32 width, UINT32 height, double* luma) {
    for (int attempt = 0; attempt < 30; ++attempt) {
        DWORD stream = 0, flags = 0;
        LONGLONG time = 0;
        ComPtr<IMFSample> sample;
        HRESULT hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &stream, &flags, &time, &sample);
        if (FAILED(hr)) return hr;
        if (!sample) continue;

        ComPtr<IMFMediaBuffer> buffer;
        hr = sample->ConvertToContiguousBuffer(&buffer);
        if (FAILED(hr)) return hr;
        BYTE* data = nullptr;
        DWORD length = 0;
        hr = buffer->Lock(&data, nullptr, &length);
        if (FAILED(hr)) return hr;
        if (length < width * height) {
            buffer->Unlock();
            return E_UNEXPECTED;
        }
        unsigned long long sum = 0;
        for (UINT32 i = 0; i < width * height; ++i) sum += data[i];
        buffer->Unlock();
        *luma = static_cast<double>(sum) / (static_cast<double>(width) * height);
        return S_OK;
    }
    return MF_E_END_OF_STREAM;
}

}  // namespace

// MARK: --source

namespace source_check {

bool g_passed = true;

void Check(bool ok, const char* what, double value = 0, double expected = 0) {
    std::printf("%s: %s (%.1f, expected %.1f)\n", ok ? "OK" : "FAIL", what, value, expected);
    g_passed = g_passed && ok;
}

// The class factory of the DLL loaded into this process (the one built next to the test), without the registry.
HRESULT CreateLocalActivate(IMFActivate** activate) {
    using GetClassObject = HRESULT(__stdcall*)(REFCLSID, REFIID, void**);
    const HMODULE module = GetModuleHandleW(L"HitCamVCam.dll");
    const auto get = module ? reinterpret_cast<GetClassObject>(GetProcAddress(module, "DllGetClassObject")) : nullptr;
    if (!get) return E_NOINTERFACE;
    ComPtr<IClassFactory> factory;
    HRESULT hr = get(hitcam::kMediaSourceClsid, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(hr)) hr = factory->CreateInstance(nullptr, IID_PPV_ARGS(activate));
    return hr;
}

HRESULT WaitEvent(IMFMediaEventGenerator* generator, MediaEventType wanted, IMFMediaEvent** result, DWORD timeoutMs = 2000) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    while (GetTickCount64() < deadline) {
        ComPtr<IMFMediaEvent> event;
        HRESULT hr = generator->GetEvent(MF_EVENT_FLAG_NO_WAIT, &event);
        if (hr == MF_E_NO_EVENTS_AVAILABLE) {
            Sleep(2);
            continue;
        }
        if (FAILED(hr)) return hr;
        MediaEventType type = MEUnknown;
        event->GetType(&type);
        if (type == MEError) {
            HRESULT status = S_OK;
            event->GetStatus(&status);
            return FAILED(status) ? status : E_FAIL;
        }
        if (type == wanted) {
            if (result) *result = event.Detach();
            return S_OK;
        }
    }
    return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
}

HRESULT StartWithType(IMFMediaSource* source, UINT32 width, UINT32 height, const GUID& subtype = MFVideoFormat_NV12) {
    ComPtr<IMFMediaType> type;
    HRESULT hr = MFCreateMediaType(&type);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, subtype);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, 30, 1);
    IMFMediaType* types[] = {type.Get()};
    ComPtr<IMFStreamDescriptor> stream;
    if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(0, 1, types, &stream);
    ComPtr<IMFMediaTypeHandler> handler;
    if (SUCCEEDED(hr)) hr = stream->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(type.Get());
    ComPtr<IMFPresentationDescriptor> descriptor;
    if (SUCCEEDED(hr)) hr = MFCreatePresentationDescriptor(1, stream.GetAddressOf(), &descriptor);
    if (SUCCEEDED(hr)) hr = descriptor->SelectStream(0);
    PROPVARIANT start;
    PropVariantInit(&start);
    if (SUCCEEDED(hr)) hr = source->Start(descriptor.Get(), &GUID_NULL, &start);
    return hr;
}

// Starts at width x height and returns the stream from MENewStream.
HRESULT StartStream(IMFMediaSource* source, UINT32 width, UINT32 height, IMFMediaStream** stream) {
    HRESULT hr = StartWithType(source, width, height);
    ComPtr<IMFMediaEvent> event;
    if (SUCCEEDED(hr)) hr = WaitEvent(source, MENewStream, &event);
    PROPVARIANT value;
    PropVariantInit(&value);
    if (SUCCEEDED(hr)) hr = event->GetValue(&value);
    if (SUCCEEDED(hr)) hr = value.vt == VT_UNKNOWN && value.punkVal ? value.punkVal->QueryInterface(IID_PPV_ARGS(stream)) : E_UNEXPECTED;
    PropVariantClear(&value);
    if (SUCCEEDED(hr)) hr = WaitEvent(*stream, MEStreamStarted, nullptr);
    return hr;
}

HRESULT SampleLuma(IMFMediaEvent* event, UINT32 width, UINT32 height, double* luma) {
    PROPVARIANT value;
    PropVariantInit(&value);
    HRESULT hr = event->GetValue(&value);
    ComPtr<IMFSample> sample;
    if (SUCCEEDED(hr)) hr = value.vt == VT_UNKNOWN && value.punkVal ? value.punkVal->QueryInterface(IID_PPV_ARGS(&sample)) : E_UNEXPECTED;
    PropVariantClear(&value);
    ComPtr<IMFMediaBuffer> buffer;
    if (SUCCEEDED(hr)) hr = sample->ConvertToContiguousBuffer(&buffer);
    BYTE* data = nullptr;
    DWORD length = 0;
    if (SUCCEEDED(hr)) hr = buffer->Lock(&data, nullptr, &length);
    if (FAILED(hr)) return hr;
    unsigned long long sum = 0;
    if (length >= width * height * 3 / 2) {
        for (UINT32 i = 0; i < width * height; ++i) sum += data[i];
    } else {
        hr = E_UNEXPECTED;
    }
    buffer->Unlock();
    *luma = static_cast<double>(sum) / (static_cast<double>(width) * height);
    return hr;
}

HRESULT RequestLuma(IMFMediaStream* stream, UINT32 width, UINT32 height, double* luma) {
    HRESULT hr = stream->RequestSample(nullptr);
    ComPtr<IMFMediaEvent> event;
    if (SUCCEEDED(hr)) hr = WaitEvent(stream, MEMediaSample, &event);
    if (SUCCEEDED(hr)) hr = SampleLuma(event.Get(), width, height, luma);
    return hr;
}

void Publish(hitcam::SharedHeader* header, uint32_t width, uint32_t height, uint8_t luma) {
    InterlockedIncrement64(&header->sequence);
    MemoryBarrier();
    std::memset(hitcam::FrameData(header), luma, static_cast<size_t>(width) * height);
    std::memset(hitcam::FrameData(header) + static_cast<size_t>(width) * height, 128, static_cast<size_t>(width) * height / 2);
    header->width = width;
    header->height = height;
    InterlockedExchange64(&header->updatedMs, static_cast<LONG64>(GetTickCount64()));
    MemoryBarrier();
    InterlockedIncrement64(&header->sequence);
}

std::wstring SectionSddl(HANDLE mapping) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (GetSecurityInfo(mapping, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION, nullptr, nullptr,
                        nullptr, nullptr, &descriptor) != ERROR_SUCCESS) {
        return L"?";
    }
    wchar_t* text = nullptr;
    std::wstring result = L"?";
    if (ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, SDDL_REVISION_1,
                                                             DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION, &text, nullptr)) {
        result = text;
        LocalFree(text);
    }
    LocalFree(descriptor);
    return result;
}

// Without the registered camera: registration guard, type checks, request queue, lifetime.
void CheckLocalSource() {
    {
        // This copy is not in Program Files: it must refuse before touching the registry.
        using Register = HRESULT(__stdcall*)();
        const HMODULE module = GetModuleHandleW(L"HitCamVCam.dll");
        const auto registerServer = module ? reinterpret_cast<Register>(GetProcAddress(module, "DllRegisterServer")) : nullptr;
        const HRESULT hr = registerServer ? registerServer() : E_NOINTERFACE;
        Check(hr == E_ACCESSDENIED, "DllRegisterServer outside Program Files refused", hr, E_ACCESSDENIED);
    }

    ComPtr<IMFActivate> activate;
    HRESULT hr = CreateLocalActivate(&activate);
    ComPtr<IMFMediaSource> source;
    if (SUCCEEDED(hr)) hr = activate->ActivateObject(IID_PPV_ARGS(&source));
    Check(SUCCEEDED(hr), "local media source created (DllGetClassObject of the built DLL)", hr, 0);
    if (FAILED(hr)) return;

    hr = StartWithType(source.Get(), 800, 600);
    Check(hr == MF_E_INVALIDMEDIATYPE, "800x600 NV12 rejected with MF_E_INVALIDMEDIATYPE", hr, MF_E_INVALIDMEDIATYPE);
    hr = StartWithType(source.Get(), 1280, 720, MFVideoFormat_YUY2);
    Check(hr == MF_E_INVALIDMEDIATYPE, "1280x720 YUY2 rejected", hr, MF_E_INVALIDMEDIATYPE);
    hr = StartWithType(source.Get(), 0xFFFF0000u, 0xFFFF0000u);
    Check(hr == MF_E_INVALIDMEDIATYPE, "absurd size rejected", hr, MF_E_INVALIDMEDIATYPE);

    ComPtr<IMFMediaStream> stream;
    hr = StartStream(source.Get(), 640, 360, &stream);
    Check(SUCCEEDED(hr), "640x360 NV12 started", hr, 0);
    if (FAILED(hr)) return;

    // Far more requests than the queue keeps: all accepted, the oldest dropped, the newest answered.
    int accepted = 0;
    for (int i = 0; i < 50; ++i) accepted += SUCCEEDED(stream->RequestSample(nullptr)) ? 1 : 0;
    int samples = 0;
    while (SUCCEEDED(WaitEvent(stream.Get(), MEMediaSample, nullptr, 600))) ++samples;
    Check(accepted == 50 && samples == 8, "50 requests at once: samples delivered (queue limit 8)", samples, 8);

    ComPtr<IMFMediaStream2> stream2;
    stream.As(&stream2);
    hr = source->Stop();
    WaitEvent(stream.Get(), MEStreamStopped, nullptr);
    if (stream2) stream2->SetStreamState(MF_STREAM_STATE_RUNNING);
    hr = stream->RequestSample(nullptr);
    Check(hr == MF_E_MEDIA_SOURCE_WRONGSTATE, "stopped source + SetStreamState(RUNNING): request refused", hr, MF_E_MEDIA_SOURCE_WRONGSTATE);

    // Restart, then release the source without Shutdown: the stream must be shut down (worker stopped) too.
    hr = StartWithType(source.Get(), 1280, 720);
    if (SUCCEEDED(hr)) hr = WaitEvent(source.Get(), MEUpdatedStream, nullptr);
    if (SUCCEEDED(hr)) hr = WaitEvent(stream.Get(), MEStreamStarted, nullptr);
    Check(SUCCEEDED(hr), "restarted at 1280x720", hr, 0);
    if (FAILED(hr)) return;
    double luma = 0;
    hr = RequestLuma(stream.Get(), 1280, 720, &luma);
    Check(SUCCEEDED(hr), "sample after restart", luma, luma);
    ComPtr<IMFMediaSource> parent;
    hr = stream->GetMediaSource(&parent);
    Check(SUCCEEDED(hr) && parent.Get() == source.Get(), "stream -> source", hr, 0);
    parent.Reset();
    activate->DetachObject();
    activate.Reset();
    source.Reset();
    MF_STREAM_STATE state{};
    hr = stream2 ? stream2->GetStreamState(&state) : E_NOINTERFACE;
    Check(hr == MF_E_SHUTDOWN, "source released without Shutdown: stream shut down with it", hr, MF_E_SHUTDOWN);
    hr = stream->GetMediaSource(&parent);
    Check(hr == MF_E_SHUTDOWN, "stream no longer hands out the source", hr, MF_E_SHUTDOWN);
}

// With the registered camera running, the frame server creates the section; the local source reads it.
void CheckSharedFrames() {
    void* camera = nullptr;
    HRESULT hr = HitCam_VirtualCameraStart(L"HitCam Source Test", &camera);
    ComPtr<IMFSourceReader> reader;
    if (SUCCEEDED(hr)) hr = OpenCamera(L"HitCam Source Test", &reader);
    double luma = 0;
    if (SUCCEEDED(hr)) hr = ReadAverageLuma(reader.Get(), 1920, 1080, &luma);
    if (FAILED(hr)) {
        std::printf("SKIP: registered camera not available (0x%08lX): shared-memory checks not run\n", static_cast<unsigned long>(hr));
        if (camera) HitCam_VirtualCameraStop(camera);
        return;
    }

    HANDLE mapping = nullptr;
    hitcam::SharedHeader* header = nullptr;
    for (int attempt = 0; attempt < 50 && !header; ++attempt) {
        header = hitcam::MapSharedFrames(&mapping, hitcam::SharedAccess::Write);
        if (!header) Sleep(100);
    }
    Check(header != nullptr, "writer mapped the frame server's section", header ? 1 : 0, 1);
    if (!header) {
        HitCam_VirtualCameraStop(camera);
        return;
    }
    const std::wstring sddl = SectionSddl(mapping);
    std::wprintf(L"  section: %s\n  frame server SID: %s\n", sddl.c_str(), hitcam::FrameServerSid());
    Check(sddl.find(L";;;IU)") == std::wstring::npos && sddl.find(hitcam::FrameServerSid()) != std::wstring::npos,
          "writer restricted the DACL (no interactive users, frame server kept)");

    // A read-only view cannot be written through.
    {
        HANDLE readMapping = nullptr;
        hitcam::SharedHeader* readHeader = hitcam::MapSharedFrames(&readMapping, hitcam::SharedAccess::Read);
        MEMORY_BASIC_INFORMATION info{};
        const bool readOnly = readHeader && VirtualQuery(readHeader, &info, sizeof(info)) && info.Protect == PAGE_READONLY;
        Check(readOnly, "reader view is read-only", readHeader ? info.Protect : 0, PAGE_READONLY);
        hitcam::UnmapSharedFrames(readHeader, readMapping);
    }

    ComPtr<IMFActivate> activate;
    ComPtr<IMFMediaSource> source;
    ComPtr<IMFMediaStream> stream;
    hr = CreateLocalActivate(&activate);
    if (SUCCEEDED(hr)) hr = activate->ActivateObject(IID_PPV_ARGS(&source));
    if (SUCCEEDED(hr)) hr = StartStream(source.Get(), 640, 360, &stream);
    Check(SUCCEEDED(hr), "local source started", hr, 0);

    if (SUCCEEDED(hr)) {
        Publish(header, 1280, 720, 200);
        for (int i = 0; i < 20 && SUCCEEDED(hr) && std::abs(luma - 200) > 4; ++i) {
            hr = RequestLuma(stream.Get(), 640, 360, &luma);
            Publish(header, 1280, 720, 200);
        }
        Check(std::abs(luma - 200) < 4, "published frame reaches the local source", luma, 200);

        // The writer is "in the middle of a frame" (odd sequence) but still alive: the last frame is repeated.
        InterlockedIncrement64(&header->sequence);
        InterlockedExchange64(&header->updatedMs, static_cast<LONG64>(GetTickCount64()));
        const ULONGLONG before = GetTickCount64();
        hr = RequestLuma(stream.Get(), 640, 360, &luma);
        Check(SUCCEEDED(hr) && std::abs(luma - 200) < 4, "writer busy: last frame repeated, not \"no signal\"", luma, 200);
        std::printf("  sample while busy took %llu ms\n", GetTickCount64() - before);

        // Stuck writer: after kStaleAfterMs the camera shows "no signal".
        Sleep(static_cast<DWORD>(hitcam::kStaleAfterMs) + 200);
        hr = RequestLuma(stream.Get(), 640, 360, &luma);
        Check(SUCCEEDED(hr) && std::abs(luma - 32) < 1, "stale frame: no signal", luma, 32);
        InterlockedIncrement64(&header->sequence);

        Publish(header, 1920, 1080, 100);
        hr = RequestLuma(stream.Get(), 640, 360, &luma);
        Check(SUCCEEDED(hr) && std::abs(luma - 100) < 4, "new frame after the stall", luma, 100);

        // Cleared (phone disconnected): no signal at once.
        InterlockedIncrement64(&header->sequence);
        header->width = 0;
        header->height = 0;
        InterlockedIncrement64(&header->sequence);
        hr = RequestLuma(stream.Get(), 640, 360, &luma);
        Check(SUCCEEDED(hr) && std::abs(luma - 32) < 1, "cleared: no signal", luma, 32);
    }
    if (source) source->Shutdown();
    stream.Reset();
    source.Reset();
    activate.Reset();

    // The frame server must still be able to open the section after the DACL change: close the camera (its source
    // unmaps; this process keeps the section alive), open it again and check a frame comes through.
    reader.Reset();
    HitCam_VirtualCameraStop(camera);
    camera = nullptr;
    Sleep(500);
    hr = HitCam_VirtualCameraStart(L"HitCam Source Test", &camera);
    if (SUCCEEDED(hr)) hr = OpenCamera(L"HitCam Source Test", &reader);
    bool through = false;
    for (int round = 0; round < 40 && SUCCEEDED(hr) && !through; ++round) {
        Publish(header, 1280, 720, 90);
        hr = ReadAverageLuma(reader.Get(), 1920, 1080, &luma);
        through = std::abs(luma - 90) < 6;
        if (!through) Sleep(100);
    }
    Check(through, "frame server reopened the restricted section (camera shows the frame)", luma, 90);

    hitcam::UnmapSharedFrames(header, mapping);
    reader.Reset();
    if (camera) HitCam_VirtualCameraStop(camera);
}

}  // namespace source_check

int RunSourceCheck() {
    source_check::CheckLocalSource();
    source_check::CheckSharedFrames();
    return source_check::g_passed ? 0 : 1;
}

#include "DShowCheck.inl"
#include "ProcessCheck.inl"
#include "OverlayCheck.inl"
#include "ShotCheck.inl"

int main(int argc, char** argv) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) return Fail("MFStartup", hr);

    // --list: only print the cameras apps can see.
    if (argc > 1 && std::strcmp(argv[1], "--list") == 0) {
        ComPtr<IMFSourceReader> none;
        OpenCamera(L"|no camera has this name|", &none);
        return 0;
    }
    if (argc > 1 && std::strcmp(argv[1], "--process") == 0) return process_check::Run();
    if (argc > 1 && std::strcmp(argv[1], "--overlay") == 0) return overlay_check::Run();
    if (argc > 1 && std::strcmp(argv[1], "--shot") == 0) return shot_check::Run();
    if (argc > 1 && std::strcmp(argv[1], "--source") == 0) return RunSourceCheck();
    if (argc > 1 && std::strcmp(argv[1], "--dshow") == 0) return dshow_check::Run(false);
    if (argc > 1 && std::strcmp(argv[1], "--dshow-installed") == 0) return dshow_check::Run(true);

    // Diagnostics: the registered class is an IMFActivate that creates the media source (as the frame server does).
    {
        ComPtr<IMFActivate> activate;
        hr = CoCreateInstance(hitcam::kMediaSourceClsid, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&activate));
        if (FAILED(hr)) return Fail("CoCreateInstance(IMFActivate)", hr);
        ComPtr<IMFMediaSource> source;
        hr = activate->ActivateObject(IID_PPV_ARGS(&source));
        if (FAILED(hr)) return Fail("ActivateObject", hr);
        ComPtr<IMFPresentationDescriptor> descriptor;
        hr = source->CreatePresentationDescriptor(&descriptor);
        if (FAILED(hr)) return Fail("CreatePresentationDescriptor", hr);
        source->Shutdown();
        std::printf("OK: media source created in-process\n");
    }
    {
        ComPtr<IMFVirtualCamera> probe;
        hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
                                   MFVirtualCameraAccess_CurrentUser, L"HitCam Probe", hitcam::kMediaSourceClsidString,
                                   nullptr, 0, &probe);
        if (FAILED(hr)) return Fail("MFCreateVirtualCamera", hr);
        hr = probe->Start(nullptr);
        if (FAILED(hr)) return Fail("IMFVirtualCamera::Start", hr);
        probe->Remove();
        probe->Shutdown();
        std::printf("OK: probe camera started\n");
    }

    void* camera = nullptr;
    hr = HitCam_VirtualCameraStart(L"HitCam Test", &camera);
    if (FAILED(hr)) return Fail("HitCam_VirtualCameraStart", hr);
    std::printf("OK: virtual camera added\n");

    ComPtr<IMFSourceReader> reader;
    hr = OpenCamera(L"HitCam Test", &reader);
    if (FAILED(hr)) return Fail("open camera", hr);

    ComPtr<IMFMediaType> type;
    hr = reader->GetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), &type);
    if (FAILED(hr)) return Fail("GetCurrentMediaType", hr);
    UINT32 width = 0, height = 0;
    MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height);
    std::printf("OK: opened, %ux%u\n", width, height);

    double luma = 0;
    hr = ReadAverageLuma(reader.Get(), width, height, &luma);
    if (FAILED(hr)) return Fail("read no-signal frame", hr);
    std::printf("OK: no-signal frame, average luma %.1f\n", luma);

    // The source created the shared section when it started; publish a bright test frame into it.
    HANDLE mapping = nullptr;
    hitcam::SharedHeader* header = nullptr;
    for (int attempt = 0; attempt < 50 && !header; ++attempt) {
        header = hitcam::MapSharedFrames(&mapping);
        if (!header) Sleep(100);
    }
    if (!header) return Fail("open shared memory", HRESULT_FROM_WIN32(GetLastError()));

    constexpr uint32_t kTestWidth = 1280, kTestHeight = 720;
    constexpr uint8_t kTestLuma = 200;
    bool passed = false;
    for (int round = 0; round < 20 && !passed; ++round) {
        InterlockedIncrement64(&header->sequence);
        MemoryBarrier();
        std::memset(hitcam::FrameData(header), kTestLuma, kTestWidth * kTestHeight);
        std::memset(hitcam::FrameData(header) + kTestWidth * kTestHeight, 128, kTestWidth * kTestHeight / 2);
        header->width = kTestWidth;
        header->height = kTestHeight;
        InterlockedExchange64(&header->updatedMs, static_cast<LONG64>(GetTickCount64()));
        MemoryBarrier();
        InterlockedIncrement64(&header->sequence);

        hr = ReadAverageLuma(reader.Get(), width, height, &luma);
        if (FAILED(hr)) return Fail("read test frame", hr);
        passed = luma > kTestLuma - 10 && luma < kTestLuma + 10;
    }
    std::printf("%s: test frame, average luma %.1f (expected %u)\n", passed ? "OK" : "FAIL", luma, kTestLuma);

    // Decoder path: real H.264 through HitCam_BridgeDecode, as HitCam.exe does with the phone's stream.
    if (passed) {
        constexpr uint8_t kEncodedLuma = 120;
        TestEncoder encoder;
        hr = encoder.Initialize(1920, 1080);
        if (FAILED(hr)) return Fail("H.264 encoder", hr);
        void* bridge = nullptr;
        hr = HitCam_BridgeCreate(&bridge);
        if (FAILED(hr)) return Fail("HitCam_BridgeCreate", hr);

        passed = false;
        int decoded = 0;
        for (int round = 0; round < 60 && !passed; ++round) {
            std::vector<std::vector<uint8_t>> units;
            hr = encoder.Encode(kEncodedLuma, units);
            if (FAILED(hr)) return Fail("encode", hr);
            for (const auto& unit : units) {
                hr = HitCam_BridgeDecode(bridge, unit.data(), static_cast<uint32_t>(unit.size()), round * 333'333LL);
                if (FAILED(hr)) return Fail("HitCam_BridgeDecode", hr);
                if (hr == S_OK) ++decoded;
            }
            if (decoded == 0) continue;
            hr = ReadAverageLuma(reader.Get(), width, height, &luma);
            if (FAILED(hr)) return Fail("read decoded frame", hr);
            passed = luma > kEncodedLuma - 6 && luma < kEncodedLuma + 6;
        }
        std::printf("%s: decoded H.264 (%d frames), average luma %.1f (expected %u)\n", passed ? "OK" : "FAIL", decoded, luma, kEncodedLuma);

        // The app window's preview: downscaled BGRA of the same frame. Luma 120, neutral chroma -> gray ~121.
        uint32_t previewWidth = 0, previewHeight = 0;
        uint64_t previewFrame = 0;
        HitCam_BridgePreviewInfo(bridge, &previewWidth, &previewHeight, &previewFrame);
        std::vector<uint8_t> pixels(static_cast<size_t>(previewWidth) * previewHeight * 4);
        const bool copied = previewWidth > 0 && HitCam_BridgeCopyPreview(bridge, pixels.data(), previewWidth * 4, previewWidth, previewHeight);
        const uint8_t* center = pixels.data() + (static_cast<size_t>(previewHeight / 2) * previewWidth + previewWidth / 2) * 4;
        const bool previewOk = copied && previewWidth == 960 && previewHeight == 540 && previewFrame > 0
                               && center[0] > 110 && center[0] < 132 && center[1] > 110 && center[1] < 132 && center[2] > 110 && center[2] < 132;
        std::printf("%s: preview %ux%u, frame %llu, center BGR %u,%u,%u (expected ~121)\n", previewOk ? "OK" : "FAIL",
                    previewWidth, previewHeight, static_cast<unsigned long long>(previewFrame),
                    copied ? center[0] : 0, copied ? center[1] : 0, copied ? center[2] : 0);
        passed = passed && previewOk;
        HitCam_BridgeDestroy(bridge);
    }

    hitcam::UnmapSharedFrames(header, mapping);
    reader.Reset();
    HitCam_VirtualCameraStop(camera);
    MFShutdown();
    return passed ? 0 : 1;
}
