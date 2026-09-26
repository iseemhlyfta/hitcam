#pragma once

#include <d3d11.h>
#include <wrl/client.h>

#include <cstdint>
#include <memory>
#include <mutex>
#include <vector>

// Settings of the background effect, shared with the C# app (HitCam_BridgeSetBackground).
#pragma pack(push, 4)
struct HitCamBackground {
    int32_t mode;        // 0 off, 1 blur, 2 replace with the image (HitCam_BridgeSetBackgroundImage)
    float strength;      // 0..1: blur radius
    float edge;          // 0..1: softness of the person's outline
    float dilate;        // 0..1: extra room kept around the person
    int32_t failClosed;  // non-zero: without a fresh mask the whole picture is blurred rather than shown as is
    uint32_t reserved;
};
#pragma pack(pop)
static_assert(sizeof(HitCamBackground) == 24, "shared with the C# app");

// Auto-framing: the part of the frame the camera shows, normalized; (0, 0, 1, 1) is the whole frame
// (HitCam_BridgeSetFraming).
#pragma pack(push, 4)
struct HitCamFraming {
    float left, top, right, bottom;
};
#pragma pack(pop)
static_assert(sizeof(HitCamFraming) == 16, "shared with the C# app");

namespace hitcam {

// Blurs or replaces the background behind the person with Direct3D 11 compute shaders, from a low-resolution mask
// (person = 255) the app computes on the preview. The mask stretches over the whole frame. Settings, mask and image
// come from any thread; Composite runs on the decoder thread.
//
// A mask older than kMaskFreshMs gets more room around the person (they may have moved); older than kMaskStaleMs, or
// none at all, is not trusted: with failClosed the whole picture is blurred (the room behind the person must not
// show), otherwise the frame passes as is.
class Compositor {
public:
    static constexpr uint32_t kMaxMaskSide = 512;
    static constexpr unsigned long long kMaskFreshMs = 200;
    static constexpr unsigned long long kMaskStaleMs = 800;

    Compositor();
    ~Compositor();
    Compositor(const Compositor&) = delete;
    Compositor& operator=(const Compositor&) = delete;

    // Any thread.
    void SetBackground(const HitCamBackground& settings);
    // Person mask, `width` x `height` bytes (at most kMaxMaskSide each); null or 0 clears it.
    void SetMask(const uint8_t* mask, uint32_t width, uint32_t height);
    // Picture for mode 2, BGRA; null or 0 clears it (mode 2 then blurs).
    void SetImage(const uint8_t* bgra, uint32_t width, uint32_t height, uint32_t stride);
    bool Active();

    // Decoder thread: the effect on a contiguous NV12 frame (pitch == width), in place. Returns true if it changed.
    bool Composite(uint8_t* nv12, uint32_t width, uint32_t height);
    // Auto-framing crop (any thread); dropped (whole frame) when not refreshed within kFramingStaleMs.
    static constexpr unsigned long long kFramingStaleMs = 1000;
    void SetFraming(const HitCamFraming& framing);
    // The crop to apply now, or false for the whole frame.
    bool CurrentFraming(HitCamFraming* framing);

    // Decoder thread: the crop scaled back to the whole frame (bilinear), in place. Returns true if it changed.
    bool Crop(uint8_t* nv12, uint32_t width, uint32_t height, const HitCamFraming& framing);

    // Decoder thread: frees the frame textures once nothing is on (keeps the device).
    void ReleaseIfOff() {
        HitCamFraming unused{};
        if (width_ != 0 && !Active() && !CurrentFraming(&unused)) ReleaseFrames();
    }
    // Milliseconds of the last Composite (upload, GPU, readback); -1 if none ran.
    double LastMilliseconds() const { return lastMs_; }

private:
    template <class T>
    using ComPtr = Microsoft::WRL::ComPtr<T>;

    struct Plane {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11ShaderResourceView> srv;
        ComPtr<ID3D11UnorderedAccessView> uav;
    };

    struct Inputs {
        HitCamBackground settings{};
        std::shared_ptr<const std::vector<uint8_t>> mask;
        uint32_t maskWidth = 0, maskHeight = 0;
        unsigned long long maskMs = 0;
        uint64_t maskVersion = 0;
        std::shared_ptr<const std::vector<uint8_t>> image;  // BGRA, pitch = imageWidth * 4
        uint32_t imageWidth = 0, imageHeight = 0;
        uint64_t imageVersion = 0;
        HitCamFraming framing{0, 0, 1, 1};
        unsigned long long framingMs = 0;
    };

    bool EnsureDevice();
    bool EnsureFrames(uint32_t width, uint32_t height);
    bool EnsureMask(const Inputs& inputs);
    bool EnsureImage(const Inputs& inputs, uint32_t width, uint32_t height);
    bool CreatePlane(Plane& plane, uint32_t width, uint32_t height, DXGI_FORMAT format, bool writable);
    bool Run(uint8_t* nv12, uint32_t width, uint32_t height, const Inputs& inputs, bool maskValid, unsigned long long maskAgeMs);
    bool ReadBack(uint8_t* nv12, uint32_t width, uint32_t height);
    // Without the GPU (lost, out of memory): coarse blocks over the whole frame on the processor, so failClosed still
    // holds.
    static void CoverOnCpu(uint8_t* nv12, uint32_t width, uint32_t height);
    void ReleaseFrames();
    void ReleaseDevice();

    std::mutex lock_;
    Inputs inputs_;

    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<ID3D11ComputeShader> downLuma_, downChroma_, blurLumaX_, blurLumaY_, blurChromaX_, blurChromaY_;
    ComPtr<ID3D11ComputeShader> compositeLuma_, compositeChroma_;
    ComPtr<ID3D11ComputeShader> cropLuma_, cropChroma_;
    ComPtr<ID3D11SamplerState> linearClamp_;
    ComPtr<ID3D11Buffer> params_;
    unsigned long long retryDeviceMs_ = 0;

    uint32_t width_ = 0, height_ = 0;
    Plane lumaIn_, chromaIn_, lumaOut_, chromaOut_;
    Plane smallLuma_[2], smallChroma_[2];  // weighted background at 1/8 (ping-pong for the blur passes)
    ComPtr<ID3D11Texture2D> lumaStaging_, chromaStaging_;
    Plane mask_;
    uint32_t maskWidth_ = 0, maskHeight_ = 0;
    uint64_t maskVersion_ = 0;
    Plane imageLuma_, imageChroma_;  // the replacement picture as NV12 at the frame size
    uint64_t imageVersion_ = 0;
    uint32_t imageFrameWidth_ = 0, imageFrameHeight_ = 0;
    double lastMs_ = -1;
};

}  // namespace hitcam
