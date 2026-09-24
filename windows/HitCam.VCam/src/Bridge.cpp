// Used by HitCam.exe (through P/Invoke): decodes the phone's H.264 stream, publishes frames to the
// media source through shared memory, and registers the virtual camera with Windows.

#include <windows.h>
#include <codecapi.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mfvirtualcamera.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstring>
#include <mutex>
#include <vector>

#include "Denoiser.h"
#include "Shared.h"

using Microsoft::WRL::ComPtr;

namespace hitcam {
namespace {

// Publishes decoded NV12 frames; silently does nothing until the section exists.
class FrameWriter {
public:
    ~FrameWriter() { UnmapSharedFrames(header_, mapping_); }

    bool IsMapped() { return EnsureMapped(); }

    void Publish(const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height) {
        if (!EnsureMapped() || width > kMaxSide || height > kMaxSide) return;
        width &= ~1u;
        height &= ~1u;
        BeginWrite();
        uint8_t* target = FrameData(header_);
        for (uint32_t y = 0; y < height; ++y) std::memcpy(target + static_cast<size_t>(y) * width, luma + static_cast<size_t>(y) * pitch, width);
        target += static_cast<size_t>(width) * height;
        for (uint32_t y = 0; y < height / 2; ++y) std::memcpy(target + static_cast<size_t>(y) * width, chroma + static_cast<size_t>(y) * pitch, width);
        header_->width = width;
        header_->height = height;
        EndWrite();
    }

    void ClearSignal() {
        if (!header_) return;
        BeginWrite();
        header_->width = 0;
        header_->height = 0;
        EndWrite();
    }

private:
    bool EnsureMapped() {
        if (header_) return true;
        const ULONGLONG now = GetTickCount64();
        if (now < nextMapAttemptMs_) return false;
        nextMapAttemptMs_ = now + 1000;
        header_ = MapSharedFrames(&mapping_);
        return header_ != nullptr;
    }

    void BeginWrite() {
        InterlockedIncrement64(&header_->sequence);
        MemoryBarrier();
    }

    void EndWrite() {
        InterlockedExchange64(&header_->updatedMs, static_cast<LONG64>(GetTickCount64()));
        MemoryBarrier();
        InterlockedIncrement64(&header_->sequence);
    }

    SharedHeader* header_ = nullptr;
    HANDLE mapping_ = nullptr;
    ULONGLONG nextMapAttemptMs_ = 0;
};

// A downscaled BGRA copy of the newest frame for the app's own window (decoder thread writes, UI thread reads).
class PreviewBuffer {
public:
    static constexpr uint32_t kMaxWidth = 960;
    static constexpr uint32_t kMaxHeight = 960;

    void Update(const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height) {
        if (width < 2 || height < 2) return;
        const double scale = std::min({1.0, static_cast<double>(kMaxWidth) / width, static_cast<double>(kMaxHeight) / height,
                                       // Landscape frames are shown at most 960x540.
                                       width >= height ? 540.0 / height : 1.0});
        const uint32_t outWidth = std::max<uint32_t>(2, static_cast<uint32_t>(width * scale));
        const uint32_t outHeight = std::max<uint32_t>(2, static_cast<uint32_t>(height * scale));

        std::lock_guard guard(lock_);
        pixels_.resize(static_cast<size_t>(outWidth) * outHeight * 4);
        for (uint32_t y = 0; y < outHeight; ++y) {
            const uint32_t sourceY = static_cast<uint32_t>(static_cast<uint64_t>(y) * height / outHeight);
            const uint8_t* lumaRow = luma + static_cast<size_t>(sourceY) * pitch;
            const uint8_t* chromaRow = chroma + static_cast<size_t>(sourceY / 2) * pitch;
            uint8_t* out = pixels_.data() + static_cast<size_t>(y) * outWidth * 4;
            for (uint32_t x = 0; x < outWidth; ++x) {
                const uint32_t sourceX = static_cast<uint32_t>(static_cast<uint64_t>(x) * width / outWidth);
                // BT.709, video range (what iPhone H.264 uses for HD).
                const int c = 298 * (lumaRow[sourceX] - 16);
                const int d = chromaRow[sourceX & ~1u] - 128;
                const int e = chromaRow[(sourceX & ~1u) + 1] - 128;
                out[x * 4 + 0] = Clamp((c + 541 * d + 128) >> 8);
                out[x * 4 + 1] = Clamp((c - 55 * d - 136 * e + 128) >> 8);
                out[x * 4 + 2] = Clamp((c + 459 * e + 128) >> 8);
                out[x * 4 + 3] = 255;
            }
        }
        width_ = outWidth;
        height_ = outHeight;
        ++frame_;
    }

