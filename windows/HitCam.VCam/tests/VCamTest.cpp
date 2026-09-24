// End-to-end check of the installed virtual camera without a phone:
// adds the camera, opens it like a video app would, checks that frames written to shared memory come out,
// then sends real H.264 through the decoder bridge and checks the decoded picture reaches the camera.
// Requires the COM class to be registered (HitCam: "Install camera", or regsvr32 as admin).

#include <windows.h>
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
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../src/Shared.h"

using Microsoft::WRL::ComPtr;

extern "C" HRESULT __stdcall HitCam_VirtualCameraStart(const wchar_t* friendlyName, void** handle);
extern "C" void __stdcall HitCam_VirtualCameraStop(void* handle);
extern "C" HRESULT __stdcall HitCam_BridgeCreate(void** handle);
extern "C" HRESULT __stdcall HitCam_BridgeDecode(void* handle, const uint8_t* data, uint32_t length, int64_t timestamp);
extern "C" void __stdcall HitCam_BridgeDestroy(void* handle);
extern "C" void __stdcall HitCam_BridgePreviewInfo(void* handle, uint32_t* width, uint32_t* height, uint64_t* frame);
extern "C" BOOL __stdcall HitCam_BridgeCopyPreview(void* handle, uint8_t* destination, uint32_t stride, uint32_t width, uint32_t height);
extern "C" BOOL __stdcall HitCam_DenoiseAvailable();
extern "C" void __stdcall HitCam_BridgeSetDenoise(void* handle, float strength);
extern "C" void __stdcall HitCam_BridgeDenoiseStats(void* handle, double* milliseconds, int* error, double* noise, float* amount);

