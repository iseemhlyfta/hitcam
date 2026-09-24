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
// Anything but CUDA_SUCCESS (0) is a failure; CUDA_ERROR_INVALID_CONTEXT (201) in particular means this thread
// has no context for the stream, so the frame was never waited for and must not be published.
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

    // The effect is temporal: frames from before a cut would otherwise bleed into the new picture.
    void ResetState() {
        if (handle && state) NvVFX_ResetState(handle, state);
    }
};

Denoiser::Denoiser() = default;

Denoiser::~Denoiser() {
    {
        std::lock_guard guard(lock_);
        ++generation_;  // a load still running is thrown away (and freed) by the loader itself
    }
    if (loader_.joinable()) loader_.join();
}

bool Denoiser::IsAvailable() {
    static const bool available = [] {
        unsigned version = 0;
        return NvVFX_GetVersion(&version) == NVCV_SUCCESS && version != 0;
    }();
    return available;
}

// Called only while no load runs (loading_ was false under lock_); only this thread sets it back to true.
void Denoiser::StartLoad(uint32_t width, uint32_t height, unsigned model) {
    // The previous loader has already finished, so this join is immediate.
    if (loader_.joinable()) loader_.join();
    wantedWidth_ = width;
    wantedHeight_ = height;
    wantedModel_ = model;
    uint64_t generation = 0;
    {
        std::lock_guard guard(lock_);
        loading_ = true;
        generation = generation_;
    }
    try {
        loader_ = std::thread([this, width, height, model, generation] {
            std::unique_ptr<Effect> effect;
            NvCV_Status status = NVCV_ERR_MEMORY;
            try {
                effect = std::make_unique<Effect>();
                status = effect->Load(width, height, model);
            } catch (...) {
                status = NVCV_ERR_MEMORY;
            }
            std::unique_ptr<Effect> discarded;
            {
                std::lock_guard guard(lock_);
                loading_ = false;
                if (generation != generation_) {
                    // Switched off (or destroyed) meanwhile: nobody wants this model any more.
                    discarded = std::move(effect);
                } else if (status == NVCV_SUCCESS) {
                    discarded = std::move(loaded_);
                    loaded_ = std::move(effect);
                    lastError_ = 0;
                } else {
                    discarded = std::move(effect);
                    lastError_ = status;
                }
            }
            // GPU memory is released outside the lock.
        });
    } catch (...) {
        std::lock_guard guard(lock_);
        loading_ = false;
        lastError_ = NVCV_ERR_MEMORY;
    }
}

void Denoiser::Unload() {
    std::unique_ptr<Effect> pending;
    {
        std::lock_guard guard(lock_);
        ++generation_;
        pending = std::move(loaded_);
    }
    effect_.reset();
    pending.reset();
    wantedWidth_ = wantedHeight_ = 0;
    wantedModel_ = 0;
    runFailures_ = 0;
    skipped_ = false;
    lastMs_ = -1;
}

void Denoiser::ReleaseIfOff() {
    if (IsEnabled()) return;
    bool pending = false;
    {
        std::lock_guard guard(lock_);
        pending = loaded_ != nullptr || loading_;
    }
    if (effect_ || pending) Unload();
    // Switching on again starts afresh: a load that failed before is retried.
    lastError_ = 0;
    strongModel_ = false;
}

void Denoiser::Reset() {
    if (effect_) effect_->ResetState();
    skipped_ = false;
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
    const auto mode = static_cast<Mode>(mode_.load());
    if (mode == Mode::Off || !IsAvailable()) return false;

    // How noisy is the picture? Smoothed over ~20 frames so the amount does not flicker. Shown in every mode.
    const double sigma = EstimateNoise(nv12, width, height);
    noise_ = noise_ < 0 ? sigma : noise_ * 0.95 + sigma * 0.05;
    lastNoise_ = noise_;

    unsigned model = 0;
    float amount = 1.0f;
    if (mode == Mode::Maximum) {
        model = 1;
    } else if (mode == Mode::Fast) {
        // The strong model removes about a third of fine detail and, measured on textured scenes, is worse than
        // the gentle one even in a dark room (noise ~16), so here it is kept for extreme noise only.
        if (strongModel_ ? noise_ < kStrongModelOff : noise_ > kStrongModelOn) strongModel_ = !strongModel_;
        model = strongModel_ ? 1 : 0;
        // A clean picture is left alone: the network has nothing to remove there and only adds its own artifacts.
        amount = static_cast<float>(std::clamp((noise_ - kNoiseIgnored) / (kNoiseFull - kNoiseIgnored), 0.0, 1.0));
    }
    lastAmount_ = amount;

    std::unique_ptr<Effect> replaced;
    bool loading = false;
    {
        std::lock_guard guard(lock_);
        if (loaded_) {
            replaced = std::move(effect_);
            effect_ = std::move(loaded_);
            runFailures_ = 0;
        }
        loading = loading_;
    }
    replaced.reset();

    const bool wantedChanged = wantedWidth_ != width || wantedHeight_ != height || wantedModel_ != model;
    const bool effectMatches = effect_ && effect_->width == width && effect_->height == height && effect_->model == model;
    // A failed load (or a model that kept failing) is not retried until the size or model changes.
    if (!effectMatches && !loading && (wantedChanged || lastError_ == 0)) StartLoad(width, height, model);
    // While the other model loads, keep using the current one if it fits this frame size.
    if (!effect_ || effect_->width != width || effect_->height != height) return false;
    if (amount < 0.02f) {
        skipped_ = true;
        return false;
    }
    if (skipped_) {
        // Fast mode resumes after frames it left alone: the effect's memory of the scene is out of date.
        effect_->ResetState();
        skipped_ = false;
    }

    // Mixing with the original gives a continuous amount on top of the effect's two levels.
    const size_t size = static_cast<size_t>(width) * height * 3 / 2;
    if (amount < 0.999f) original_.assign(nv12, nv12 + size);

    const auto start = std::chrono::steady_clock::now();
    const NvCV_Status status = effect_->Run(nv12);
    if (status != NVCV_SUCCESS) {
        if (amount < 0.999f) std::memcpy(nv12, original_.data(), size);
        if (++runFailures_ >= kMaxRunFailures) {
            // The GPU keeps failing (driver reset, lost context, out of memory): report it instead of retrying
            // forever. wanted* stay as they are, so the error holds until the size, model or mode changes.
            effect_.reset();
            runFailures_ = 0;
            lastMs_ = -1;
            lastError_ = status;
        }
        return false;
    }
    runFailures_ = 0;
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
