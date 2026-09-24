// --overlay: detection boxes burnt into the camera's frames (HitCam_BridgeSetOverlay). Frames go through a
// preview-only bridge whose camera output is replaced by a test callback (HitCam_BridgeTestOutput), so neither
// the Media Foundation section nor the DirectShow camera is touched. Included by VCamTest.cpp after ProcessCheck.inl.

namespace overlay_check {

bool g_passed = true;

void Check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "OK" : "FAIL", what);
    g_passed = g_passed && ok;
}

// An NV12 frame as the decoder gives it: rows of `pitch` bytes, padding rows between the planes.
struct Source {
    uint32_t width, height, pitch, lumaRows;
    std::vector<uint8_t> data;

    Source(uint32_t w, uint32_t h, uint32_t p, uint32_t padRows = 8)
        : width(w), height(h), pitch(p), lumaRows(h + padRows), data(static_cast<size_t>(p) * (h + padRows + h / 2), 0xEE) {
        for (uint32_t y = 0; y < h; ++y) {
            for (uint32_t x = 0; x < w; ++x) Luma()[static_cast<size_t>(y) * p + x] = static_cast<uint8_t>(20 + (x * 3 + y * 5) % 200);
        }
        for (uint32_t y = 0; y < h / 2; ++y) {
            for (uint32_t x = 0; x < w; ++x) Chroma()[static_cast<size_t>(y) * p + x] = static_cast<uint8_t>(40 + (x * 7 + y * 11) % 170);
        }
    }
    uint8_t* Luma() { return data.data(); }
    uint8_t* Chroma() { return data.data() + static_cast<size_t>(pitch) * lumaRows; }
    uint8_t Y(uint32_t x, uint32_t y) { return Luma()[static_cast<size_t>(y) * pitch + x]; }
    uint8_t UV(uint32_t cx, uint32_t cy, int channel) { return Chroma()[static_cast<size_t>(cy) * pitch + cx * 2 + channel]; }
};

// What the camera would have received.
struct Output {
    int frames = 0;
    const uint8_t* luma = nullptr;
    uint32_t pitch = 0, width = 0, height = 0;
    std::vector<uint8_t> packed;

    uint8_t Y(uint32_t x, uint32_t y) const { return packed[static_cast<size_t>(y) * width + x]; }
    uint8_t UV(uint32_t cx, uint32_t cy, int channel) const {
        return packed[static_cast<size_t>(width) * height + static_cast<size_t>(cy) * width + cx * 2 + channel];
    }
};

void __stdcall Receive(void* context, const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height) {
    auto* out = static_cast<Output*>(context);
    ++out->frames;
    out->luma = luma;
    out->pitch = pitch;
    out->width = width;
    out->height = height;
    out->packed.resize(static_cast<size_t>(width) * height * 3 / 2);
    for (uint32_t y = 0; y < height; ++y) std::memcpy(out->packed.data() + static_cast<size_t>(y) * width, luma + static_cast<size_t>(y) * pitch, width);
    uint8_t* c = out->packed.data() + static_cast<size_t>(width) * height;
    for (uint32_t y = 0; y < height / 2; ++y) std::memcpy(c + static_cast<size_t>(y) * width, chroma + static_cast<size_t>(y) * pitch, width);
}

struct TestBridge {
    void* handle = nullptr;
    Output out;
    TestBridge() {
        if (SUCCEEDED(HitCam_BridgeCreate(&handle))) HitCam_BridgeTestOutput(handle, &Receive, &out);
    }
    ~TestBridge() {
        if (handle) HitCam_BridgeDestroy(handle);
    }
    void Publish(Source& s) { HitCam_BridgeTestPublish(handle, s.Luma(), s.Chroma(), s.pitch, s.width, s.height); }
    std::vector<uint8_t> Preview() {
        uint32_t w = 0, h = 0;
        uint64_t frame = 0;
        HitCam_BridgePreviewInfo(handle, &w, &h, &frame);
        std::vector<uint8_t> pixels(static_cast<size_t>(w) * h * 4);
        if (w == 0 || !HitCam_BridgeCopyPreview(handle, pixels.data(), w * 4, w, h)) pixels.clear();
        return pixels;
    }
};

