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
// Used from the decoder thread; SetStrength may be called from any thread.
class Denoiser {
public:
    Denoiser();
    ~Denoiser();

    // 0 turns it off; (0, 0.5] = gentle model mixed with the original, (0.5, 1] = strong model.
    void SetStrength(float strength) { strength_.store(strength); }

    bool IsEnabled() const { return strength_.load() > 0; }

    // Denoises a contiguous NV12 frame (pitch == width) in place. Returns true if the frame was changed.
    bool Process(uint8_t* nv12, uint32_t width, uint32_t height);

    // Milliseconds the last denoised frame took, or -1.
    double LastMilliseconds() const { return lastMs_.load(); }

    // NvCV status of the last failed load (0 = none): lets the UI explain why it does not work.
    int LastError() const { return lastError_.load(); }

    // Whether the NVIDIA runtime is installed and can create the effect (loads the libraries once; cached).
    static bool IsAvailable();

private:
    struct Effect;

    void StartLoad(uint32_t width, uint32_t height, unsigned model);

    std::atomic<float> strength_{0};
    std::atomic<double> lastMs_{-1};
    std::atomic<int> lastError_{0};

    std::mutex lock_;
    std::unique_ptr<Effect> effect_;   // ready to run, owned by the decoder thread while in use
    std::unique_ptr<Effect> loaded_;   // handed over by the loader thread
    std::thread loader_;
    std::atomic<bool> loading_{false};
    uint32_t wantedWidth_ = 0, wantedHeight_ = 0;
    unsigned wantedModel_ = 0;
    std::vector<uint8_t> original_;
};

}  // namespace hitcam