    void Info(uint32_t* width, uint32_t* height, uint64_t* frame) {
        std::lock_guard guard(lock_);
        *width = width_;
        *height = height_;
        *frame = frame_;
    }

    // Copies the frame when it still has the given size; returns false if it changed meanwhile.
    bool Copy(uint8_t* destination, uint32_t stride, uint32_t width, uint32_t height) {
        std::lock_guard guard(lock_);
        if (width != width_ || height != height_ || pixels_.empty() || stride < width * 4) return false;
        for (uint32_t y = 0; y < height; ++y) {
            std::memcpy(destination + static_cast<size_t>(y) * stride, pixels_.data() + static_cast<size_t>(y) * width * 4, width * 4);
        }
        return true;
    }

private:
    static uint8_t Clamp(int value) { return static_cast<uint8_t>(value < 0 ? 0 : value > 255 ? 255 : value); }

    std::mutex lock_;
    std::vector<uint8_t> pixels_;
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    uint64_t frame_ = 0;
};

// Every decoded frame goes (optionally denoised) to the virtual camera and to the preview.
struct FrameSink {
    FrameWriter writer;
    PreviewBuffer preview;
    Denoiser denoiser;
    std::vector<uint8_t> packed;

    void Publish(const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height) {
        width &= ~1u;
        height &= ~1u;
        if (denoiser.IsEnabled()) {
            // The effect needs one contiguous NV12 image; the decoder's has padding rows between the planes.
            packed.resize(static_cast<size_t>(width) * height * 3 / 2);
            uint8_t* out = packed.data();
            for (uint32_t y = 0; y < height; ++y) std::memcpy(out + static_cast<size_t>(y) * width, luma + static_cast<size_t>(y) * pitch, width);
            out += static_cast<size_t>(width) * height;
            for (uint32_t y = 0; y < height / 2; ++y) std::memcpy(out + static_cast<size_t>(y) * width, chroma + static_cast<size_t>(y) * pitch, width);
            if (denoiser.Process(packed.data(), width, height)) {
                luma = packed.data();
                chroma = packed.data() + static_cast<size_t>(width) * height;
                pitch = width;
            }
        }
        writer.Publish(luma, chroma, pitch, width, height);
        preview.Update(luma, chroma, pitch, width, height);
    }

    void ClearSignal() { writer.ClearSignal(); }
};

class Decoder {
public:
    HRESULT Initialize() {
        HRESULT hr = CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&transform_));
        if (FAILED(hr)) return hr;

        ComPtr<IMFAttributes> attributes;
        if (SUCCEEDED(transform_->GetAttributes(&attributes))) attributes->SetUINT32(MF_LOW_LATENCY, TRUE);
        ComPtr<ICodecAPI> codec;
        if (SUCCEEDED(transform_.As(&codec))) {
            VARIANT value;
            VariantInit(&value);
            value.vt = VT_UI4;
            value.ulVal = TRUE;
            codec->SetValue(&CODECAPI_AVLowLatencyMode, &value);
        }

        ComPtr<IMFMediaType> input;
        hr = MFCreateMediaType(&input);
        if (SUCCEEDED(hr)) hr = input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        if (SUCCEEDED(hr)) hr = input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = transform_->SetInputType(0, input.Get(), 0);
        if (SUCCEEDED(hr)) hr = SelectOutputType();
        if (SUCCEEDED(hr)) hr = transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        if (SUCCEEDED(hr)) hr = transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        return hr;
    }

