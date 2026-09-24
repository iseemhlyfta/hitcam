#pragma once

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>

namespace hitcam {

// Compression artifact removal (blocking, ringing, mosquito noise of the phone's H.264) with the NVIDIA Video
// Effects SDK "ArtifactReduction" effect on RTX GPUs. The SDK is a separate NVIDIA install; without it Process()
// leaves frames untouched. Used from the decoder thread; SetMode and the statistics may be called from any thread.
class ArtifactReducer {
public:
    enum class Mode : int {
        Off = 0,
        // AR mode 0: for higher bitrates, removes artifacts and keeps texture.
        Gentle = 1,
        // AR mode 1: for low bitrates, removes more, smooths more.
        Strong = 2,
    };

    ArtifactReducer();
    // Never waits for a model load in progress: the loader thread finishes (and frees it) on its own.
    ~ArtifactReducer();

    void SetMode(Mode mode) { mode_.store(static_cast<int>(mode)); }
    bool IsEnabled() const { return mode_.load() != static_cast<int>(Mode::Off); }

    // Consecutive failed frames after which the model is unloaded and the failure reported.
    static constexpr int kMaxRunFailures = 30;

    enum class Result { Unchanged, Changed, Failed };

    // Processes a contiguous NV12 frame (pitch == width) in place. Failed: the run failed and may have left the
    // frame half written.
    // Frames pass through untouched while the model loads, or if it cannot load for this size (see LastError).
    Result Process(uint8_t* nv12, uint32_t width, uint32_t height);

    // Decoder thread, for frames that are not passed to Process: once switched off, frees the model and its GPU
    // memory, and forgets a failure so switching on again retries.
    void ReleaseIfOff();

    // Milliseconds the last processed frame took, or -1 if the last frame was not processed.
    double LastMilliseconds() const { return lastMs_.load(); }

    // NvCV status of the last failed load, or of processing that kept failing (0 = none): lets the UI explain why
    // it does not work (for example NVCV_ERR_RESOLUTION for a portrait 1080x1920 picture) instead of waiting.
    int LastError() const;

    // Whether the NVIDIA runtime and the artifact reduction models are installed (checked once; cached).
    static bool IsAvailable();

private:
    struct Effect;
    // Shared with the loader thread, which may outlive this object.
    struct Loader {
        std::mutex lock;
        std::unique_ptr<Effect> loaded;
        bool loading = false;
        // A load started before the latest Unload (older generation) is discarded.
        uint64_t generation = 0;
        int lastError = 0;
    };

    void StartLoad(uint32_t width, uint32_t height, unsigned model);
    void Unload();

    std::atomic<int> mode_{0};
    std::atomic<double> lastMs_{-1};
    std::shared_ptr<Loader> loader_;

    // Decoder thread only.
    int runFailures_ = 0;
    std::unique_ptr<Effect> effect_;
    uint32_t wantedWidth_ = 0, wantedHeight_ = 0;
    unsigned wantedModel_ = 0;
};

}  // namespace hitcam
