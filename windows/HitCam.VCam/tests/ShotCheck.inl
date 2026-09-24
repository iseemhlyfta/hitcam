// --shot: the finger-gun shot effect in the camera's frames (HitCam_BridgeShot). Like --overlay, frames go through a
// preview-only bridge whose camera output goes to a test callback: no camera is touched. Included by VCamTest.cpp
// after OverlayCheck.inl (reuses its Source, Output and TestBridge).

namespace shot_check {

using overlay_check::Check;
using overlay_check::Source;
using overlay_check::TestBridge;

// A packed NV12 frame of one colour.
std::vector<uint8_t> Flat(uint32_t w, uint32_t h, uint8_t y, uint8_t u = 128, uint8_t v = 128) {
    std::vector<uint8_t> frame(static_cast<size_t>(w) * h * 3 / 2, y);
    for (size_t i = static_cast<size_t>(w) * h; i < frame.size(); i += 2) {
        frame[i] = u;
        frame[i + 1] = v;
    }
    return frame;
}

void CheckTimeline() {
    const auto start = hitcam::ShotEffect::At(0, 1);
    const auto middle = hitcam::ShotEffect::At(100, 1);
    const auto end = hitcam::ShotEffect::At(hitcam::ShotEffect::kDurationMs, 1);
    Check(start.flash == 1 && start.lift > 0.2f && start.zoom > 1.03f && start.shakeY > 0.015f, "shot starts: full flash, bright frame, kick");
    Check(middle.flash < 0.1f && middle.lift < 0.03f && std::abs(middle.shakeY) < 0.01f, "100 ms later: almost over");
    Check(end.flash == 0 && end.lift == 0 && end.shakeX == 0 && end.shakeY == 0 && end.zoom == 1, "220 ms later: nothing");
    Check(hitcam::ShotEffect::At(0, 1).shakeX == -hitcam::ShotEffect::At(0, -1).shakeX, "kick against the barrel, left or right");
    // Zoom always hides the frame's edges: the margin it adds is at least the shake.
    bool covered = true;
    for (double ms = 0; ms < hitcam::ShotEffect::kDurationMs; ms += 5) {
        const auto s = hitcam::ShotEffect::At(ms, 1);
        covered = covered && (s.zoom - 1) / 2 >= std::abs(s.shakeY) - 1e-6f && (s.zoom - 1) / 2 >= std::abs(s.shakeX) - 1e-6f;
    }
    Check(covered, "shake never uncovers the frame's edges");
}

void CheckFlash() {
    const uint32_t w = 640, h = 360;
    const HitCamShot shot{0.5f, 0.5f, 1, 0, 0.2f};
    auto frame = Flat(w, h, 100);
    HitCam_TestShotFrame(&shot, 10, frame.data(), w, h);
    auto Y = [&](uint32_t x, uint32_t y) { return frame[static_cast<size_t>(y) * w + x]; };
    auto V = [&](uint32_t cx, uint32_t cy) { return frame[static_cast<size_t>(w) * h + static_cast<size_t>(cy) * w + cx * 2 + 1]; };
    // Muzzle at (320, 180) + a quarter hand (72 px tall) forward; the flame reaches further right.
    Check(Y(360, 180) > 215, "flame at the muzzle is white-hot");
    Check(Y(360, 180) > Y(360, 215) + 40, "flame thinner across the barrel than along it");
    Check(Y(5, 5) > 110 && Y(5, 5) < 140, "whole frame brighter for an instant");
    bool warm = false;
    for (uint32_t cx = 170; cx < 230; ++cx) warm = warm || V(cx, 90) > 140;
    Check(warm, "glow is warm (red chroma up)");

    auto later = Flat(w, h, 100);
    HitCam_TestShotFrame(&shot, 300, later.data(), w, h);
    Check(later == Flat(w, h, 100), "300 ms later the frame is untouched");

    auto scaled = Flat(w, h, 100);
    const HitCamShot longer{0.5f, 0.5f, 5, 0, 0.2f};
    HitCam_TestShotFrame(&longer, 10, scaled.data(), w, h);
    Check(scaled == frame, "direction is normalized");

    auto broken = Flat(w, h, 100);
    const HitCamShot nan{std::nanf(""), 0.5f, 1, 0, 0.2f};
    HitCam_TestShotFrame(&nan, 10, broken.data(), w, h);
    Check(broken == Flat(w, h, 100), "shot with NaN ignored");

    // At the edge of the frame: clipped, no crash.
    auto edge = Flat(w, h, 100);
    const HitCamShot corner{0.99f, 0.01f, 1, -1, 0.5f};
    HitCam_TestShotFrame(&corner, 10, edge.data(), w, h);
    Check(edge[static_cast<size_t>(2) * w + (w - 3)] > 170, "flash at the frame's corner clipped");
}

void CheckShake() {
    const uint32_t w = 640, h = 360;
    // Vertical gradient: the kick moves rows, so the picture near the top changes.
    std::vector<uint8_t> frame(static_cast<size_t>(w) * h * 3 / 2, 128);
    for (uint32_t y = 0; y < h; ++y) std::memset(frame.data() + static_cast<size_t>(y) * w, static_cast<int>(20 + y * 200 / h), w);
    const auto original = frame;
    const HitCamShot shot{0.9f, 0.9f, 1, 0, 0.05f};  // flash far away in the corner
    HitCam_TestShotFrame(&shot, 0, frame.data(), w, h);
    // Lift brightens everything, so compare with the same row only brightened: near the bottom the kick and the
    // zoom show a row about 12 higher, which is darker.
    const auto lift = hitcam::ShotEffect::At(0, 1).lift;
    const size_t row = static_cast<size_t>(340) * w;
    const int unshaken = static_cast<int>(std::lround(original[row] + (235 - original[row]) * lift));
    char what[96];
    std::snprintf(what, sizeof(what), "picture kicked (row 340: %d, unmoved %d)", frame[row + 10], unshaken);
    Check(frame[row + 10] < unshaken - 3, what);
}

void CheckBridge() {
    TestBridge bridge;
    if (!bridge.handle) {
        Check(false, "bridge created");
        return;
    }
    Source source(640, 360, 704);
    bridge.Publish(source);
    const auto cleanPreview = bridge.Preview();
    const auto clean = bridge.out.packed;

    const HitCamShot shot{0.5f, 0.5f, 1, 0, 0.2f};
    HitCam_BridgeShot(bridge.handle, &shot);
    bridge.Publish(source);
    Check(bridge.out.packed != clean, "shot reaches the camera frame");
    Check(bridge.Preview() == cleanPreview, "preview stays clean");

    Sleep(300);
    bridge.Publish(source);
    Check(bridge.out.packed == clean, "effect over after 0.3 s");

    HitCam_BridgeShot(bridge.handle, nullptr);
    HitCam_BridgeShot(nullptr, &shot);
    Check(true, "null shot or bridge ignored");
}

void CheckSpeed() {
    const uint32_t w = 1920, h = 1080;
    auto frame = Flat(w, h, 100);
    const HitCamShot shot{0.5f, 0.5f, 1, 0, 0.25f};
    const auto start = std::chrono::steady_clock::now();
    const int runs = 20;
    for (int i = 0; i < runs; ++i) HitCam_TestShotFrame(&shot, 5, frame.data(), w, h);
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count() / runs;
    char what[96];
    std::snprintf(what, sizeof(what), "1080p frame with the full effect: %.1f ms (< 15)", ms);
    Check(ms < 15, what);
}

int Run() {
    CheckTimeline();
    CheckFlash();
    CheckShake();
    CheckBridge();
    CheckSpeed();
    std::printf(overlay_check::g_passed ? "ALL PASSED\n" : "SOME CHECKS FAILED\n");
    return overlay_check::g_passed ? 0 : 1;
}

}  // namespace shot_check
