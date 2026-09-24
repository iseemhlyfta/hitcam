#include "ArtifactReducer.h"

#include <windows.h>

#include <chrono>
#include <cstring>
#include <string>
#include <thread>

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

std::string ModelDirectory() { return SdkDirectory() + "\\models"; }

bool FileExists(const std::string& pattern) {
    WIN32_FIND_DATAA data;
    const HANDLE find = FindFirstFileA(pattern.c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) return false;
    FindClose(find);
    return true;
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

struct ArtifactReducer::Effect {
    NvVFX_Handle handle = nullptr;
    CUstream stream = nullptr;
    NvCVImage gpuInput, gpuOutput, frame, staging;
    uint32_t width = 0, height = 0;
    unsigned model = 0;

    ~Effect() {
        if (handle) NvVFX_DestroyEffect(handle);
        if (stream) NvVFX_CudaStreamDestroy(stream);
    }

    // Can take seconds (TensorRT engine load), so it runs on the loader thread.
    NvCV_Status Load(uint32_t w, uint32_t h, unsigned mode) {
        width = w;
        height = h;
        model = mode;
        const std::string models = ModelDirectory();
        NvCV_Status status = NvVFX_CreateEffect(NVVFX_FX_ARTIFACT_REDUCTION, &handle);
        if (status == NVCV_SUCCESS) status = NvVFX_SetString(handle, NVVFX_MODEL_DIRECTORY, models.c_str());
        if (status == NVCV_SUCCESS) status = NvVFX_CudaStreamCreate(&stream);
        if (status == NVCV_SUCCESS) status = NvVFX_SetCudaStream(handle, NVVFX_CUDA_STREAM, stream);
        if (status == NVCV_SUCCESS) status = NvCVImage_Alloc(&gpuInput, w, h, NVCV_BGR, NVCV_F32, NVCV_PLANAR, NVCV_GPU, 1);
        if (status == NVCV_SUCCESS) status = NvCVImage_Alloc(&gpuOutput, w, h, NVCV_BGR, NVCV_F32, NVCV_PLANAR, NVCV_GPU, 1);
        if (status == NVCV_SUCCESS) status = NvVFX_SetImage(handle, NVVFX_INPUT_IMAGE, &gpuInput);
        if (status == NVCV_SUCCESS) status = NvVFX_SetImage(handle, NVVFX_OUTPUT_IMAGE, &gpuOutput);
        // 0: for higher bitrates (keeps texture), 1: for low bitrates (stronger).
        if (status == NVCV_SUCCESS) status = NvVFX_SetU32(handle, NVVFX_MODE, mode);
        if (status == NVCV_SUCCESS) status = NvVFX_Load(handle);
        return status;
    }

    NvCV_Status Run(uint8_t* nv12) {
        NvCV_Status status = NvCVImage_Init(&frame, width, height, static_cast<int>(width), nv12, NVCV_YUV420, NVCV_U8, NVCV_NV12, NVCV_CPU);
        // Phone HD video: BT.709, limited range, MPEG-2 chroma siting.
        frame.colorspace = NVCV_709 | NVCV_VIDEO_RANGE | NVCV_CHROMA_COSITED;
        if (status == NVCV_SUCCESS) status = NvCVImage_Transfer(&frame, &gpuInput, 1.0f / 255.0f, stream, &staging);
        if (status == NVCV_SUCCESS) status = NvVFX_Run(handle, 0);
        if (status == NVCV_SUCCESS) status = NvCVImage_Transfer(&gpuOutput, &frame, 255.0f, stream, &staging);
        // The frame must be complete before it goes to the camera; never publish a half-copied one.
        if (status == NVCV_SUCCESS && !SynchronizeStream(stream)) status = NVCV_ERR_CUDA;
        return status;
    }
};

ArtifactReducer::ArtifactReducer() : loader_(std::make_shared<Loader>()) {}

ArtifactReducer::~ArtifactReducer() {
    std::lock_guard guard(loader_->lock);
    ++loader_->generation;  // a load still running is thrown away (and freed) by the loader thread itself
}

bool ArtifactReducer::IsAvailable() {
    static const bool available = [] {
        unsigned version = 0;
        if (NvVFX_GetVersion(&version) != NVCV_SUCCESS || version == 0) return false;
        // AR-con-<sm>.engine.trtpkg (mode 0) and AR-agg-<sm>.engine.trtpkg (mode 1).
        const std::string models = ModelDirectory();
        return FileExists(models + "\\AR-con*") && FileExists(models + "\\AR-agg*");
    }();
    return available;
}

int ArtifactReducer::LastError() const {
    std::lock_guard guard(loader_->lock);
    return loader_->lastError;
}

// Called only while no load runs (loading was false under the lock); only this thread sets it back to true.
void ArtifactReducer::StartLoad(uint32_t width, uint32_t height, unsigned model) {
    wantedWidth_ = width;
    wantedHeight_ = height;
    wantedModel_ = model;
    std::shared_ptr<Loader> loader = loader_;
    uint64_t generation = 0;
    {
        std::lock_guard guard(loader->lock);
        loader->loading = true;
        generation = loader->generation;
    }
    try {
        std::thread([loader, width, height, model, generation] {
            // The thread may outlive the bridge (it is never waited for): keep this DLL loaded for it.
            HMODULE self = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                               reinterpret_cast<LPCWSTR>(&SdkDirectory), &self);
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
                std::lock_guard guard(loader->lock);
                loader->loading = false;
                if (generation != loader->generation) {
                    // Switched off (or destroyed) meanwhile: nobody wants this model any more.
                    discarded = std::move(effect);
                } else if (status == NVCV_SUCCESS) {
                    discarded = std::move(loader->loaded);
                    loader->loaded = std::move(effect);
                    loader->lastError = 0;
                } else {
                    discarded = std::move(effect);
                    loader->lastError = status;
                }
            }
            // GPU memory is released outside the lock.
        }).detach();
    } catch (...) {
        std::lock_guard guard(loader->lock);
        loader->loading = false;
        loader->lastError = NVCV_ERR_MEMORY;
    }
}

