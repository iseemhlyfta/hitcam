#pragma once

#include <windows.h>

#include <cstdint>
#include <memory>
#include <mutex>
#include <vector>

// Face areas HitCam.exe hides in the camera's frames (HitCam_BridgeSetFaceRegions): mosaic, blur or a solid fill,
// optionally framed. Camera frames only; the app hides faces in its preview itself. The layout is shared with the C#
// app: keep it. Coordinates are normalized to the output frame.
#pragma pack(push, 4)
struct HitCamFaceRegion {
    float left, top, right, bottom;
    int32_t effect;    // 0 mosaic, 1 blur, 2 solid fill, 3 none (the outline only)
    float strength;    // 0..1: cell size / blur radius
    uint32_t rgb;      // fill colour 0xRRGGBB
    int32_t frame;     // non-zero: draw an outline
    uint32_t frameRgb;
};
#pragma pack(pop)
static_assert(sizeof(HitCamFaceRegion) == 36, "shared with the C# app");

namespace hitcam {

class FaceEffect {
public:
    static constexpr int32_t kMaxRegions = 32;
    // Regions not refreshed for this long are dropped: the app may stop sending.
    static constexpr ULONGLONG kStaleMs = 1000;

    enum Kind : int32_t { Mosaic = 0, Blur = 1, Fill = 2, None = 3 };

    // Any thread; copies everything (invalid regions are skipped). count 0 clears.
    void Set(const HitCamFaceRegion* regions, int32_t count);

    std::shared_ptr<const std::vector<HitCamFaceRegion>> Current();

    // Applies the regions to an NV12 image (BT.709 video range, even size; chroma rows of `pitch` bytes like luma).
    static void Draw(const std::vector<HitCamFaceRegion>& regions, uint8_t* luma, uint8_t* chroma, uint32_t pitch,
                     uint32_t width, uint32_t height);

    // Cell size in pixels of a mosaic over a face `side` pixels wide: 1/24 of it at strength 0, 1/6 at strength 1.
    static int MosaicCell(int side, float strength);

    // Blur radius in pixels: 1/40 of the face at strength 0, 1/8 at strength 1.
    static int BlurRadius(int side, float strength);

private:
    std::mutex lock_;
    std::shared_ptr<const std::vector<HitCamFaceRegion>> regions_;
    ULONGLONG setMs_ = 0;
};

}  // namespace hitcam
