#pragma once

#include <windows.h>

#include <cstdint>
#include <mutex>
#include <vector>

// A finger-gun shot from HitCam.exe (HitCam_BridgeShot), played in the camera's frames only (the app's preview stays
// clean and draws the same effect itself, see ShotEffect.cs). The layout is shared with the C# app: keep it.
#pragma pack(push, 4)
struct HitCamShot {
    float x, y;        // muzzle, normalized 0..1 in the output frame
    float dirX, dirY;  // barrel direction in frame pixels (y down); normalized here
    float size;        // hand size as a fraction of the frame height
};
#pragma pack(pop)
static_assert(sizeof(HitCamShot) == 20, "shared with the C# app");

namespace hitcam {

// The effect at a moment after the shot. Same curves as ShotEffect.Timeline in the app: keep them in step.
struct ShotState {
    float flash;         // muzzle flash strength 0..1
    float lift;          // whole-frame brightening 0..1 (towards white)
    float shakeX, shakeY;  // picture offset, fractions of the frame height
    float zoom;          // picture scale >= 1, so the shake never shows the frame's edges
};

class ShotEffect {
public:
    static constexpr double kDurationMs = 220;
    static constexpr size_t kMaxShots = 4;

    static ShotState At(double ms, float dirX);

    // Any thread.
    void Fire(const HitCamShot& shot);
    void Clear();

    // True while a shot is playing at `nowMs` (GetTickCount64 based, or any clock `Fire` also used via FireAt).
    bool Active(double nowMs);

    // Plays the shots onto an NV12 frame (BT.709 video range, even size, packed: chroma right after luma, both with
    // `width` bytes per row). Decoder thread.
    void Draw(uint8_t* nv12, uint32_t width, uint32_t height, double nowMs);

    // For tests: a shot fired at a given time on the same clock as `nowMs`.
    void FireAt(const HitCamShot& shot, double ms);

    static double NowMs();

private:
    struct Fired {
        HitCamShot shot;
        double ms;
    };

    std::mutex lock_;
    std::vector<Fired> shots_;
    std::vector<uint8_t> scratch_;
    std::vector<int> columns_, rows_;
};

}  // namespace hitcam