// Independent BT.709 video range conversion (floating point).
struct Yuv {
    int y, u, v;
};
Yuv Expected(uint32_t rgb) {
    const double r = (rgb >> 16 & 0xFF) / 255.0, g = (rgb >> 8 & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
    const double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
    const double u = (b - y) / 1.8556, v = (r - y) / 1.5748;
    auto round = [](double value, int low, int high) { return std::clamp(static_cast<int>(std::lround(value)), low, high); };
    return {round(16 + 219 * y, 16, 235), round(128 + 224 * u, 16, 240), round(128 + 224 * v, 16, 240)};
}

HitCamOverlayBox Box(float l, float t, float r, float b, uint32_t rgb, const std::vector<uint8_t>* label = nullptr, int lw = 0, int lh = 0) {
    return HitCamOverlayBox{l, t, r, b, rgb, label ? lw : 0, label ? lh : 0, label ? label->data() : nullptr};
}

std::vector<uint8_t> Label(int w, int h) {
    std::vector<uint8_t> alpha(static_cast<size_t>(w) * h);
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) alpha[static_cast<size_t>(y) * w + x] = static_cast<uint8_t>(((x / 3 + y / 3) % 2) ? (x % 5 == 0 ? 128 : 255) : 0);
    }
    return alpha;
}

// The expected picture: every pixel either untouched, box colour, or white text blended over it.
struct Expectation {
    int width, height;
    std::vector<int> colour;  // per pixel: -1 untouched, else index into boxes
    std::vector<int> alpha;   // per pixel: text alpha (0 = none)
    std::vector<Yuv> colours;

    Expectation(int w, int h) : width(w), height(h), colour(static_cast<size_t>(w) * h, -1), alpha(static_cast<size_t>(w) * h, 0) {}

    void Fill(int x0, int y0, int x1, int y1, int index) {
        for (int y = std::max(0, y0); y < std::min(height, y1); ++y) {
            for (int x = std::max(0, x0); x < std::min(width, x1); ++x) {
                colour[static_cast<size_t>(y) * width + x] = index;
                alpha[static_cast<size_t>(y) * width + x] = 0;
            }
        }
    }

    // Edges round to the nearest pixel, then to even pixels outward.
    static int Edge(float v, int size, bool up) {
        int p = static_cast<int>(std::lround(std::clamp(v, 0.0f, 1.0f) * size));
        p = up ? (p + 1) & ~1 : p & ~1;
        return std::clamp(p, 0, size);
    }

    void Add(const HitCamOverlayBox& b) {
        const int index = static_cast<int>(colours.size());
        colours.push_back(Expected(b.rgb));
        const int t = std::max(2, height / 360);
        const int x0 = Edge(b.left, width, false), x1 = Edge(b.right, width, true);
        const int y0 = Edge(b.top, height, false), y1 = Edge(b.bottom, height, true);
        Fill(x0, y0, x1, y0 + t, index);
        Fill(x0, y1 - t, x1, y1, index);
        Fill(x0, y0, x0 + t, y1, index);
        Fill(x1 - t, y0, x1, y1, index);
        if (!b.label) return;
        const int tw = b.labelWidth + 2 * t, th = b.labelHeight + 2 * t;
        const int tx = std::max(0, std::min(x0, width - tw));
        const int ty = std::max(0, std::min(y0 - th >= 0 ? y0 - th : y0, height - th));
        Fill(tx, ty, tx + tw, ty + th, index);
        for (int y = 0; y < b.labelHeight; ++y) {
            for (int x = 0; x < b.labelWidth; ++x) {
                const int px = tx + t + x, py = ty + t + y;
                if (px < width && py < height) alpha[static_cast<size_t>(py) * width + px] = b.label[static_cast<size_t>(y) * b.labelWidth + x];
            }
        }
    }

    static int Blend(int from, int to, int a) {
        const int d = (to - from) * a;
        return from + (d >= 0 ? (d + 127) / 255 : (d - 127) / 255);
    }

