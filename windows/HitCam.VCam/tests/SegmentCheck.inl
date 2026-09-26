// --segment: background blur and replacement from a person mask (HitCam_BridgeSetBackground, SetSegmentMask,
// SetBackgroundImage). Frames go through a preview-only bridge with a test output, like --overlay: no camera is
// touched. Included by VCamTest.cpp after OverlayCheck.inl (it reuses its Source, Output and TestBridge).

namespace segment_check {

using overlay_check::Source;
using overlay_check::TestBridge;

bool g_passed = true;

void Check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "OK" : "FAIL", what);
    g_passed = g_passed && ok;
}

constexpr uint32_t kWidth = 1280, kHeight = 720, kMaskWidth = 64, kMaskHeight = 36;

// Person on the left half of the frame, room on the right.
std::vector<uint8_t> LeftHalfMask() {
    std::vector<uint8_t> mask(kMaskWidth * kMaskHeight, 0);
    for (uint32_t y = 0; y < kMaskHeight; ++y) std::memset(mask.data() + y * kMaskWidth, 255, kMaskWidth / 2);
    return mask;
}

HitCamBackground Blur(float strength = 0.5f, int failClosed = 1) { return {1, strength, 0.2f, 0.0f, failClosed, 0}; }

// Mean absolute difference of neighbouring luma pixels in [x0, x1): the texture of the source pattern, gone when blurred.
double Texture(const overlay_check::Output& out, uint32_t x0, uint32_t x1) {
    double sum = 0;
    int count = 0;
    for (uint32_t y = 100; y < kHeight - 100; y += 7) {
        for (uint32_t x = x0; x + 1 < x1; x += 3) {
            sum += std::abs(out.Y(x + 1, y) - out.Y(x, y));
            ++count;
        }
    }
    return sum / count;
}

double Texture(Source& source, uint32_t x0, uint32_t x1) {
    double sum = 0;
    int count = 0;
    for (uint32_t y = 100; y < kHeight - 100; y += 7) {
        for (uint32_t x = x0; x + 1 < x1; x += 3) {
            sum += std::abs(source.Y(x + 1, y) - source.Y(x, y));
            ++count;
        }
    }
    return sum / count;
}

// Largest luma difference to the source in [x0, x1).
int Changed(const overlay_check::Output& out, Source& source, uint32_t x0, uint32_t x1) {
    int worst = 0;
    for (uint32_t y = 0; y < kHeight; y += 5) {
        for (uint32_t x = x0; x < x1; x += 5) worst = (std::max)(worst, std::abs(out.Y(x, y) - source.Y(x, y)));
    }
    return worst;
}

std::vector<uint8_t> DisplayPreview(void* handle, uint32_t* width, uint32_t* height) {
    uint64_t frame = 0;
    HitCam_BridgeDisplayPreviewInfo(handle, width, height, &frame);
    std::vector<uint8_t> pixels(static_cast<size_t>(*width) * *height * 4);
    if (*width == 0 || !HitCam_BridgeCopyDisplayPreview(handle, pixels.data(), *width * 4, *width, *height)) pixels.clear();
    return pixels;
}

void CheckOff() {
    TestBridge bridge;
    Source source(kWidth, kHeight, kWidth + 64);
    const auto mask = LeftHalfMask();
    HitCam_BridgeSetSegmentMask(bridge.handle, mask.data(), kMaskWidth, kMaskHeight);
    bridge.Publish(source);
    Check(bridge.out.frames == 1 && Changed(bridge.out, source, 0, kWidth) == 0 && HitCam_BridgeCompositeMs(bridge.handle) < 0,
          "background off: the frame goes out untouched, no GPU work");
}

void CheckBlur() {
    TestBridge bridge;
    Source source(kWidth, kHeight, kWidth + 64);
    const auto mask = LeftHalfMask();
    const HitCamBackground blur = Blur();
    HitCam_BridgeSetBackground(bridge.handle, &blur);
    HitCam_BridgeSetSegmentMask(bridge.handle, mask.data(), kMaskWidth, kMaskHeight);
    bridge.Publish(source);
    const auto& out = bridge.out;
    const double before = Texture(source, kWidth * 3 / 4 - 100, kWidth - 20), after = Texture(out, kWidth * 3 / 4 - 100, kWidth - 20);
    const int person = Changed(out, source, 20, kWidth / 2 - 60);
    char what[200];
    std::snprintf(what, sizeof(what), "blur: person kept (largest change %d), room texture %.1f -> %.1f, %.2f ms", person, before, after,
                  HitCam_BridgeCompositeMs(bridge.handle));
    Check(out.frames == 1 && person <= 2 && after < before / 4 && HitCam_BridgeCompositeMs(bridge.handle) >= 0, what);

    // The preview the analysis reads stays the real picture; the one the app shows has the effect.
    const auto real = bridge.Preview();
    uint32_t w = 0, h = 0;
    const auto shown = DisplayPreview(bridge.handle, &w, &h);
    int realChange = 0, shownChange = 0;
    for (uint32_t x = w * 3 / 4; x + 1 < w; ++x) {
        const size_t i = (static_cast<size_t>(h / 2) * w + x) * 4, j = i + 4;
        realChange += std::abs(real[j + 1] - real[i + 1]);
        shownChange += std::abs(shown[j + 1] - shown[i + 1]);
    }
    std::snprintf(what, sizeof(what), "preview for the analysis stays real (texture %d), the shown one is blurred (%d)", realChange, shownChange);
    Check(!real.empty() && shown.size() == real.size() && shownChange * 4 < realChange, what);
}

