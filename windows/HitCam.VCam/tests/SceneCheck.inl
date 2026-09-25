// --scene: points, threads and fills for the hands drawn into the camera's frames (HitCam_BridgeSetHandScene).
// Frames go through a preview-only bridge with a test callback, as in --overlay: no camera is touched. Included by
// VCamTest.cpp after OverlayCheck.inl (reuses its Check, Source, TestBridge and Expected colours).

namespace scene_check {

using overlay_check::Check;
using overlay_check::Expected;
using overlay_check::Source;
using overlay_check::TestBridge;

struct Nv12 {
    uint32_t w, h;
    std::vector<uint8_t> data;
    Nv12(uint32_t width, uint32_t height, uint8_t y = 100) : w(width), h(height), data(static_cast<size_t>(width) * height * 3 / 2, 128) {
        std::fill(data.begin(), data.begin() + static_cast<size_t>(w) * h, y);
    }
    int Y(uint32_t x, uint32_t y) const { return data[static_cast<size_t>(y) * w + x]; }
    int U(uint32_t x, uint32_t y) const { return data[static_cast<size_t>(w) * h + static_cast<size_t>(y / 2) * w + (x / 2) * 2]; }
    int V(uint32_t x, uint32_t y) const { return data[static_cast<size_t>(w) * h + static_cast<size_t>(y / 2) * w + (x / 2) * 2 + 1]; }
    void Draw(const std::vector<HitCamSceneDot>& dots, const std::vector<HitCamSceneLine>& lines, const std::vector<HitCamSceneQuad>& quads) {
        HitCam_TestSceneFrame(dots.data(), static_cast<int32_t>(dots.size()), lines.data(), static_cast<int32_t>(lines.size()),
                              quads.data(), static_cast<int32_t>(quads.size()), data.data(), w, h);
    }
};

bool Near(int actual, int expected, int tolerance = 3) { return std::abs(actual - expected) <= tolerance; }

HitCamSceneQuad Rect(float l, float t, float r, float b, uint32_t from, uint32_t to, float alpha) {
    HitCamSceneQuad q{};
    q.x[0] = l; q.y[0] = t;
    q.x[1] = r; q.y[1] = t;
    q.x[2] = r; q.y[2] = b;
    q.x[3] = l; q.y[3] = b;
    q.rgbFrom = from;
    q.rgbTo = to;
    q.fromX = l; q.fromY = (t + b) / 2;
    q.toX = r; q.toY = (t + b) / 2;
    q.alpha = alpha;
    return q;
}

void CheckFill() {
    Nv12 f(640, 360);
    f.Draw({}, {}, {Rect(0.25f, 0.25f, 0.75f, 0.75f, 0xFF0000, 0x0000FF, 1)});
    const auto red = Expected(0xFF0000), blue = Expected(0x0000FF), purple = Expected(0x800080);
    Check(Near(f.Y(162, 180), red.y) && Near(f.V(162, 180), red.v), "fill starts with the left colour");
    Check(Near(f.Y(477, 180), blue.y) && Near(f.U(477, 180), blue.u), "fill ends with the right colour");
    Check(Near(f.Y(320, 180), purple.y, 6), "fill blends in between");
    Check(f.Y(150, 180) == 100 && f.Y(320, 80) == 100 && f.U(150, 180) == 128, "outside the fill untouched");

    // A triangle sent as a quad with a repeated corner (half of a twisted ribbon).
    Nv12 tri(640, 360);
    HitCamSceneQuad t = Rect(0.25f, 0.25f, 0.75f, 0.75f, 0x00FF00, 0x00FF00, 1);
    t.x[0] = 0.25f; t.y[0] = 0.25f;
    t.x[1] = 0.75f; t.y[1] = 0.5f;
    t.x[2] = 0.75f; t.y[2] = 0.5f;
    t.x[3] = 0.25f; t.y[3] = 0.75f;
    tri.Draw({}, {}, {t});
    const auto green = Expected(0x00FF00);
    Check(Near(tri.Y(200, 180), green.y) && tri.Y(460, 120) == 100 && tri.Y(460, 240) == 100 && Near(tri.Y(460, 180), green.y),
          "triangle (repeated corner) filled to its edges only");

    Nv12 half(640, 360);
    half.Draw({}, {}, {Rect(0.25f, 0.25f, 0.75f, 0.75f, 0xFF0000, 0xFF0000, 0.5f)});
    Check(Near(half.Y(320, 180), (100 + red.y) / 2), "half transparent fill");
}

void CheckLinesAndDots() {
    Nv12 f(640, 360);
    f.Draw({HitCamSceneDot{0.2f, 0.2f, 0.03f, 0x00FF00}}, {HitCamSceneLine{0.1f, 0.5f, 0.9f, 0.5f, 0.02f, 0xFFFFFF, 1}}, {});
    Check(f.Y(320, 180) >= 233 && Near(f.U(320, 180), 128, 2), "white line");
    Check(f.Y(320, 170) == 100 && f.Y(40, 180) == 100, "line ends and edges where they should");
    const auto green = Expected(0x00FF00);
    Check(Near(f.Y(128, 72), green.y) && Near(f.V(128, 72), green.v), "dot in its colour");
    Check(f.Y(128, 90) == 100, "dot is round and small");
}

void CheckRobust() {
    Nv12 f(640, 360);
    const float nan = std::nanf("");
    f.Draw({HitCamSceneDot{nan, 0.5f, 0.1f, 0xFF0000}, HitCamSceneDot{0.5f, 0.5f, 5, 0xFF0000}},
           {HitCamSceneLine{0, 0, 1, 1, nan, 0xFFFFFF, 1}, HitCamSceneLine{1e9f, 0, 0, 0, 0.01f, 0xFFFFFF, 1}},
           {Rect(nan, 0, 1, 1, 0, 0, 1)});
    Check(f.data == Nv12(640, 360).data, "invalid items skipped");

    Nv12 clipped(640, 360);
    HitCamSceneQuad bowtie = Rect(0.1f, 0.1f, 0.9f, 0.9f, 0xFF0000, 0x0000FF, 1);
    std::swap(bowtie.x[2], bowtie.x[3]);   // crossed corners
    clipped.Draw({HitCamSceneDot{1.01f, -0.01f, 0.05f, 0xFF0000}}, {HitCamSceneLine{-5, 0.5f, 5, 0.5f, 0.05f, 0xFFFFFF, 1}},
                 {bowtie, Rect(-2, -2, 3, 3, 0x00FF00, 0x00FF00, 0.3f)});
    Check(clipped.Y(0, 180) > 200 && clipped.Y(639, 180) > 200, "shapes past the frame's edges clipped");

    Nv12 odd(641, 361);
    HitCam_TestSceneFrame(nullptr, 3, nullptr, 3, nullptr, 3, odd.data.data(), 641, 361);
    Check(true, "null arrays with counts ignored");
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

    const HitCamSceneLine line{0.1f, 0.5f, 0.9f, 0.5f, 0.02f, 0xFFFFFF, 1};
    HitCam_BridgeSetHandScene(bridge.handle, nullptr, 0, &line, 1, nullptr, 0);
    bridge.Publish(source);
    Check(bridge.out.packed != clean, "scene reaches the camera frame");
    Check(bridge.Preview() == cleanPreview, "preview stays clean");

    HitCam_BridgeSetHandScene(bridge.handle, nullptr, 0, nullptr, 0, nullptr, 0);
    bridge.Publish(source);
    Check(bridge.out.packed == clean, "empty scene clears it");

    HitCam_BridgeSetHandScene(bridge.handle, nullptr, 0, &line, 1, nullptr, 0);
    Sleep(1100);
    bridge.Publish(source);
    Check(bridge.out.packed == clean, "a scene not refreshed for a second is dropped");
}

void CheckSpeed() {
    const uint32_t w = 1920, h = 1080;
    std::vector<HitCamSceneQuad> quads;
    for (int i = 0; i < 4; ++i) quads.push_back(Rect(0.1f, 0.1f + i * 0.2f, 0.9f, 0.3f + i * 0.2f, 0x22D3EE, 0xF472B6, 0.5f));
    std::vector<HitCamSceneLine> lines;
    for (int i = 0; i < 5; ++i) lines.push_back(HitCamSceneLine{0.1f, 0.1f + i * 0.2f, 0.9f, 0.1f + i * 0.2f, 0.006f, 0xFFFFFF, 1});
    for (int i = 0; i < 42; ++i) lines.push_back(HitCamSceneLine{0.3f, 0.2f + i * 0.01f, 0.35f, 0.25f + i * 0.01f, 0.003f, 0xFFFFFF, 0.8f});
    std::vector<HitCamSceneDot> dots;
    for (int i = 0; i < 42; ++i) dots.push_back(HitCamSceneDot{0.2f + (i % 21) * 0.03f, 0.3f + (i / 21) * 0.3f, 0.006f, 0x22C55E});
    Nv12 f(w, h);
    const auto start = std::chrono::steady_clock::now();
    const int runs = 10;
    for (int i = 0; i < runs; ++i) f.Draw(dots, lines, quads);
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count() / runs;
    char what[128];
    std::snprintf(what, sizeof(what), "1080p: 4 fills over most of the frame, 47 lines, 42 dots: %.1f ms (< 10)", ms);
    Check(ms < 10, what);
}

int Run() {
    CheckFill();
    CheckLinesAndDots();
    CheckRobust();
    CheckBridge();
    CheckSpeed();
    std::printf(overlay_check::g_passed ? "ALL PASSED\n" : "SOME CHECKS FAILED\n");
    return overlay_check::g_passed ? 0 : 1;
}

}  // namespace scene_check