    // Counts wrong luma and chroma samples: changed where untouched expected, not the box colour, text not as blended.
    int Compare(Source& src, const Output& out, int* untouchedChanged, int* colourWrong, int* textWrong) const {
        *untouchedChanged = *colourWrong = *textWrong = 0;
        for (int y = 0; y < height; ++y) {
            for (int x = 0; x < width; ++x) {
                const size_t i = static_cast<size_t>(y) * width + x;
                const int got = out.Y(x, y);
                if (colour[i] < 0) {
                    *untouchedChanged += got != src.Y(x, y);
                } else if (alpha[i] == 0) {
                    *colourWrong += got != colours[colour[i]].y;
                } else {
                    *textWrong += got != Blend(colours[colour[i]].y, 235, alpha[i]) || got <= colours[colour[i]].y;
                }
            }
        }
        for (int cy = 0; cy < height / 2; ++cy) {
            for (int cx = 0; cx < width / 2; ++cx) {
                int index = -1, sum = 0;
                for (int y = cy * 2; y < cy * 2 + 2; ++y) {
                    for (int x = cx * 2; x < cx * 2 + 2; ++x) {
                        const size_t i = static_cast<size_t>(y) * width + x;
                        if (colour[i] >= 0) index = colour[i];
                        sum += alpha[i];
                    }
                }
                for (int c = 0; c < 2; ++c) {
                    const int got = out.UV(cx, cy, c);
                    if (index < 0) {
                        *untouchedChanged += got != src.UV(cx, cy, c);
                    } else {
                        const int base = c == 0 ? colours[index].u : colours[index].v;
                        const int want = sum == 0 ? base : Blend(base, 128, (sum + 2) / 4);
                        *(sum == 0 ? colourWrong : textWrong) += got != want;
                    }
                }
            }
        }
        return *untouchedChanged + *colourWrong + *textWrong;
    }
};

bool Identical(Source& src, const Output& out) {
    if (out.width != src.width || out.height != src.height) return false;
    for (uint32_t y = 0; y < src.height; ++y) {
        for (uint32_t x = 0; x < src.width; ++x) {
            if (out.Y(x, y) != src.Y(x, y)) return false;
        }
    }
    for (uint32_t y = 0; y < src.height / 2; ++y) {
        for (uint32_t x = 0; x < src.width / 2; ++x) {
            if (out.UV(x, y, 0) != src.UV(x, y, 0) || out.UV(x, y, 1) != src.UV(x, y, 1)) return false;
        }
    }
    return true;
}

void CheckScene(const char* name, uint32_t width, uint32_t height, const std::vector<HitCamOverlayBox>& boxes) {
    TestBridge bridge;
    Source src(width, height, width + 64);
    const std::vector<uint8_t> original = src.data;
    bridge.Publish(src);
    const std::vector<uint8_t> cleanPreview = bridge.Preview();

    HitCam_BridgeSetOverlay(bridge.handle, boxes.data(), static_cast<int32_t>(boxes.size()));
    bridge.Publish(src);
    Expectation expect(static_cast<int>(width), static_cast<int>(height));
    for (const auto& b : boxes) expect.Add(b);
    int untouched = 0, colour = 0, text = 0;
    const int wrong = expect.Compare(src, bridge.out, &untouched, &colour, &text);
    char what[256];
    std::snprintf(what, sizeof(what), "%s %ux%u: outline/tab colour exact (%d wrong), text blended white (%d wrong), rest untouched (%d changed)",
                  name, width, height, colour, text, untouched);
    Check(bridge.out.frames == 2 && wrong == 0, what);
    std::snprintf(what, sizeof(what), "%s: preview stays clean, decoder's buffer not written", name);
    Check(!cleanPreview.empty() && bridge.Preview() == cleanPreview && src.data == original, what);
}

void CheckBasics() {
    const auto label = Label(40, 10);
    // 640x360: thickness 2. Red box in the middle with a label above.
    CheckScene("red box, label above", 640, 360, {Box(0.25f, 0.25f, 0.75f, 0.75f, 0xFF0000, &label, 40, 10)});
    // Several colours, 1080p (thickness 3: odd, chroma of the inner edge is coloured too).
    const auto wide = Label(120, 24);
    CheckScene("three boxes", 1920, 1080,
               {Box(0.1f, 0.3f, 0.3f, 0.6f, 0x00FF00, &wide, 120, 24), Box(0.5f, 0.1f, 0.7f, 0.4f, 0x0000FF, &label, 40, 10),
                Box(0.4f, 0.7f, 0.9f, 0.9f, 0xFFFF00)});
    // Clamping: past the left/top/bottom (label inside the top, no room above), past the right with a label wider
    // than the room left, bottom-right corner; odd coordinates.
    CheckScene("clamped at the borders", 640, 360,
               {Box(-0.2f, -0.1f, 0.1f, 1.3f, 0xFF00FF, &label, 40, 10), Box(0.9f, 0.5f, 1.2f, 0.8f, 0x00FFFF, &wide, 120, 24),
                Box(0.3f, 0.9f, 0.5f, 5.0f, 0xFF8000, &label, 40, 10), Box(0.3013f, 0.2017f, 0.4011f, 0.3021f, 0x808080)});
    // Label larger than the frame: clipped, no crash.
    const auto huge = Label(700, 50);
    CheckScene("label wider than the frame", 640, 360, {Box(0.5f, 0.5f, 0.6f, 0.6f, 0x404040, &huge, 700, 50)});
    // Portrait, thickness max(2, 1920/360) = 5.
    CheckScene("portrait", 1080, 1920, {Box(0.2f, 0.2f, 0.8f, 0.5f, 0x2080FF, &wide, 120, 24)});
}

