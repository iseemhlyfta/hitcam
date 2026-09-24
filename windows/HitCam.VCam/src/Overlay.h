#pragma once

#include <windows.h>

#include <cstdint>
#include <memory>
#include <mutex>
#include <vector>

// Object detection boxes from HitCam.exe (HitCam_BridgeSetOverlay), burnt into the camera's frames only (the app's
// preview stays clean). The layout is shared with the C# app: keep it.
#pragma pack(push, 4)
struct HitCamOverlayBox {
    float left, top, right, bottom;   // normalized 0..1 in the output frame; out of range is clamped
    uint32_t rgb;                     // box colour 0xRRGGBB
    int32_t labelWidth, labelHeight;  // label bitmap size in output-frame pixels (0 = no label)
    const uint8_t* label;             // 8-bit alpha of white text, labelWidth * labelHeight bytes, row-major; may be null
};
#pragma pack(pop)
static_assert(sizeof(HitCamOverlayBox) == 28 + sizeof(void*), "shared with the C# app");

namespace hitcam {

class Overlay {
public:
    static constexpr int32_t kMaxBoxes = 64;
    // Boxes not refreshed for this long are dropped: the app may stop sending.
    static constexpr ULONGLONG kStaleMs = 1000;

    struct Box {
        float left, top, right, bottom;
        uint32_t rgb;
        int32_t labelWidth, labelHeight;
        std::vector<uint8_t> label;
    };
    using Boxes = std::vector<Box>;

    // Any thread; copies everything. count <= 0 or null boxes clears.
    void Set(const HitCamOverlayBox* boxes, int32_t count);

    // The boxes to draw now, or null if there are none or they are stale.
    std::shared_ptr<const Boxes> Current();

    // Draws into an NV12 image (BT.709 video range) of even width and height; touches only box and label pixels.
    static void Draw(const Boxes& boxes, uint8_t* luma, uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height);

private:
    std::mutex lock_;
    std::shared_ptr<const Boxes> boxes_;
    ULONGLONG setMs_ = 0;
};

}  // namespace hitcam