    // Returns S_OK when a frame was published, S_FALSE when the decoder needs more data.
    HRESULT Decode(const uint8_t* data, uint32_t length, int64_t timestamp, FrameSink& writer) {
        ComPtr<IMFMediaBuffer> buffer;
        HRESULT hr = MFCreateMemoryBuffer(length, &buffer);
        if (FAILED(hr)) return hr;
        BYTE* target = nullptr;
        hr = buffer->Lock(&target, nullptr, nullptr);
        if (FAILED(hr)) return hr;
        std::memcpy(target, data, length);
        buffer->Unlock();
        buffer->SetCurrentLength(length);

        ComPtr<IMFSample> sample;
        hr = MFCreateSample(&sample);
        if (SUCCEEDED(hr)) hr = sample->AddBuffer(buffer.Get());
        if (SUCCEEDED(hr)) hr = sample->SetSampleTime(timestamp);
        if (FAILED(hr)) return hr;

        hr = transform_->ProcessInput(0, sample.Get(), 0);
        if (hr == MF_E_NOTACCEPTING) {
            // Drain pending output first, then retry once.
            Drain(writer);
            hr = transform_->ProcessInput(0, sample.Get(), 0);
        }
        if (FAILED(hr)) return hr;
        return Drain(writer);
    }

private:
    HRESULT SelectOutputType() {
        for (DWORD index = 0;; ++index) {
            ComPtr<IMFMediaType> type;
            HRESULT hr = transform_->GetOutputAvailableType(0, index, &type);
            if (FAILED(hr)) return hr;
            GUID subtype{};
            if (SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) && subtype == MFVideoFormat_NV12) {
                hr = transform_->SetOutputType(0, type.Get(), 0);
                if (FAILED(hr)) return hr;
                ReadOutputFormat(type.Get());
                return S_OK;
            }
        }
    }

    void ReadOutputFormat(IMFMediaType* type) {
        UINT32 width = 0, height = 0;
        MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height);
        frameWidth_ = width;
        frameHeight_ = height;
        visibleWidth_ = width;
        visibleHeight_ = height;
        MFVideoArea area{};
        if (SUCCEEDED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), nullptr))) {
            visibleWidth_ = static_cast<uint32_t>(area.Area.cx);
            visibleHeight_ = static_cast<uint32_t>(area.Area.cy);
        }
        stride_ = MFGetAttributeUINT32(type, MF_MT_DEFAULT_STRIDE, width);
    }

    HRESULT Drain(FrameSink& writer) {
        HRESULT result = S_FALSE;
        while (true) {
            MFT_OUTPUT_STREAM_INFO info{};
            HRESULT hr = transform_->GetOutputStreamInfo(0, &info);
            if (FAILED(hr)) return hr;

            ComPtr<IMFSample> sample;
            const bool providesSamples = (info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
            if (!providesSamples) {
                ComPtr<IMFMediaBuffer> buffer;
                hr = MFCreateMemoryBuffer(info.cbSize, &buffer);
                if (SUCCEEDED(hr)) hr = MFCreateSample(&sample);
                if (SUCCEEDED(hr)) hr = sample->AddBuffer(buffer.Get());
                if (FAILED(hr)) return hr;
            }

            MFT_OUTPUT_DATA_BUFFER output{};
            output.pSample = sample.Get();
            DWORD status = 0;
            hr = transform_->ProcessOutput(0, 1, &output, &status);
            ComPtr<IMFSample> produced;
            produced.Attach(providesSamples ? output.pSample : nullptr);
            if (output.pEvents) output.pEvents->Release();

            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return result;
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
                hr = SelectOutputType();
                if (FAILED(hr)) return hr;
                continue;
            }
            if (FAILED(hr)) return hr;

            Publish(providesSamples ? produced.Get() : sample.Get(), writer);
            result = S_OK;
        }
    }

    void Publish(IMFSample* sample, FrameSink& writer) {
        ComPtr<IMFMediaBuffer> buffer;
        if (FAILED(sample->ConvertToContiguousBuffer(&buffer))) return;

        ComPtr<IMF2DBuffer> buffer2d;
        BYTE* data = nullptr;
        LONG pitch = 0;
        if (SUCCEEDED(buffer.As(&buffer2d)) && SUCCEEDED(buffer2d->Lock2D(&data, &pitch))) {
            if (pitch > 0) writer.Publish(data, data + static_cast<size_t>(pitch) * frameHeight_, static_cast<uint32_t>(pitch), visibleWidth_, visibleHeight_);
            buffer2d->Unlock2D();
            return;
        }

        DWORD length = 0;
        if (SUCCEEDED(buffer->Lock(&data, nullptr, &length))) {
            const uint32_t stride = stride_ ? stride_ : frameWidth_;
            if (length >= static_cast<DWORD>(stride) * frameHeight_ * 3 / 2) {
                writer.Publish(data, data + static_cast<size_t>(stride) * frameHeight_, stride, visibleWidth_, visibleHeight_);
            }
            buffer->Unlock();
        }
    }

    ComPtr<IMFTransform> transform_;
    uint32_t frameWidth_ = 0;
    uint32_t frameHeight_ = 0;
    uint32_t visibleWidth_ = 0;
    uint32_t visibleHeight_ = 0;
    uint32_t stride_ = 0;
};

struct Bridge {
    bool comInitialized = false;
    FrameSink sink;
    Decoder decoder;
};

}  // namespace
}  // namespace hitcam

