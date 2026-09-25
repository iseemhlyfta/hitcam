#pragma once

#include <windows.h>

#include <cstdint>
#include <memory>
#include <mutex>
#include <vector>

// What HitCam.exe draws for the hands in the camera's frames (HitCam_BridgeSetHandScene): points on the fingers,
// lines (threads between fingertips, the hand's skeleton) and colour fills between threads. Camera frames only; the
// app draws its preview itself. The layouts are shared with the C# app: keep them. Coordinates are normalized to the
// output frame; sizes are fractions of the frame height.
#pragma pack(push, 4)
struct HitCamSceneDot {
    float x, y, radius;
    uint32_t rgb;  // 0xRRGGBB
};
struct HitCamSceneLine {
    float x0, y0, x1, y1, width;
    uint32_t rgb;
    float alpha;  // 0..1
};
// A four-cornered area (corners in order around it), filled with a linear gradient from `rgbFrom` at (fromX, fromY)
// to `rgbTo` at (toX, toY).
struct HitCamSceneQuad {
    float x[4], y[4];
    uint32_t rgbFrom, rgbTo;
    float fromX, fromY, toX, toY;
    float alpha;  // 0..1
};
#pragma pack(pop)
static_assert(sizeof(HitCamSceneDot) == 16, "shared with the C# app");
static_assert(sizeof(HitCamSceneLine) == 28, "shared with the C# app");
static_assert(sizeof(HitCamSceneQuad) == 60, "shared with the C# app");

namespace hitcam {

class HandScene {
public:
    static constexpr int32_t kMaxDots = 128;
    static constexpr int32_t kMaxLines = 128;
    static constexpr int32_t kMaxQuads = 16;
    // A scene not refreshed for this long is dropped: the app may stop sending.
    static constexpr ULONGLONG kStaleMs = 1000;

    struct Scene {
        std::vector<HitCamSceneQuad> quads;
        std::vector<HitCamSceneLine> lines;
        std::vector<HitCamSceneDot> dots;
    };

    // Any thread; copies everything (invalid items are skipped). All counts 0 clears.
    void Set(const HitCamSceneDot* dots, int32_t dotCount, const HitCamSceneLine* lines, int32_t lineCount,
             const HitCamSceneQuad* quads, int32_t quadCount);

    // The scene to draw now, or null if there is none or it is stale.
    std::shared_ptr<const Scene> Current();

    // Draws fills, then lines, then dots into an NV12 image (BT.709 video range, even size; chroma `pitch` bytes per
    // row like luma). Clipped to the frame.
    static void Draw(const Scene& scene, uint8_t* luma, uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height);

private:
    std::mutex lock_;
    std::shared_ptr<const Scene> scene_;
    ULONGLONG setMs_ = 0;
};

}  // namespace hitcam