namespace {

int Fail(const char* step, HRESULT hr) {
    std::printf("FAIL: %s (0x%08lX)\n", step, static_cast<unsigned long>(hr));
    return 1;
}

// Produces real H.264 (Annex-B, like the iPhone sends) with the Windows software encoder.
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

// Mean and standard deviation of the green channel of the bridge's BGRA preview.
bool PreviewStats(void* bridge, double* mean, double* deviation) {
    uint32_t width = 0, height = 0;
    uint64_t frame = 0;
    HitCam_BridgePreviewInfo(bridge, &width, &height, &frame);
    if (width == 0) return false;
    std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
    if (!HitCam_BridgeCopyPreview(bridge, pixels.data(), width * 4, width, height)) return false;
    double sum = 0, squares = 0;
    const size_t count = static_cast<size_t>(width) * height;
    for (size_t i = 0; i < count; ++i) {
        const double green = pixels[i * 4 + 1];
        sum += green;
        squares += green * green;
    }
    *mean = sum / count;
    *deviation = std::sqrt(std::max(0.0, squares / count - *mean * *mean));
    return true;
}

// Decodes a textured scene through a fresh bridge and returns the settled BGRA preview.
bool RenderScene(int noise, float denoise, std::vector<uint8_t>& pixels, uint32_t& width, uint32_t& height, double& milliseconds, double& noiseLevel, float& applied) {
    TestEncoder encoder;
    encoder.UseScene();
    void* bridge = nullptr;
    if (FAILED(encoder.Initialize(1920, 1080, 30'000'000)) || FAILED(HitCam_BridgeCreate(&bridge))) return false;
    HitCam_BridgeSetDenoise(bridge, denoise);

    int64_t time = 0;
    int settled = 0;
    const ULONGLONG deadline = GetTickCount64() + 60'000;
    milliseconds = -1;
    while (GetTickCount64() < deadline && settled < 15) {
        std::vector<std::vector<uint8_t>> units;
        if (FAILED(encoder.Encode(0, units, noise))) break;
        for (const auto& unit : units) HitCam_BridgeDecode(bridge, unit.data(), static_cast<uint32_t>(unit.size()), time += 333'333);
        int error = 0;
        float amount = 0;
        HitCam_BridgeDenoiseStats(bridge, &milliseconds, &error, &noiseLevel, &amount);
        if (error != 0) break;
        uint64_t frame = 0;
        HitCam_BridgePreviewInfo(bridge, &width, &height, &frame);
        // Count frames once decoding runs and, with denoise, once the model runs or it decided nothing is needed.
        if (frame > 0 && (denoise <= 0 || milliseconds >= 0 || (noiseLevel >= 0 && amount < 0.02f && frame > 30))) ++settled;
        else Sleep(20);
    }
    pixels.assign(static_cast<size_t>(width) * height * 4, 0);
    {
        int error = 0;
        HitCam_BridgeDenoiseStats(bridge, &milliseconds, &error, &noiseLevel, &applied);
    }
    const bool ok = settled >= 15 && HitCam_BridgeCopyPreview(bridge, pixels.data(), width * 4, width, height);
    HitCam_BridgeDestroy(bridge);
    return ok;
}

// --denoise-scene: does noise removal keep colour and detail on a textured picture (and not damage a clean one)?
int RunSceneCheck() {
    struct Case { const char* name; int noise; float denoise; };
    const Case cases[] = {
        {"clean", 0, 0}, {"light noise", 4, 0}, {"light noise + 100%", 4, 1.0f}, {"noisy", 12, 0}, {"noisy + 100%", 12, 1.0f},
        {"dark room", 30, 0}, {"dark room + 100%", 30, 1.0f}, {"clean + 100%", 0, 1.0f},
    };
    std::vector<uint8_t> reference;
    std::printf("%-20s %6s %6s %6s %6s %8s %7s %6s %8s\n", "case", "diff", "dB", "dG", "dR", "texture", "smooth", "noise", "applied");
    for (const auto& c : cases) {
        std::vector<uint8_t> pixels;
        uint32_t width = 0, height = 0;
        double milliseconds = -1, noiseLevel = -1;
        float applied = 0;
        if (!RenderScene(c.noise, c.denoise, pixels, width, height, milliseconds, noiseLevel, applied)) {
            std::printf("%-22s FAILED\n", c.name);
            return 1;
        }
        if (reference.empty()) reference = pixels;

        // diff: mean abs difference to the clean frame; dB/dG/dR: mean colour shift on the gradient half;
        // texture: mean horizontal step on the striped half (detail); smooth: the same on the gradient half (noise).
        double diff = 0, shift[3] = {}, texture = 0, smooth = 0;
        size_t leftCount = 0, rightCount = 0;
        for (uint32_t y = 0; y < height; ++y) {
            for (uint32_t x = 0; x + 1 < width; ++x) {
                const size_t i = (static_cast<size_t>(y) * width + x) * 4;
                for (int ch = 0; ch < 3; ++ch) diff += std::abs(pixels[i + ch] - reference[i + ch]);
                const double step = std::abs(pixels[i + 4 + 1] - pixels[i + 1]);
                if (x < width / 2 - 1) {
                    for (int ch = 0; ch < 3; ++ch) shift[ch] += pixels[i + ch] - reference[i + ch];
                    smooth += step;
                    ++leftCount;
                } else if (x > width / 2 + 1) {
                    texture += step;
                    ++rightCount;
                }
            }
        }
        const double all = static_cast<double>(width - 1) * height * 3;
        std::printf("%-20s %6.2f %6.2f %6.2f %6.2f %8.2f %7.2f %6.1f %7.0f%%\n", c.name, diff / all, shift[0] / leftCount,
                    shift[1] / leftCount, shift[2] / leftCount, texture / rightCount, smooth / leftCount, noiseLevel,
                    milliseconds >= 0 ? applied * 100 : 0.0f);
    }
    return 0;
}

// --denoise: NVIDIA AI noise removal on real H.264 with synthetic sensor noise; no camera needed.
int RunDenoiseCheck() {
    if (!HitCam_DenoiseAvailable()) {
        std::printf("SKIP: NVIDIA Video Effects runtime is not installed\n");
        return 0;
    }
    TestEncoder encoder;
    HRESULT hr = encoder.Initialize(1920, 1080, 30'000'000);
    if (FAILED(hr)) return Fail("H.264 encoder", hr);
    void* bridge = nullptr;
    hr = HitCam_BridgeCreate(&bridge);
    if (FAILED(hr)) return Fail("HitCam_BridgeCreate", hr);

    constexpr uint8_t kLuma = 120;
    constexpr int kNoise = 24;
    int64_t time = 0;
    auto feed = [&](int frames) -> HRESULT {
        for (int i = 0; i < frames; ++i) {
            std::vector<std::vector<uint8_t>> units;
            HRESULT result = encoder.Encode(kLuma, units, kNoise);
            for (const auto& unit : units) {
                if (SUCCEEDED(result)) result = HitCam_BridgeDecode(bridge, unit.data(), static_cast<uint32_t>(unit.size()), time += 333'333);
            }
            if (FAILED(result)) return result;
        }
        return S_OK;
    };

    double noisyMean = 0, noisyDeviation = 0;
    // The Windows encoder and decoder hold a few frames at the start.
    bool decoded = false;
    for (int round = 0; round < 60 && !decoded; ++round) {
        hr = feed(1);
        if (FAILED(hr)) return Fail("decode noisy frames", hr);
        decoded = PreviewStats(bridge, &noisyMean, &noisyDeviation);
    }
    if (!decoded) return Fail("no decoded frame", E_FAIL);
    hr = feed(5);
    PreviewStats(bridge, &noisyMean, &noisyDeviation);
    std::printf("OK: without denoise: mean %.1f, noise (std dev) %.1f\n", noisyMean, noisyDeviation);

    bool passed = true;
    for (const float strength : {1.0f, 0.5f}) {
        HitCam_BridgeSetDenoise(bridge, strength);
        double milliseconds = -1;
        int error = 0;
        double noiseLevel = -1;
        float amount = 0;
        // The model loads in the background (TensorRT); frames pass through meanwhile.
        const ULONGLONG deadline = GetTickCount64() + 60'000;
        while (GetTickCount64() < deadline) {
            hr = feed(3);
            if (FAILED(hr)) return Fail("decode while loading", hr);
            HitCam_BridgeDenoiseStats(bridge, &milliseconds, &error, &noiseLevel, &amount);
            if (error != 0) break;
            if (milliseconds >= 0) {
                hr = feed(10);  // let the temporal model settle
                break;
            }
            Sleep(50);
        }
        HitCam_BridgeDenoiseStats(bridge, &milliseconds, &error, &noiseLevel, &amount);
        double mean = 0, deviation = 0;
        const bool ok = error == 0 && milliseconds >= 0 && PreviewStats(bridge, &mean, &deviation)
                        && deviation < noisyDeviation * 0.7 && std::abs(mean - noisyMean) < 4;
        std::printf("%s: denoise %.1f: mean %.1f, noise %.1f (was %.1f), %.1f ms per frame, NvCV status %d\n",
                    ok ? "OK" : "FAIL", strength, mean, deviation, noisyDeviation, milliseconds, error);
        passed = passed && ok;
    }

    HitCam_BridgeDestroy(bridge);
    return passed ? 0 : 1;
}

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
    if (argc > 1 && std::strcmp(argv[1], "--denoise") == 0) return RunDenoiseCheck();
    if (argc > 1 && std::strcmp(argv[1], "--denoise-scene") == 0) return RunSceneCheck();

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