extern "C" {

__declspec(dllexport) void __stdcall HitCam_BridgeDestroy(void* handle);

// Creates the decoder. Call from one thread; all Bridge functions for a handle must stay on it.
__declspec(dllexport) HRESULT __stdcall HitCam_BridgeCreate(void** handle) {
    if (!handle) return E_POINTER;
    *handle = nullptr;
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;
    auto* bridge = new (std::nothrow) hitcam::Bridge();
    if (!bridge) {
        MFShutdown();
        return E_OUTOFMEMORY;
    }
    // The decoder is a COM object; the calling thread may not have joined an apartment yet.
    bridge->comInitialized = SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
    hr = bridge->decoder.Initialize();
    if (FAILED(hr)) {
        HitCam_BridgeDestroy(bridge);
        return hr;
    }
    *handle = bridge;
    return S_OK;
}

// Decodes one Annex-B access unit. Failure means the decoder needs a new keyframe.
__declspec(dllexport) HRESULT __stdcall HitCam_BridgeDecode(void* handle, const uint8_t* data, uint32_t length, int64_t timestamp) {
    if (!handle || !data || length == 0) return E_INVALIDARG;
    auto* bridge = static_cast<hitcam::Bridge*>(handle);
    return bridge->decoder.Decode(data, length, timestamp, bridge->sink);
}

// Shows "no signal" in the camera right away instead of waiting for the stale timeout.
__declspec(dllexport) void __stdcall HitCam_BridgeClearSignal(void* handle) {
    if (handle) static_cast<hitcam::Bridge*>(handle)->sink.ClearSignal();
}

// True once frames can reach the camera (an app has opened it at least once since the service started).
__declspec(dllexport) BOOL __stdcall HitCam_BridgeIsLinked(void* handle) {
    return handle && static_cast<hitcam::Bridge*>(handle)->sink.writer.IsMapped();
}

// True if the NVIDIA Video Effects runtime is installed (AI noise removal can be offered).
__declspec(dllexport) BOOL __stdcall HitCam_DenoiseAvailable() {
    return hitcam::Denoiser::IsAvailable();
}

// 0 turns noise removal off; up to 0.5 the gentle model is mixed in, above that the strong one. Any thread.
__declspec(dllexport) void __stdcall HitCam_BridgeSetDenoise(void* handle, float strength) {
    if (handle) static_cast<hitcam::Bridge*>(handle)->sink.denoiser.SetStrength(strength);
}

// Time the last denoised frame took (ms, -1 if none) and the NvCV status of a failed model load (0 if none).
__declspec(dllexport) void __stdcall HitCam_BridgeDenoiseStats(void* handle, double* milliseconds, int* error) {
    if (!handle || !milliseconds || !error) return;
    const auto& denoiser = static_cast<hitcam::Bridge*>(handle)->sink.denoiser;
    *milliseconds = denoiser.LastMilliseconds();
    *error = denoiser.LastError();
}

// Size and number of the newest preview frame (0x0 before the first one). Safe to call from any thread.
__declspec(dllexport) void __stdcall HitCam_BridgePreviewInfo(void* handle, uint32_t* width, uint32_t* height, uint64_t* frame) {
    if (!handle || !width || !height || !frame) return;
    static_cast<hitcam::Bridge*>(handle)->sink.preview.Info(width, height, frame);
}

// Copies the preview as BGRA rows of `stride` bytes; fails if its size is no longer width x height.
__declspec(dllexport) BOOL __stdcall HitCam_BridgeCopyPreview(void* handle, uint8_t* destination, uint32_t stride, uint32_t width, uint32_t height) {
    return handle && destination && static_cast<hitcam::Bridge*>(handle)->sink.preview.Copy(destination, stride, width, height);
}

__declspec(dllexport) void __stdcall HitCam_BridgeDestroy(void* handle) {
    if (!handle) return;
    auto* bridge = static_cast<hitcam::Bridge*>(handle);
    const bool comInitialized = bridge->comInitialized;
    delete bridge;
    MFShutdown();
    if (comInitialized) CoUninitialize();
}

// Registers the camera with Windows for the lifetime of this process. The COM class must be registered (regsvr32).
__declspec(dllexport) HRESULT __stdcall HitCam_VirtualCameraStart(const wchar_t* friendlyName, void** handle) {
    if (!handle) return E_POINTER;
    *handle = nullptr;
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;

    ComPtr<IMFVirtualCamera> camera;
    hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
                               MFVirtualCameraAccess_CurrentUser, friendlyName ? friendlyName : hitcam::kFriendlyName,
                               hitcam::kMediaSourceClsidString, nullptr, 0, &camera);
    if (SUCCEEDED(hr)) hr = camera->Start(nullptr);
    if (FAILED(hr)) {
        if (camera) camera->Shutdown();
        MFShutdown();
        return hr;
    }
    *handle = camera.Detach();
    return S_OK;
}

__declspec(dllexport) void __stdcall HitCam_VirtualCameraStop(void* handle) {
    if (!handle) return;
    auto* camera = static_cast<IMFVirtualCamera*>(handle);
    camera->Remove();
    camera->Shutdown();
    camera->Release();
    MFShutdown();
}

}  // extern "C"
