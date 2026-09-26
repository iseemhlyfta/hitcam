// --faces: hidden faces (mosaic, blur, fill) in the camera's frames (HitCam_BridgeSetFaceRegions). Frames go through a
// preview-only bridge with a test callback, as in --overlay: no camera is touched. Included by VCamTest.cpp after
// SceneCheck.inl (reuses its Nv12 and the overlay check's helpers).

namespace face_check {

using overlay_check::Check;
using overlay_check::Expected;
using overlay_check::Source;
using overlay_check::TestBridge;
using scene_check::Near;
using scene_check::Nv12;

// A frame with a fine checkerboard in luma (20/200) and a chroma ramp: any smoothing shows.
Nv12 Checker(uint32_t w, uint32_t h) {
    Nv12 f(w, h);
    for (uint32_t y = 0; y < h; ++y)
        for (uint32_t x = 0; x < w; ++x) f.data[static_cast<size_t>(y) * w + x] = static_cast<uint8_t>(((x / 2 + y / 2) % 2) ? 200 : 20);
    for (uint32_t y = 0; y < h / 2; ++y)
        for (uint32_t x = 0; x < w; ++x) f.data[static_cast<size_t>(w) * h + static_cast<size_t>(y) * w + x] = static_cast<uint8_t>(60 + x % 120);
    return f;
}

void Apply(Nv12& f, const std::vector<HitCamFaceRegion>& regions) {
    HitCam_TestFaceFrame(regions.data(), static_cast<int32_t>(regions.size()), f.data.data(), f.w, f.h);
}

HitCamFaceRegion Region(float l, float t, float r, float b, int32_t effect, float strength = 0.5f, uint32_t rgb = 0, int32_t frame = 0) {
    return HitCamFaceRegion{l, t, r, b, effect, strength, rgb, frame, 0x22C55E};
}

void CheckMosaic() {
    Nv12 f = Checker(640, 360);
    const Nv12 original = f;
    Apply(f, {Region(0.25f, 0.25f, 0.5f, 0.75f, 0)});   // 160 × 180 px from (160, 90)
    // FaceEffect::MosaicCell(180, 0.5): 180 · (1/24 + (1/6 − 1/24) / 2) = 18.75 → 19 → even 18.
    const int cell = 18;
    bool flat = true;
    for (int dy = 0; dy < cell; ++dy)
        for (int dx = 0; dx < cell; ++dx) flat = flat && f.Y(160 + dx, 90 + dy) == f.Y(160, 90);
    Check(flat, "mosaic: each cell one value");
    Check(Near(f.Y(160, 90), 110, 3), "mosaic: the cell is the average (checker 20/200 -> 110)");
    Check(f.Y(158, 90) == original.Y(158, 90) && f.Y(330, 90) == original.Y(330, 90) && f.Y(200, 280) == original.Y(200, 280),
          "mosaic: outside untouched");
}

void CheckBlurAndFill() {
    Nv12 blurred = Checker(640, 360);
    Apply(blurred, {Region(0.25f, 0.25f, 0.75f, 0.75f, 1, 1.0f)});
    Check(Near(blurred.Y(320, 180), 110, 12) && Near(blurred.Y(321, 181), 110, 12), "blur: the checkerboard is smoothed away");

    Nv12 filled = Checker(640, 360);
    Apply(filled, {Region(0.25f, 0.25f, 0.75f, 0.75f, 2, 0.5f, 0xFF0000, 1)});
    const auto red = Expected(0xFF0000), green = Expected(0x22C55E);
    Check(Near(filled.Y(320, 180), red.y) && Near(filled.V(320, 180), red.v), "fill: the chosen colour");
    Check(Near(filled.Y(161, 180), green.y), "frame: drawn on the edge");
    Check(filled.Y(150, 180) == Checker(640, 360).Y(150, 180), "fill: outside untouched");

    Nv12 framed = Checker(640, 360);
    Apply(framed, {Region(0.25f, 0.25f, 0.75f, 0.75f, 3, 0.5f, 0, 1)});
    const Nv12 plain = Checker(640, 360);
    Check(Near(framed.Y(161, 180), green.y) && framed.Y(320, 180) == plain.Y(320, 180) && framed.Y(321, 181) == plain.Y(321, 181),
          "none: only the outline, the face stays");
}

void CheckRobust() {
    Nv12 f = Checker(640, 360);
    const Nv12 original = f;
    Apply(f, {Region(std::nanf(""), 0, 1, 1, 0), Region(0.1f, 0.1f, 0.2f, 0.2f, 7), Region(0.1f, 0.1f, 0.2f, 0.2f, -1), Region(1e9f, 0, 1, 1, 1)});
    Check(f.data == original.data, "invalid regions skipped");

    Nv12 edge = Checker(640, 360);
    Apply(edge, {Region(-0.5f, -0.5f, 0.1f, 0.1f, 0), Region(0.95f, 0.95f, 2, 2, 1), Region(0.5f, 0.5f, 0.5f, 0.5f, 2)});
    Check(true, "regions past the edges and empty ones clipped");

    std::vector<HitCamFaceRegion> many(100, Region(0.1f, 0.1f, 0.2f, 0.2f, 2));
    Nv12 lots = Checker(640, 360);
    Apply(lots, many);
    Check(true, "more regions than the limit ignored beyond it");
}

void CheckBridge() {
    TestBridge bridge;
    if (!bridge.handle) {
        Check(false, "bridge created");
        return;
    }
    Source source(640, 360, 704);
    bridge.Publish(source);
    const auto clean = bridge.out.packed;
    const auto cleanPreview = bridge.Preview();

    const HitCamFaceRegion region = Region(0.3f, 0.3f, 0.6f, 0.7f, 0);
    HitCam_BridgeSetFaceRegions(bridge.handle, &region, 1);
    bridge.Publish(source);
    Check(bridge.out.packed != clean, "hidden face reaches the camera frame");
    Check(bridge.Preview() == cleanPreview, "preview stays clean (the app hides faces there itself)");

    HitCam_BridgeSetFaceRegions(bridge.handle, nullptr, 0);
    bridge.Publish(source);
    Check(bridge.out.packed == clean, "no regions: untouched");

    HitCam_BridgeSetFaceRegions(bridge.handle, &region, 1);
    Sleep(1100);
    bridge.Publish(source);
    Check(bridge.out.packed == clean, "regions not refreshed for a second are dropped");
}

void CheckSpeed() {
    Nv12 f = Checker(1920, 1080);
    std::vector<HitCamFaceRegion> regions;
    for (int i = 0; i < 5; ++i) regions.push_back(Region(0.05f + i * 0.18f, 0.2f, 0.2f + i * 0.18f, 0.6f, i % 2, 1.0f));
    const auto start = std::chrono::steady_clock::now();
    for (int i = 0; i < 10; ++i) Apply(f, regions);
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count() / 10;
    char what[96];
    std::snprintf(what, sizeof(what), "1080p, 5 large faces (mosaic and strong blur): %.1f ms (< 5)", ms);
    Check(ms < 5, what);
}

int Run() {
    CheckMosaic();
    CheckBlurAndFill();
    CheckRobust();
    CheckBridge();
    CheckSpeed();
    std::printf(overlay_check::g_passed ? "ALL PASSED\n" : "SOME CHECKS FAILED\n");
    return overlay_check::g_passed ? 0 : 1;
}

}  // namespace face_check
