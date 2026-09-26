// --frame: auto-framing crop (HitCam_BridgeSetFraming) through a preview-only bridge with a test output, like --overlay:
// no camera is touched. Included by VCamTest.cpp after SegmentCheck.inl (it reuses --overlay's Source and TestBridge).

namespace frame_check {

using overlay_check::Source;
using overlay_check::TestBridge;

bool g_passed = true;

void Check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "OK" : "FAIL", what);
    g_passed = g_passed && ok;
}

constexpr uint32_t kWidth = 1280, kHeight = 720;

// A smooth horizontal and vertical ramp, so a crop's position can be read back from the values.
void Ramp(Source& source) {
    for (uint32_t y = 0; y < kHeight; ++y) {
        for (uint32_t x = 0; x < kWidth; ++x) source.Luma()[static_cast<size_t>(y) * source.pitch + x] = static_cast<uint8_t>(16 + x * 200 / kWidth);
    }
    for (uint32_t y = 0; y < kHeight / 2; ++y) {
        for (uint32_t x = 0; x < kWidth; x += 2) {
            source.Chroma()[static_cast<size_t>(y) * source.pitch + x] = static_cast<uint8_t>(16 + y * 200 / (kHeight / 2));
            source.Chroma()[static_cast<size_t>(y) * source.pitch + x + 1] = 128;
        }
    }
}

void CheckCentre() {
    TestBridge bridge;
    Source source(kWidth, kHeight, kWidth + 64);
    Ramp(source);
    const HitCamFraming centre{0.25f, 0.25f, 0.75f, 0.75f};
    HitCam_BridgeSetFraming(bridge.handle, &centre);
    bridge.Publish(source);
    const auto& out = bridge.out;
    // The output's left edge shows the source a quarter in, its right edge three quarters in; so do the rows.
    const int left = out.Y(2, 360), right = out.Y(kWidth - 3, 360);
    const int expectedLeft = source.Y(kWidth / 4, 360), expectedRight = source.Y(kWidth * 3 / 4, 360);
    const int top = out.UV(320, 1, 0), bottom = out.UV(320, kHeight / 2 - 2, 0);
    const int expectedTop = source.UV(320, kHeight / 8, 0), expectedBottom = source.UV(320, kHeight * 3 / 8, 0);
    char what[200];
    std::snprintf(what, sizeof(what), "centre crop: columns %d..%d (source %d..%d), rows %d..%d (source %d..%d)", left, right,
                  expectedLeft, expectedRight, top, bottom, expectedTop, expectedBottom);
    Check(std::abs(left - expectedLeft) <= 2 && std::abs(right - expectedRight) <= 2 && std::abs(top - expectedTop) <= 2 &&
              std::abs(bottom - expectedBottom) <= 2,
          what);
}

void CheckHiddenFaceZooms() {
    TestBridge bridge;
    Source source(kWidth, kHeight, kWidth + 64);
    Ramp(source);
    // A face filled black in the source's centre: after a 2x crop of the centre it covers twice as much of the output.
    HitCamFaceRegion face{0.45f, 0.45f, 0.55f, 0.55f, 2, 1.0f, 0x000000, 0, 0};
    HitCam_BridgeSetFaceRegions(bridge.handle, &face, 1);
    const HitCamFraming centre{0.25f, 0.25f, 0.75f, 0.75f};
    HitCam_BridgeSetFraming(bridge.handle, &centre);
    bridge.Publish(source);
    const auto& out = bridge.out;
    // The face spans 0.4..0.6 of the output now; its middle and near its edges are dark, outside it is not.
    const bool covered = out.Y(kWidth / 2, kHeight / 2) < 24 && out.Y(static_cast<uint32_t>(kWidth * 0.42), kHeight / 2) < 24 &&
                         out.Y(static_cast<uint32_t>(kWidth * 0.58), kHeight / 2) < 24;
    const bool outside = out.Y(static_cast<uint32_t>(kWidth * 0.35), kHeight / 2) > 40;
    Check(covered && outside, "a hidden face is cropped and zoomed with the frame, still covered");
}

void CheckOffAndStale() {
    {
        TestBridge bridge;
        Source source(kWidth, kHeight, kWidth + 64);
        Ramp(source);
        const HitCamFraming whole{0, 0, 1, 1};
        HitCam_BridgeSetFraming(bridge.handle, &whole);
        bridge.Publish(source);
        int worst = 0;
        for (uint32_t x = 0; x < kWidth; x += 7) worst = (std::max)(worst, std::abs(bridge.out.Y(x, 300) - source.Y(x, 300)));
        Check(worst == 0, "whole frame: no crop, the frame as is");
    }
    {
        TestBridge bridge;
        Source source(kWidth, kHeight, kWidth + 64);
        Ramp(source);
        const HitCamFraming centre{0.25f, 0.25f, 0.75f, 0.75f};
        HitCam_BridgeSetFraming(bridge.handle, &centre);
        Sleep(1200);  // the app stopped sending: back to the whole frame
        bridge.Publish(source);
        Check(std::abs(bridge.out.Y(2, 360) - source.Y(2, 360)) <= 1, "framing not refreshed for a second: the whole frame");
    }
}

int Run() {
    CheckCentre();
    CheckHiddenFaceZooms();
    CheckOffAndStale();
    std::printf(g_passed ? "ALL PASSED\n" : "SOME CHECKS FAILED\n");
    return g_passed ? 0 : 1;
}

}  // namespace frame_check