void CheckNoOverlay() {
    TestBridge bridge;
    Source src(640, 360, 704);
    bridge.Publish(src);
    Check(bridge.out.frames == 1 && Identical(src, bridge.out) && bridge.out.luma == src.Luma() && bridge.out.pitch == src.pitch,
          "no overlay: camera gets the decoder's frame itself (no copy)");

    const auto label = Label(40, 10);
    const HitCamOverlayBox box = Box(0.25f, 0.25f, 0.75f, 0.75f, 0xFF0000, &label, 40, 10);
    HitCam_BridgeSetOverlay(bridge.handle, &box, 1);
    bridge.Publish(src);
    const bool drawn = !Identical(src, bridge.out) && bridge.out.luma != src.Luma();
    HitCam_BridgeSetOverlay(bridge.handle, nullptr, 0);
    bridge.Publish(src);
    Check(drawn && Identical(src, bridge.out) && bridge.out.luma == src.Luma(), "count 0 clears: bit-identical, no copy");

    HitCam_BridgeSetOverlay(bridge.handle, &box, 1);
    Sleep(500);
    bridge.Publish(src);
    const bool fresh = !Identical(src, bridge.out);
    Sleep(700);
    bridge.Publish(src);
    Check(fresh && Identical(src, bridge.out) && bridge.out.luma == src.Luma(), "boxes not refreshed for over 1 s are dropped");

    // Refreshing keeps them.
    HitCam_BridgeSetOverlay(bridge.handle, &box, 1);
    Sleep(700);
    HitCam_BridgeSetOverlay(bridge.handle, &box, 1);
    Sleep(700);
    bridge.Publish(src);
    Check(!Identical(src, bridge.out), "boxes refreshed within 1 s stay");

    // Oversized frames: the Media Foundation camera does not get them; nothing is drawn or copied.
    Source big(2000, 1000, 2048);
    bridge.Publish(big);
    Check(Identical(big, bridge.out) && bridge.out.luma == big.Luma(), "oversized frame: untouched");

    // Invalid input: null boxes with a count, NaN coordinates, bogus label sizes.
    HitCam_BridgeSetOverlay(bridge.handle, nullptr, 5);
    bridge.Publish(src);
    const bool nullCleared = Identical(src, bridge.out);
    HitCamOverlayBox bad[2] = {Box(NAN, 0.1f, 0.5f, 0.5f, 0xFF0000), Box(0.1f, 0.1f, 0.5f, 0.5f, 0x00FF00)};
    bad[1].labelWidth = 100000;
    bad[1].labelHeight = 100000;
    bad[1].label = label.data();
    HitCam_BridgeSetOverlay(bridge.handle, bad, 2);
    bridge.Publish(src);
    Expectation expect(640, 360);
    expect.Add(Box(0.1f, 0.1f, 0.5f, 0.5f, 0x00FF00));
    int u = 0, c = 0, t = 0;
    Check(nullCleared && expect.Compare(src, bridge.out, &u, &c, &t) == 0, "null boxes clear; NaN box and oversized label ignored");
}

void CheckLimit() {
    TestBridge bridge;
    Source src(640, 360, 640);
    std::vector<HitCamOverlayBox> boxes;
    for (int i = 0; i < 100; ++i) boxes.push_back(Box(i * 6 / 640.0f, 100 / 360.0f, (i * 6 + 4) / 640.0f, 104 / 360.0f, 0xFF0000));
    HitCam_BridgeSetOverlay(bridge.handle, boxes.data(), 100);
    bridge.Publish(src);
    Expectation expect(640, 360);
    for (int i = 0; i < 64; ++i) expect.Add(boxes[i]);
    int u = 0, c = 0, t = 0;
    Check(expect.Compare(src, bridge.out, &u, &c, &t) == 0, "100 boxes: the first 64 drawn, the rest ignored");
}

