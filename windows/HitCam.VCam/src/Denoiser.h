#pragma once

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

namespace hitcam {

// AI video noise removal with the NVIDIA Video Effects SDK ("Denoising" effect) on RTX GPUs.
// The SDK is a separate NVIDIA install; without it Process() passes frames through untouched.
// Used from the decoder thread; SetMode and the statistics may be called from any thread.
class Denoiser {
public:
    enum class Mode : int {
        Off = 0,
        // Gentle model only as far as noise is measured: a clean picture is passed through untouched (no GPU work).
        Fast = 1,
        // Gentle model at full strength on every frame: removes fine grain and compression noise, keeps texture.
        General = 2,
        // Strong model at full strength on every frame: cleanest, but also smooths fine detail.
        Maximum = 3,
    };

    Denoiser();
    // Waits for a model load in progress (can take seconds); holds no lock meanwhile.
    ~Denoiser();

    void SetMode(Mode mode) { mode_.store(static_cast<int>(mode)); }

    // Noise level (luma standard deviation, 0..255 scale) and the amount applied to the last frame, or -1 / 0.
    double LastNoise() const { return lastNoise_.load(); }
    float LastAmount() const { return lastAmount_.load(); }

    // Luma noise standard deviation of an image, robust to edges and texture.
    static double EstimateNoise(const uint8_t* luma, uint32_t width, uint32_t height);

    // Fast mode: noise below kNoiseIgnored is left alone (what a well-lit phone picture has after its own
    // processing); the amount rises linearly to full at kNoiseFull. It switches to the strong model only for
    // extreme noise, with hysteresis so models are not reloaded back and forth.
    static constexpr double kNoiseIgnored = 1.5;
    static constexpr double kNoiseFull = 4.0;
    static constexpr double kStrongModelOn = 20.0;
    static constexpr double kStrongModelOff = 15.0;
    // Consecutive failed frames after which the model is unloaded and the failure reported.
    static constexpr int kMaxRunFailures = 30;

    bool IsEnabled() const { return mode_.load() != static_cast<int>(Mode::Off); }

    // Denoises a contiguous NV12 frame (pitch == width) in place. Returns true if the frame was changed.
    bool Process(uint8_t* nv12, uint32_t width, uint32_t height);

    // Decoder thread, for frames that are not passed to Process: once switched off, frees the model and its GPU
    // memory, and forgets a failure so switching on again retries.
    void ReleaseIfOff();

    // Decoder thread: the picture was interrupted (phone disconnected); the next frames start a new scene.
    void Reset();

    // Milliseconds the last denoised frame took, or -1.
    double LastMilliseconds() const { return lastMs_.load(); }

    // NvCV status of the last failed load, or of processing that kept failing (0 = none): lets the UI explain
    // why it does not work instead of waiting for the model forever.
    int LastError() const { return lastError_.load(); }

    // Whether the NVIDIA runtime is installed and can create the effect (loads the libraries once; cached).
    static bool IsAvailable();

private:
    struct Effect;

    void StartLoad(uint32_t width, uint32_t height, unsigned model);
    void Unload();

    std::atomic<int> mode_{0};
    std::atomic<double> lastMs_{-1};
    std::atomic<int> lastError_{0};
    std::atomic<double> lastNoise_{-1};
    std::atomic<float> lastAmount_{0};

    // Decoder thread only.
    double noise_ = -1;
    bool strongModel_ = false;
    bool skipped_ = false;
    int runFailures_ = 0;
    std::unique_ptr<Effect> effect_;
    std::thread loader_;
    uint32_t wantedWidth_ = 0, wantedHeight_ = 0;
    unsigned wantedModel_ = 0;
    std::vector<uint8_t> original_;

    // Shared with the loader thread. A load started before the latest Unload (older generation) is discarded.
    std::mutex lock_;
    std::unique_ptr<Effect> loaded_;
    bool loading_ = false;
    uint64_t generation_ = 0;
};

}  // namespace hitcam
