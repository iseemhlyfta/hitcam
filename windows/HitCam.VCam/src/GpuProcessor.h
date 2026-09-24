#pragma once

#include <d3d11.h>
#include <wrl/client.h>

#include <array>
#include <cstdint>
#include <vector>

#include "Processing.h"

namespace hitcam {

// Temporal noise reduction and colour / sharpness adjustments of NV12 frames with Direct3D 11 compute shaders, on
// any GPU (WARP, the software rasterizer, if there is no hardware device). Decoder thread only.
//
// Temporal noise reduction mixes each pixel with the previous output where the picture does not move: the
// difference to the previous output, averaged over 3x3 pixels (robust to noise), and the pixel's own difference
// decide. Above a threshold that follows the measured noise level the pixel is taken from the new frame as is, so
// moving parts are neither smeared nor leave trails. Chroma uses the motion of its luma pixels and its own.
class GpuProcessor {
public:
    GpuProcessor();
    ~GpuProcessor();
    GpuProcessor(const GpuProcessor&) = delete;
    GpuProcessor& operator=(const GpuProcessor&) = delete;

    // Processes a contiguous NV12 frame (pitch == width) in place; `settings` must need the GPU (NeedsGpu).
    // Returns false and leaves the frame untouched if the GPU is not available (or was lost).
    bool Process(uint8_t* nv12, uint32_t width, uint32_t height, const HitCamProcessing& settings);

    // The next frame starts a new scene (phone disconnected, processing paused).
    void Reset() { historyValid_ = false; }

    // Frees the textures (keeps the device); the next Process creates them again.
    void ReleaseFrames();

    // Milliseconds the last processed frame took on the decoder thread (upload, GPU, readback).
    double LastMilliseconds() const { return lastMs_; }

    // Luma noise standard deviation of an image (0..255 scale), robust to edges and texture.
    static double EstimateNoise(const uint8_t* luma, uint32_t width, uint32_t height);
    // The 256-entry tone curve (output 0..1) for brightness, contrast, shadows and highlights.
    static std::array<float, 256> BuildToneCurve(const HitCamProcessing& settings);

private:
    bool EnsureDevice();
    bool EnsureFrames(uint32_t width, uint32_t height);
    bool Run(uint8_t* nv12, uint32_t width, uint32_t height, const HitCamProcessing& settings);
    void ReleaseDevice();
    bool DetectCut(const uint8_t* luma, uint32_t width, uint32_t height);

    template <class T>
    using ComPtr = Microsoft::WRL::ComPtr<T>;

    struct Plane {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11ShaderResourceView> srv;
        ComPtr<ID3D11UnorderedAccessView> uav;
    };
    bool CreatePlane(Plane& plane, uint32_t width, uint32_t height, DXGI_FORMAT format, bool writable);

    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<ID3D11ComputeShader> temporalLuma_, temporalChroma_, adjustLuma_, adjustChroma_;
    ComPtr<ID3D11Buffer> params_;
    ComPtr<ID3D11Buffer> toneCurve_;
    ComPtr<ID3D11ShaderResourceView> toneCurveView_;
    unsigned long long retryDeviceMs_ = 0;

    uint32_t width_ = 0, height_ = 0;
    Plane lumaIn_, chromaIn_, lumaHistory_[2], chromaHistory_[2], weight_, lumaOut_, chromaOut_;
    ComPtr<ID3D11Texture2D> lumaStaging_, chromaStaging_;
    int history_ = 0;  // which of the two history planes holds the previous output
    bool historyValid_ = false;

    std::array<float, 256> uploadedCurve_{};
    bool curveUploaded_ = false;
    double noise_ = -1;
    std::vector<int> cutSamples_;
    double lastMs_ = -1;
};

}  // namespace hitcam