// Picture processing on: the overlay goes on top of the processed (packed) frame.
void CheckWithProcessing() {
    TestBridge bridge;
    Source src(640, 360, 704);
    HitCamProcessing s{};
    s.brightness = 0.3f;
    HitCam_BridgeSetProcessing(bridge.handle, &s);
    bridge.Publish(src);
    Output processed = bridge.out;
    const bool changed = !Identical(src, processed);
    const HitCamOverlayBox box = Box(0.25f, 0.25f, 0.75f, 0.75f, 0x00FF00);
    HitCam_BridgeSetOverlay(bridge.handle, &box, 1);
    bridge.Publish(src);
    // Compare against the processed frame: re-pack it as a source.
    Source base(640, 360, 640, 0);
    std::memcpy(base.data.data(), processed.packed.data(), processed.packed.size());
    Expectation expect(640, 360);
    expect.Add(box);
    int u = 0, c = 0, t = 0;
    const int wrong = expect.Compare(base, bridge.out, &u, &c, &t);
    char what[160];
    std::snprintf(what, sizeof(what), "with brightness +0.3: box drawn over the processed frame (%d wrong, %d changed elsewhere)", c + t, u);
    Check(changed && wrong == 0, what);
}

void CheckSpeed() {
    TestBridge bridge;
    Source src(1920, 1080, 1920 + 128);
    const auto label = Label(200, 30);
    std::vector<HitCamOverlayBox> boxes;
    for (int i = 0; i < 20; ++i) {
        const float x = 0.05f + (i % 5) * 0.18f, y = 0.1f + (i / 5) * 0.22f;
        boxes.push_back(Box(x, y, x + 0.15f, y + 0.18f, 0x10E060 + i * 0x0A0000, &label, 200, 30));
    }
    auto measure = [&](int count) {
        HitCam_BridgeSetOverlay(bridge.handle, boxes.data(), count);
        for (int i = 0; i < 5; ++i) bridge.Publish(src);
        LARGE_INTEGER f, a, b;
        QueryPerformanceFrequency(&f);
        QueryPerformanceCounter(&a);
        for (int i = 0; i < 50; ++i) bridge.Publish(src);
        QueryPerformanceCounter(&b);
        return (b.QuadPart - a.QuadPart) * 1000.0 / f.QuadPart / 50;
    };
    const double without = measure(0);
    const double with = measure(20);
    char what[200];
    std::snprintf(what, sizeof(what), "1080p, 20 labelled boxes: %.2f ms per frame vs %.2f without (overlay + copy %.2f ms; incl. preview and test copy)",
                  with, without, with - without);
    Check(with - without < 4.0, what);
}

// Decoded H.264 through the whole path: the camera output carries the box, exact at its edges.
void CheckDecoded() {
    TestEncoder encoder;
    encoder.UseScene();
    TestBridge bridge;
    if (!bridge.handle || FAILED(encoder.Initialize(1280, 720))) {
        Check(false, "decoder bridge for the decoded path");
        return;
    }
    const HitCamOverlayBox box = Box(0.2f, 0.3f, 0.6f, 0.8f, 0xFF0000);
    const Yuv red = Expected(0xFF0000);
    const bool decoded = process_check::Decode(bridge.handle, encoder, 0, [&] {
        HitCam_BridgeSetOverlay(bridge.handle, &box, 1);
        return bridge.out.frames >= 5;
    }, 20'000);
    const Output& out = bridge.out;
    bool edges = decoded && out.width == 1280 && out.height == 720;
    if (edges) {
        // Box 256..768 x 216..576, thickness 2.
        for (uint32_t x = 256; x < 768; x += 7) edges = edges && out.Y(x, 216) == red.y && out.Y(x, 575) == red.y;
        for (uint32_t y = 216; y < 576; y += 7) edges = edges && out.Y(256, y) == red.y && out.Y(767, y) == red.y;
        edges = edges && out.UV(128, 108, 0) == red.u && out.UV(128, 108, 1) == red.v && out.Y(400, 400) != red.y;
    }
    char what[128];
    std::snprintf(what, sizeof(what), "decoded H.264 1280x720: box in the camera frame (%d frames)", out.frames);
    Check(edges, what);
}

int Run() {
    CheckBasics();
    CheckNoOverlay();
    CheckLimit();
    CheckWithProcessing();
    CheckSpeed();
    CheckDecoded();
    std::printf(g_passed ? "ALL PASSED\n" : "SOME CHECKS FAILED\n");
    return g_passed ? 0 : 1;
}

}  // namespace overlay_check
