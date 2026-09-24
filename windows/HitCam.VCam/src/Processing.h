#pragma once

#include <cstdint>

// Picture settings from HitCam.exe (HitCam_BridgeSetProcessing). The layout is shared with the C# app: keep it.
#pragma pack(push, 4)
struct HitCamProcessing {
    int32_t temporalStrength;   // 0 = off .. 100 = strongest temporal noise reduction
    int32_t artifactReduction;  // 0 = off, 1 = gentle (AR mode 0), 2 = strong (AR mode 1); ignored without NVIDIA runtime
    float brightness;           // -1..1, 0 neutral
    float contrast;             // -1..1, 0 neutral
    float saturation;           // -1..1, 0 neutral (-1 = grayscale)
    float sharpness;            // 0..1, 0 = off
    float shadows;              // -1..1, 0 neutral (+ lifts dark areas)
    float highlights;           // -1..1, 0 neutral (- recovers bright areas)
};
#pragma pack(pop)
static_assert(sizeof(HitCamProcessing) == 32, "shared with the C# app");

namespace hitcam {

// Values out of range (or NaN) are clamped; NaN counts as neutral.
HitCamProcessing ClampProcessing(const HitCamProcessing& settings);

// Tone curve changes luma (brightness, contrast, shadows, highlights).
inline bool HasToneCurve(const HitCamProcessing& s) {
    return s.brightness != 0 || s.contrast != 0 || s.shadows != 0 || s.highlights != 0;
}

// Anything for the Direct3D 11 stage (everything but the NVIDIA artifact reduction).
inline bool NeedsGpu(const HitCamProcessing& s) {
    return s.temporalStrength > 0 || HasToneCurve(s) || s.saturation != 0 || s.sharpness > 0;
}

}  // namespace hitcam