void ArtifactReducer::Unload() {
    std::unique_ptr<Effect> pending;
    {
        std::lock_guard guard(loader_->lock);
        ++loader_->generation;
        pending = std::move(loader_->loaded);
    }
    effect_.reset();
    pending.reset();
    wantedWidth_ = wantedHeight_ = 0;
    wantedModel_ = 0;
    runFailures_ = 0;
    lastMs_ = -1;
}

void ArtifactReducer::ReleaseIfOff() {
    lastMs_ = -1;
    if (IsEnabled()) return;
    bool pending = false;
    {
        std::lock_guard guard(loader_->lock);
        pending = loader_->loaded != nullptr || loader_->loading;
        // Switching on again starts afresh: a load that failed before is retried.
        loader_->lastError = 0;
    }
    if (effect_ || pending || wantedWidth_ != 0) Unload();
}

ArtifactReducer::Result ArtifactReducer::Process(uint8_t* nv12, uint32_t width, uint32_t height) {
    const auto mode = static_cast<Mode>(mode_.load());
    if (mode == Mode::Off || !IsAvailable()) {
        lastMs_ = -1;
        return Result::Unchanged;
    }
    const unsigned model = mode == Mode::Strong ? 1 : 0;

    std::unique_ptr<Effect> replaced;
    bool loading = false;
    int lastError = 0;
    {
        std::lock_guard guard(loader_->lock);
        if (loader_->loaded) {
            replaced = std::move(effect_);
            effect_ = std::move(loader_->loaded);
            runFailures_ = 0;
        }
        loading = loader_->loading;
        lastError = loader_->lastError;
    }
    replaced.reset();

    const bool wantedChanged = wantedWidth_ != width || wantedHeight_ != height || wantedModel_ != model;
    const bool effectMatches = effect_ && effect_->width == width && effect_->height == height && effect_->model == model;
    // A failed load (or a model that kept failing) is not retried until the size or mode changes.
    if (!effectMatches && !loading && (wantedChanged || lastError == 0)) StartLoad(width, height, model);
    // While the other mode loads, keep using the current model if it fits this frame size.
    if (!effect_ || effect_->width != width || effect_->height != height) {
        lastMs_ = -1;
        return Result::Unchanged;
    }

    const auto start = std::chrono::steady_clock::now();
    const NvCV_Status status = effect_->Run(nv12);
    if (status != NVCV_SUCCESS) {
        // The transfer back may have been cut short: the caller keeps its own copy of the frame for this.
        lastMs_ = -1;
        if (++runFailures_ >= kMaxRunFailures) {
            // The GPU keeps failing (driver reset, lost context, out of memory): report it instead of retrying
            // forever. wanted* stay as they are, so the error holds until the size, mode or setting changes.
            effect_.reset();
            runFailures_ = 0;
            std::lock_guard guard(loader_->lock);
            loader_->lastError = status;
        }
        return Result::Failed;
    }
    runFailures_ = 0;
    if (lastError != 0) {
        // A failure for another size (a portrait picture the model does not take) no longer applies.
        std::lock_guard guard(loader_->lock);
        loader_->lastError = 0;
    }
    lastMs_ = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    return Result::Changed;
}

}  // namespace hitcam