void CheckReplace() {
    TestBridge bridge;
    Source source(kWidth, kHeight, kWidth + 64);
    const auto mask = LeftHalfMask();
    // Pure red, 16x9.
    std::vector<uint8_t> image(16 * 9 * 4);
    for (size_t i = 0; i < image.size(); i += 4) {
        image[i] = 0;
        image[i + 1] = 0;
        image[i + 2] = 255;
        image[i + 3] = 255;
    }
    HitCamBackground replace = Blur();
    replace.mode = 2;
    HitCam_BridgeSetBackground(bridge.handle, &replace);
    HitCam_BridgeSetBackgroundImage(bridge.handle, image.data(), 16, 9, 16 * 4);
    HitCam_BridgeSetSegmentMask(bridge.handle, mask.data(), kMaskWidth, kMaskHeight);
    bridge.Publish(source);
    const auto& out = bridge.out;
    // BT.709 video range red: Y 63, Cb 102, Cr 240.
    const int y = out.Y(kWidth - 100, 300), cb = out.UV((kWidth - 100) / 2, 150, 0), cr = out.UV((kWidth - 100) / 2, 150, 1);
    char what[160];
    std::snprintf(what, sizeof(what), "replace: room is the picture (Y %d Cb %d Cr %d), person kept (largest change %d)", y, cb, cr,
                  Changed(out, source, 20, kWidth / 2 - 60));
    Check(std::abs(y - 63) <= 2 && std::abs(cb - 102) <= 2 && std::abs(cr - 240) <= 2 && Changed(out, source, 20, kWidth / 2 - 60) <= 2, what);
}

void CheckNoMask() {
    {
        TestBridge bridge;
        Source source(kWidth, kHeight, kWidth + 64);
        const HitCamBackground blur = Blur();
        HitCam_BridgeSetBackground(bridge.handle, &blur);
        bridge.Publish(source);
        const double before = Texture(source, 20, kWidth / 2 - 60), after = Texture(bridge.out, 20, kWidth / 2 - 60);
        char what[160];
        std::snprintf(what, sizeof(what), "no mask yet, fail closed: the whole frame is blurred (texture %.1f -> %.1f)", before, after);
        Check(after < before / 4, what);
    }
    {
        TestBridge bridge;
        Source source(kWidth, kHeight, kWidth + 64);
        const HitCamBackground open = Blur(0.5f, 0);
        HitCam_BridgeSetBackground(bridge.handle, &open);
        bridge.Publish(source);
        Check(Changed(bridge.out, source, 0, kWidth) == 0, "no mask yet, fail open: the frame as is");
    }
}

void CheckStale() {
    TestBridge bridge;
    Source source(kWidth, kHeight, kWidth + 64);
    const auto mask = LeftHalfMask();
    const HitCamBackground blur = Blur();
    HitCam_BridgeSetBackground(bridge.handle, &blur);
    HitCam_BridgeSetSegmentMask(bridge.handle, mask.data(), kMaskWidth, kMaskHeight);
    Sleep(700);  // past kMaskStaleMs: the analysis stopped
    bridge.Publish(source);
    const double before = Texture(source, 20, kWidth / 2 - 60), after = Texture(bridge.out, 20, kWidth / 2 - 60);
    char what[160];
    std::snprintf(what, sizeof(what), "stale mask: the person's side is blurred too (texture %.1f -> %.1f)", before, after);
    Check(after < before / 4, what);
}

void CheckSpeed() {
    TestBridge bridge;
    Source source(1920, 1080, 1920);
    std::vector<uint8_t> mask(256 * 256, 0);
    for (uint32_t y = 40; y < 256; ++y) std::memset(mask.data() + y * 256 + 80, 255, 96);
    const HitCamBackground blur = Blur(1.0f);
    HitCam_BridgeSetBackground(bridge.handle, &blur);
    double total = 0;
    int counted = 0;
    for (int i = 0; i < 40; ++i) {
        HitCam_BridgeSetSegmentMask(bridge.handle, mask.data(), 256, 256);
        bridge.Publish(source);
        if (i >= 10) {
            total += HitCam_BridgeCompositeMs(bridge.handle);
            ++counted;
        }
    }
    char what[120];
    std::snprintf(what, sizeof(what), "1080p background blur: %.2f ms per frame (upload, GPU, readback; limit 8 ms)", total / counted);
    Check(total / counted >= 0 && total / counted < 8, what);
}

int Run() {
    CheckOff();
    CheckBlur();
    CheckReplace();
    CheckNoMask();
    CheckStale();
    CheckSpeed();
    std::printf(g_passed ? "ALL PASSED\n" : "SOME CHECKS FAILED\n");
    return g_passed ? 0 : 1;
}

}  // namespace segment_check
