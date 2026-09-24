#include "Denoiser.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstring>
#include <string>

#include "nvCVImage.h"
#include "nvVideoEffects.h"

// The SDK's loader proxies read this; empty means NV_VIDEO_EFFECTS_PATH or Program Files\NVIDIA Corporation\NVIDIA Video Effects.
char* g_nvVFXSDKPath = nullptr;

namespace hitcam {
namespace {

std::string SdkDirectory() {
    char path[MAX_PATH] = {};
    const DWORD length = GetEnvironmentVariableA("NV_VIDEO_EFFECTS_PATH", path, MAX_PATH);
    if (length > 0 && length < MAX_PATH && std::strcmp(path, "USE_APP_PATH") != 0) return path;
    GetEnvironmentVariableA("ProgramFiles", path, MAX_PATH);
    return std::string(path) + "\\NVIDIA Corporation\\NVIDIA Video Effects";
}

// The SDK has no stream sync of its own, and it links the CUDA runtime statically (no cudart DLL to borrow from).
// The driver API is always there with an NVIDIA driver and takes the same stream handle.
bool SynchronizeStream(CUstream stream) {
    using Sync = int(__stdcall*)(CUstream);
    static const Sync sync = [] {
        const HMODULE driver = LoadLibraryExW(L"nvcuda.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        return driver ? reinterpret_cast<Sync>(GetProcAddress(driver, "cuStreamSynchronize")) : nullptr;
    }();
    return sync && sync(stream) == 0;
}

}  // namespace

struct Denoiser::Effect {
    NvVFX_Handle handle = nullptr;
    NvVFX_StateObjectHandle state = nullptr;
    CUstream stream = nullptr;
    NvCVImage gpuInput, gpuOutput, frame, staging;
    uint32_t width = 0, height = 0;
    unsigned model = 0;

    ~Effect() {
        if (handle && state) NvVFX_DeallocateState(handle, state);
        if (handle) NvVFX_DestroyEffect(handle);
        if (stream) NvVFX_CudaStreamDestroy(stream);
    }

    // Can take seconds (TensorRT engine load), so it runs on the loader thread.
    NvCV_Status Load(uint32_t w, uint32_t h, unsigned strengthModel) {
        width = w;
        height = h;
        model = strengthModel;
        const std::string models = SdkDirectory() + "\\models";
        NvCV_Status status = NvVFX_CreateEffect(NVVFX_FX_DENOISING, &handle);
        if (status == NVCV_SUCCESS) status = NvVFX_SetString(handle, NVVFX_MODEL_DIRECTORY, models.c_str());
        if (status == NVCV_SUCCESS) status = NvVFX_CudaStreamCreate(&stream);
        if (status == NVCV_SUCCESS) status = NvVFX_SetCudaStream(handle, NVVFX_CUDA_STREAM, stream);
        if (status == NVCV_SUCCESS) status = NvCVImage_Alloc(&gpuInput, w, h, NVCV_BGR, NVCV_F32, NVCV_PLANAR, NVCV_GPU, 1);
        if (status == NVCV_SUCCESS) status = NvCVImage_Alloc(&gpuOutput, w, h, NVCV_BGR, NVCV_F32, NVCV_PLANAR, NVCV_GPU, 1);
        if (status == NVCV_SUCCESS) status = NvVFX_SetImage(handle, NVVFX_INPUT_IMAGE, &gpuInput);
        if (status == NVCV_SUCCESS) status = NvVFX_SetImage(handle, NVVFX_OUTPUT_IMAGE, &gpuOutput);
        // 0: gentle (keeps texture), 1: strong. The effect offers no levels in between.
        if (status == NVCV_SUCCESS) status = NvVFX_SetF32(handle, NVVFX_STRENGTH, static_cast<float>(strengthModel));
        if (status == NVCV_SUCCESS) status = NvVFX_AllocateState(handle, &state);
        if (status == NVCV_SUCCESS) status = NvVFX_SetStateObjectHandleArray(handle, NVVFX_STATE, &state);
        if (status == NVCV_SUCCESS) status = NvVFX_Load(handle);
        return status;
    }

    NvCV_Status Run(uint8_t* nv12) {
        NvCV_Status status = NvCVImage_Init(&frame, width, height, static_cast<int>(width), nv12, NVCV_YUV420, NVCV_U8, NVCV_NV12, NVCV_CPU);
        // iPhone HD video: BT.709, limited range, MPEG-2 chroma siting.
        frame.colorspace = NVCV_709 | NVCV_VIDEO_RANGE | NVCV_CHROMA_COSITED;
        if (status == NVCV_SUCCESS) status = NvCVImage_Transfer(&frame, &gpuInput, 1.0f / 255.0f, stream, &staging);
        if (status == NVCV_SUCCESS) status = NvVFX_Run(handle, 0);
        if (status == NVCV_SUCCESS) status = NvCVImage_Transfer(&gpuOutput, &frame, 255.0f, stream, &staging);
        // The frame must be complete before it goes to the camera; never publish a half-copied one.
        if (status == NVCV_SUCCESS && !SynchronizeStream(stream)) status = NVCV_ERR_CUDA;
        return status;
    }
};

Denoiser::Denoiser() = default;

Denoiser::~Denoiser() {
    if (loader_.joinable()) loader_.join();
}

bool Denoiser::IsAvailable() {
    static const bool available = [] {
        unsigned version = 0;
        return NvVFX_GetVersion(&version) == NVCV_SUCCESS && version != 0;
    }();
    return available;
}

void Denoiser::StartLoad(uint32_t width, uint32_t height, unsigned model) {
    if (loader_.joinable()) loader_.join();
    wantedWidth_ = width;
    wantedHeight_ = height;
    wantedModel_ = model;
    loading_ = true;
    loader_ = std::thread([this, width, height, model] {
        auto effect = std::make_unique<Effect>();
        const NvCV_Status status = effect->Load(width, height, model);
        if (status == NVCV_SUCCESS) {
            std::lock_guard guard(lock_);
            loaded_ = std::move(effect);
            lastError_ = 0;
        } else {
            lastError_ = status;
        }
        loading_ = false;
    });
}

double Denoiser::EstimateNoise(const uint8_t* luma, uint32_t width, uint32_t height) {
    // Immerkær's Laplacian-difference mask on a sparse grid. Its response to Gaussian noise of sigma s has
    // standard deviation 6s; edges and texture give large responses, so a low percentile ignores them.
    thread_local std::vector<int> responses;
    responses.clear();
    for (uint32_t y = 2; y + 2 < height; y += 4) {
        const uint8_t* row = luma + static_cast<size_t>(y) * width;
        for (uint32_t x = 2; x + 2 < width; x += 4) {
            const uint8_t* p = row + x;
            const int response = p[-static_cast<int>(width) - 1] - 2 * p[-static_cast<int>(width)] + p[-static_cast<int>(width) + 1]
                                 - 2 * p[-1] + 4 * p[0] - 2 * p[1]
                                 + p[width - 1] - 2 * p[width] + p[width + 1];
            responses.push_back(response < 0 ? -response : response);
        }
    }
    if (responses.empty()) return 0;
    // 30th percentile of |N(0, 6s)| is 0.385 * 6s.
    const auto percentile = responses.begin() + static_cast<std::ptrdiff_t>(responses.size() * 3 / 10);
    std::nth_element(responses.begin(), percentile, responses.end());
    return *percentile / (0.385 * 6.0);
}

bool Denoiser::Process(uint8_t* nv12, uint32_t width, uint32_t height) {
    const float strength = strength_.load();
    if (strength <= 0 || !IsAvailable()) return false;

    // How noisy is the picture? Smoothed over ~20 frames so the amount does not flicker.
    const double sigma = EstimateNoise(nv12, width, height);
    noise_ = noise_ < 0 ? sigma : noise_ * 0.95 + sigma * 0.05;
    lastNoise_ = noise_;
    // The strong model removes about a third of fine detail and, measured on textured scenes, is worse than the
    // gentle one even in a dark room (noise ~16); it is kept for extreme noise only. Hysteresis avoids reloading.
    if (strongModel_ ? noise_ < kStrongModelOff : noise_ > kStrongModelOn) strongModel_ = !strongModel_;
    const unsigned model = strongModel_ ? 1 : 0;
    // A clean picture is left alone: the network has nothing to remove there and only adds its own artifacts.
    const double need = std::clamp((noise_ - kNoiseIgnored) / (kNoiseFull - kNoiseIgnored), 0.0, 1.0);
    const float amount = static_cast<float>(strength * need);
    lastAmount_ = amount;

    {
        std::lock_guard guard(lock_);
        if (loaded_) effect_ = std::move(loaded_);
    }

    const bool wantedChanged = wantedWidth_ != width || wantedHeight_ != height || wantedModel_ != model;
    const bool effectMatches = effect_ && effect_->width == width && effect_->height == height && effect_->model == model;
    // A failed load is not retried until the size or model changes.
    if (!effectMatches && !loading_ && (wantedChanged || lastError_ == 0)) StartLoad(width, height, model);
    // While the other model loads, keep using the current one if it fits this frame size.
    if (!effect_ || effect_->width != width || effect_->height != height) return false;
    if (amount < 0.02f) return false;

    // Mixing with the original gives a continuous amount on top of the effect's two levels.
    const size_t size = static_cast<size_t>(width) * height * 3 / 2;
    if (amount < 0.999f) original_.assign(nv12, nv12 + size);

    const auto start = std::chrono::steady_clock::now();
    if (effect_->Run(nv12) != NVCV_SUCCESS) {
        if (amount < 0.999f) std::memcpy(nv12, original_.data(), size);
        return false;
    }
    if (amount < 0.999f) {
        const int weight = static_cast<int>(amount * 256);
        for (size_t i = 0; i < size; ++i) {
            nv12[i] = static_cast<uint8_t>(original_[i] + (((nv12[i] - original_[i]) * weight) >> 8));
        }
    }
    lastMs_ = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    return true;
}

}  // namespace hitcam
