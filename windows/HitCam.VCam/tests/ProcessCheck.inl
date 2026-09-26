// --process: the decoder bridge's picture processing without any camera output. Synthetic NV12 frames go through
// HitCam_BridgeProcessFrame; decoded H.264 goes through a bridge whose frames reach only its preview
// (HitCam_BridgePreviewOnly), so neither the Media Foundation section nor the DirectShow camera is touched.
// Included by VCamTest.cpp.

namespace process_check {

bool g_passed = true;

void Check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "OK" : "FAIL", what);
    g_passed = g_passed && ok;
}

struct Frame {
    uint32_t width = 0, height = 0;
    std::vector<uint8_t> data;

    Frame(uint32_t w, uint32_t h, uint8_t luma = 100, uint8_t chroma = 128)
        : width(w), height(h), data(static_cast<size_t>(w) * h * 3 / 2, chroma) {
        std::memset(data.data(), luma, static_cast<size_t>(w) * h);
    }
    uint8_t& Y(uint32_t x, uint32_t y) { return data[static_cast<size_t>(y) * width + x]; }
    // U at even offsets, V at odd, per chroma pixel (x, y) of width/2 x height/2.
    uint8_t& UV(uint32_t x, uint32_t y, int channel) {
        return data[static_cast<size_t>(width) * height + static_cast<size_t>(y) * width + x * 2 + channel];
    }
    uint8_t* Chroma() { return data.data() + static_cast<size_t>(width) * height; }
};

class Noise {
public:
    int Next(int amplitude) {
        seed_ = seed_ * 1664525u + 1013904223u;
        return static_cast<int>(seed_ >> 24) % (2 * amplitude + 1) - amplitude;
    }
    void Add(Frame& frame, int luma, int chroma) {
        const size_t lumaSize = static_cast<size_t>(frame.width) * frame.height;
        for (size_t i = 0; i < frame.data.size(); ++i) {
            const int amplitude = i < lumaSize ? luma : chroma;
            if (amplitude > 0) frame.data[i] = static_cast<uint8_t>(std::clamp(frame.data[i] + Next(amplitude), 16, 240));
        }
    }

private:
    uint32_t seed_ = 12345;
};

double LumaMean(Frame& frame, uint32_t x0 = 0, uint32_t x1 = 0) {
    if (x1 == 0) x1 = frame.width;
    double sum = 0;
    for (uint32_t y = 0; y < frame.height; ++y) {
        for (uint32_t x = x0; x < x1; ++x) sum += frame.Y(x, y);
    }
    return sum / (static_cast<double>(x1 - x0) * frame.height);
}

double LumaDeviation(Frame& frame) {
    const double mean = LumaMean(frame);
    double squares = 0;
    for (uint32_t y = 0; y < frame.height; ++y) {
        for (uint32_t x = 0; x < frame.width; ++x) squares += (frame.Y(x, y) - mean) * (frame.Y(x, y) - mean);
    }
    return std::sqrt(squares / (static_cast<double>(frame.width) * frame.height));
}

HitCamProcessing Neutral() { return HitCamProcessing{}; }

struct Bridge {
    void* handle = nullptr;
    Bridge() { HitCam_BridgeCreate(&handle); }
    ~Bridge() {
        if (handle) HitCam_BridgeDestroy(handle);
    }
    void Set(const HitCamProcessing& settings) { HitCam_BridgeSetProcessing(handle, &settings); }
    void Process(Frame& frame) { HitCam_BridgeProcessFrame(handle, frame.data.data(), frame.width, frame.height); }
    double GpuMs() {
        double gpu = 0, artifact = 0;
        int error = 0;
        HitCam_BridgeProcessingStats(handle, &gpu, &artifact, &error);
        return gpu;
    }
};

// A colourful noisy test picture.
Frame Picture(uint32_t width, uint32_t height) {
    Frame frame(width, height);
    for (uint32_t y = 0; y < height; ++y) {
        for (uint32_t x = 0; x < width; ++x) frame.Y(x, y) = static_cast<uint8_t>(16 + (x * 7 + y * 3) % 220);
    }
    for (uint32_t y = 0; y < height / 2; ++y) {
        for (uint32_t x = 0; x < width / 2; ++x) {
            frame.UV(x, y, 0) = static_cast<uint8_t>(40 + (x * 5) % 180);
            frame.UV(x, y, 1) = static_cast<uint8_t>(40 + (y * 3) % 180);
        }
    }
    Noise noise;
    noise.Add(frame, 5, 3);
    return frame;
}

void CheckNeutral() {
    Bridge bridge;
    const Frame original = Picture(1280, 720);
    Frame frame = original;
    bridge.Set(Neutral());
    bridge.Process(frame);
    Check(frame.data == original.data && bridge.GpuMs() < 0, "neutral settings: frame bit-identical, no GPU work");

    // Back to neutral after processing: untouched again.
    HitCamProcessing some = Neutral();
    some.temporalStrength = 50;
    some.saturation = 0.5f;
    bridge.Set(some);
    Frame processed = original;
    bridge.Process(processed);
    bridge.Set(Neutral());
    frame = original;
    bridge.Process(frame);
    Check(processed.data != original.data && frame.data == original.data && bridge.GpuMs() < 0,
          "neutral again after processing: bit-identical");

    // Out-of-range and NaN values are clamped, not trusted.
    HitCamProcessing wild = Neutral();
    wild.temporalStrength = 1000;
    wild.artifactReduction = 7;
    wild.brightness = std::nanf("");
    wild.saturation = -5;
    bridge.Set(wild);
    frame = original;
    bridge.Process(frame);
    bool gray = true;
    for (uint32_t i = 0; i < frame.width * frame.height / 2; ++i) gray = gray && frame.Chroma()[i] == 128;
    Check(gray && bridge.GpuMs() >= 0, "out-of-range settings clamped (saturation -5 -> -1, NaN brightness -> 0)");
}

void CheckTemporal() {
    Bridge bridge;
    HitCamProcessing settings = Neutral();
    settings.temporalStrength = 70;
    bridge.Set(settings);
    Noise noise;
    double inputDeviation = 0, outputDeviation = 0, outputMean = 0;
    for (int i = 0; i < 30; ++i) {
        Frame frame(1280, 720, 100, 128);
        noise.Add(frame, 12, 6);
        inputDeviation = LumaDeviation(frame);
        bridge.Process(frame);
        outputDeviation = LumaDeviation(frame);
        outputMean = LumaMean(frame);
    }
    char what[160];
    std::snprintf(what, sizeof(what), "temporal 70 on static noise: std dev %.2f -> %.2f (%.0f%% less, need >= 50%%), mean %.1f",
                  inputDeviation, outputDeviation, 100 * (1 - outputDeviation / inputDeviation), outputMean);
    Check(outputDeviation <= inputDeviation * 0.5 && std::abs(outputMean - 100) < 1.5, what);
}

void CheckNoGhosting() {
    Bridge bridge;
    HitCamProcessing settings = Neutral();
    settings.temporalStrength = 100;
    bridge.Set(settings);
    Noise noise;
    constexpr uint32_t kWidth = 1280, kHeight = 720, kBar = 40, kStep = 16;
    constexpr int kFrames = 40;
    Frame frame(kWidth, kHeight);
    for (int i = 0; i < kFrames; ++i) {
        frame = Frame(kWidth, kHeight, 100, 128);
        noise.Add(frame, 8, 4);
        const uint32_t x0 = 100 + i * kStep;
        for (uint32_t y = 0; y < kHeight; ++y) {
            for (uint32_t x = x0; x < x0 + kBar; ++x) frame.Y(x, y) = 220;
        }
        for (uint32_t y = 0; y < kHeight / 2; ++y) {
            for (uint32_t x = x0 / 2; x < (x0 + kBar) / 2; ++x) frame.UV(x, y, 1) = 200;
        }
        bridge.Process(frame);
    }
    // Where the bar was 2 or more frames ago (and not since): background again. Worst column mean.
    const uint32_t last = 100 + (kFrames - 1) * kStep;
    const uint32_t trailEnd = last - kStep;  // the bar one frame ago started here
    double worst = 0, worstChroma = 0;
    for (uint32_t x = 100; x < trailEnd; ++x) {
        double sum = 0;
        for (uint32_t y = 0; y < kHeight; ++y) sum += frame.Y(x, y);
        worst = std::max(worst, std::abs(sum / kHeight - 100));
        double v = 0;
        for (uint32_t y = 0; y < kHeight / 2; ++y) v += frame.UV(x / 2, y, 1);
        worstChroma = std::max(worstChroma, std::abs(v / (kHeight / 2) - 128));
    }
    // And the bar itself is complete where it is now.
    const double bar = LumaMean(frame, last + 2, last + kBar - 2);
    char what[200];
    std::snprintf(what, sizeof(what), "temporal 100, moving bar: trail luma off by <= %.2f, chroma <= %.2f (limit 3), bar %.1f (220)",
                  worst, worstChroma, bar);
    Check(worst <= 3 && worstChroma <= 3 && std::abs(bar - 220) < 3, what);
}

void CheckAdjustments() {
    const uint32_t width = 640, height = 360;
    auto run = [&](const HitCamProcessing& settings, Frame frame) {
        Bridge bridge;
        bridge.Set(settings);
        bridge.Process(frame);
        return frame;
    };
    {
        HitCamProcessing s = Neutral();
        s.saturation = -1;
        Frame out = run(s, Picture(width, height));
        bool gray = true;
        for (uint32_t i = 0; i < width * height / 2; ++i) gray = gray && out.Chroma()[i] == 128;
        Frame original = Picture(width, height);
        const bool lumaKept = std::equal(out.data.begin(), out.data.begin() + width * height, original.data.begin());
        Check(gray && lumaKept, "saturation -1: chroma exactly 128, luma untouched");
    }
    {
        HitCamProcessing s = Neutral();
        s.brightness = 0.3f;
        Frame original = Picture(width, height);
        Frame out = run(s, original);
        const double before = LumaMean(original), after = LumaMean(out);
        char what[128];
        std::snprintf(what, sizeof(what), "brightness +0.3: mean luma %.1f -> %.1f", before, after);
        Check(after > before + 8, what);
    }
    {
        // A soft (blurred) vertical edge 60 -> 180 around x = 320.
        Frame edge(width, height, 60);
        for (uint32_t y = 0; y < height; ++y) {
            for (uint32_t x = 0; x < width; ++x) {
                const double t = std::clamp((static_cast<int>(x) - 317) / 6.0, 0.0, 1.0);
                edge.Y(x, y) = static_cast<uint8_t>(std::lround(60 + 120 * t * t * (3 - 2 * t)));
            }
        }
        HitCamProcessing s = Neutral();
        s.sharpness = 1;
        Frame out = run(s, edge);
        const int row = height / 2;
        auto contrast = [&](Frame& f) { return f.Y(322, row) - f.Y(318, row); };
        auto steepest = [&](Frame& f) {
            int best = 0;
            for (uint32_t x = 300; x < 340; ++x) best = std::max(best, f.Y(x + 1, row) - f.Y(x, row));
            return best;
        };
        const bool flatKept = out.Y(100, row) == 60 && out.Y(500, row) == 180;
        char what[160];
        std::snprintf(what, sizeof(what), "sharpness 1: edge contrast %d -> %d, steepest step %d -> %d, flat areas kept",
                      contrast(edge), contrast(out), steepest(edge), steepest(out));
        Check(contrast(out) > contrast(edge) && steepest(out) > steepest(edge) && flatKept, what);
    }
    {
        Frame original(width, height);
        for (uint32_t y = 0; y < height; ++y) {
            for (uint32_t x = 0; x < width; ++x) original.Y(x, y) = x < width / 2 ? 50 : 200;
        }
        HitCamProcessing s = Neutral();
        s.shadows = 0.5f;
        Frame out = run(s, original);
        const int dark = out.Y(10, 10) - 50, bright = out.Y(width - 10, 10) - 200;
        char what[128];
        std::snprintf(what, sizeof(what), "shadows +0.5: dark 50 -> %d (+%d), bright 200 -> %d (+%d)", out.Y(10, 10), dark,
                      out.Y(width - 10, 10), bright);
        Check(dark > 3 && dark > bright * 2, what);
    }
    {
        Frame original(width, height);
        for (uint32_t y = 0; y < height; ++y) {
            for (uint32_t x = 0; x < width; ++x) original.Y(x, y) = x < width / 2 ? 50 : 215;
        }
        HitCamProcessing s = Neutral();
        s.highlights = -0.5f;
        s.contrast = 0.3f;
        Frame out = run(s, original);
        char what[128];
        std::snprintf(what, sizeof(what), "highlights -0.5, contrast +0.3: 50 -> %d, 215 -> %d", out.Y(10, 10), out.Y(width - 10, 10));
        Check(out.Y(width - 10, 10) < 215 && out.Y(10, 10) < 50, what);
    }
}

// Picture enhancement: adaptive sharpening, clarity, vibrance.
void CheckEnhancement() {
    const uint32_t width = 640, height = 360;
    auto run = [&](const HitCamProcessing& settings, Frame frame) {
        Bridge bridge;
        bridge.Set(settings);
        bridge.Process(frame);
        return frame;
    };
    const int row = height / 2;
    {
        // A soft edge 60 -> 180 around x = 320 over flat areas with faint noise (+-1: what compression leaves).
        Frame edge(width, height, 60);
        for (uint32_t y = 0; y < height; ++y) {
            for (uint32_t x = 0; x < width; ++x) {
                const double t = std::clamp((static_cast<int>(x) - 317) / 6.0, 0.0, 1.0);
                edge.Y(x, y) = static_cast<uint8_t>(std::lround(60 + 120 * t * t * (3 - 2 * t)));
            }
        }
        Noise noise;
        noise.Add(edge, 1, 0);
        HitCamProcessing s = Neutral();
        s.detail = 1;
        Frame out = run(s, edge);
        auto steepest = [&](Frame& f) {
            int best = 0;
            for (uint32_t x = 300; x < 340; ++x) best = std::max(best, f.Y(x + 1, row) - f.Y(x, row));
            return best;
        };
        int changedFlat = 0;
        for (uint32_t y = 0; y < height; ++y) {
            for (uint32_t x = 20; x < 200; ++x) changedFlat += out.Y(x, y) != edge.Y(x, y);
        }
        char what[180];
        std::snprintf(what, sizeof(what), "detail 1: steepest step %d -> %d; flat area with faint noise: %d of %u pixels changed",
                      steepest(edge), steepest(out), changedFlat, 180 * height);
        Check(steepest(out) > steepest(edge) && changedFlat < static_cast<int>(180 * height / 100), what);
    }
    {
        // A soft 30 px bar (120 on 90), about the size clarity works at: it stands out more; far areas stay.
        Frame bar(width, height, 90);
        for (uint32_t y = 0; y < height; ++y) {
            for (uint32_t x = 300; x < 340; ++x) {
                const double t = std::clamp(std::min(x - 300.0, 339.0 - x) / 5.0, 0.0, 1.0);
                bar.Y(x, y) = static_cast<uint8_t>(std::lround(90 + 30 * t));
            }
        }
        HitCamProcessing s = Neutral();
        s.clarity = 1;
        Frame out = run(s, bar);
        const int before = bar.Y(320, row) - bar.Y(280, row), after = out.Y(320, row) - out.Y(280, row);
        const bool farKept = std::abs(out.Y(20, row) - 90) <= 1 && std::abs(out.Y(620, row) - 90) <= 1;
        char what[180];
        std::snprintf(what, sizeof(what), "clarity 1: bar against its surroundings %d -> %d, far areas %d / %d (90)", before, after,
                      out.Y(20, row), out.Y(620, row));
        Check(after > before + 4 && farKept, what);

        // A hard, high-contrast edge (dark head against a bright wall): no halo on either side.
        Frame edge(width, height, 50);
        for (uint32_t y = 0; y < height; ++y) {
            for (uint32_t x = 320; x < width; ++x) edge.Y(x, y) = 200;
        }
        Frame edged = run(s, edge);
        int halo = 0;
        for (uint32_t x = 290; x < 350; ++x) {
            if (x < 318 || x > 322) halo = std::max(halo, std::abs(edged.Y(x, row) - edge.Y(x, row)));
        }
        std::snprintf(what, sizeof(what), "clarity 1 next to a hard 50/200 edge: largest change %d of 150 (halo limit 4, invisible)", halo);
        Check(halo <= 4, what);
    }
    {
        // Three colour patches: muted blue-ish, strongly saturated, skin (U below, V above neutral).
        Frame patches(width, height);
        auto fill = [&](uint32_t x0, uint32_t x1, int u, int v) {
            for (uint32_t y = 0; y < height / 2; ++y) {
                for (uint32_t x = x0; x < x1; ++x) {
                    patches.UV(x, y, 0) = static_cast<uint8_t>(u);
                    patches.UV(x, y, 1) = static_cast<uint8_t>(v);
                }
            }
        };
        fill(0, 100, 128 + 12, 128 - 6);    // muted
        fill(100, 200, 128 + 70, 128 - 30); // saturated
        fill(200, 320, 128 - 12, 128 + 12); // skin
        HitCamProcessing s = Neutral();
        s.vibrance = 1;
        Frame out = run(s, patches);
        auto gain = [&](uint32_t x) {
            const double before = std::hypot(patches.UV(x, 10, 0) - 128.0, patches.UV(x, 10, 1) - 128.0);
            return std::hypot(out.UV(x, 10, 0) - 128.0, out.UV(x, 10, 1) - 128.0) / before;
        };
        const bool lumaKept = std::equal(out.data.begin(), out.data.begin() + width * height, patches.data.begin());
        char what[180];
        std::snprintf(what, sizeof(what), "vibrance 1: chroma gain muted %.2f, saturated %.2f, skin %.2f; luma untouched", gain(50),
                      gain(150), gain(260));
        Check(gain(50) > 1.3 && gain(150) < gain(50) && gain(260) < gain(50) && gain(260) > 1.0 && lumaKept, what);
    }
    {
        // Neutral chroma (U=V=128) has no hue: atan2(0, 0) must not turn it into NaN (which the clamp makes green).
        auto neutral = [&](float saturation) {
            HitCamProcessing s = Neutral();
            s.saturation = saturation;
            s.vibrance = 1;
            Frame out = run(s, Frame(width, height, 100, 128));
            int worst = 0;
            for (uint32_t i = 0; i < width * height / 2; ++i) worst = (std::max)(worst, std::abs(out.Chroma()[i] - 128));
            return worst;
        };
        const int gray = neutral(0), grayscale = neutral(-1);
        char what[160];
        std::snprintf(what, sizeof(what), "vibrance 1 on neutral chroma: largest shift %d (gray frame), %d (saturation -1)", gray,
                      grayscale);
        Check(gray == 0 && grayscale == 0, what);
    }
}

// Average of HitCam_BridgeProcessingStats' GPU time over frames 11..70 of a 1080p sequence with everything on.
double MeasureGpu(bool* ok) {
    Bridge bridge;
    HitCamProcessing s = Neutral();
    s.temporalStrength = 70;
    s.brightness = 0.1f;
    s.contrast = 0.2f;
    s.saturation = 0.2f;
    s.sharpness = 0.5f;
    s.shadows = 0.3f;
    s.highlights = -0.3f;
    s.detail = 0.6f;
    s.clarity = 0.5f;
    s.vibrance = 0.5f;
    bridge.Set(s);
    const Frame source = Picture(1920, 1080);
    double total = 0;
    int counted = 0;
    *ok = true;
    for (int i = 0; i < 70; ++i) {
        Frame frame = source;
        bridge.Process(frame);
        const double ms = bridge.GpuMs();
        *ok = *ok && ms >= 0;
        if (i >= 10) {
            total += ms;
            ++counted;
        }
    }
    return total / counted;
}

void CheckSpeed() {
    bool ok = false;
    const double ms = MeasureGpu(&ok);
    char what[128];
    std::snprintf(what, sizeof(what), "1080p, all effects on: %.2f ms per frame (upload, GPU, readback; limit 8 ms)", ms);
    Check(ok && ms < 8, what);

    // The software fallback (no usable GPU): works, just slower.
    SetEnvironmentVariableW(L"HITCAM_GPU", L"warp");
    const double warp = MeasureGpu(&ok);
    SetEnvironmentVariableW(L"HITCAM_GPU", nullptr);
    std::snprintf(what, sizeof(what), "WARP (software) fallback, 1080p all effects: %.1f ms per frame", warp);
    Check(ok, what);
}

// Feeds H.264 of a textured scene through a preview-only bridge until `done` (checked after each frame) or timeout.
template <class Done>
bool Decode(void* bridge, TestEncoder& encoder, int noise, Done done, int timeoutMs) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    int64_t time = 0;
    while (GetTickCount64() < deadline) {
        std::vector<std::vector<uint8_t>> units;
        if (FAILED(encoder.Encode(0, units, noise))) return false;
        for (const auto& unit : units) HitCam_BridgeDecode(bridge, unit.data(), static_cast<uint32_t>(unit.size()), time += 333'333);
        if (done()) return true;
        Sleep(10);
    }
    return false;
}

// Decoded H.264 through the whole path (decoder -> processing -> preview): saturation -1 gives a gray preview.
void CheckDecodedPath() {
    TestEncoder encoder;
    encoder.UseScene();
    void* bridge = nullptr;
    if (FAILED(encoder.Initialize(1280, 720)) || FAILED(HitCam_BridgeCreate(&bridge))) {
        Check(false, "decoder bridge for the decoded path");
        return;
    }
    HitCam_BridgePreviewOnly(bridge);
    HitCamProcessing s = Neutral();
    s.saturation = -1;
    s.temporalStrength = 50;
    HitCam_BridgeSetProcessing(bridge, &s);
    std::vector<uint8_t> pixels;
    int maxSpread = 999;
    double gpu = -1;
    const bool decoded = Decode(bridge, encoder, 4, [&] {
        uint32_t width = 0, height = 0;
        uint64_t frame = 0;
        HitCam_BridgePreviewInfo(bridge, &width, &height, &frame);
        if (frame < 5) return false;
        pixels.resize(static_cast<size_t>(width) * height * 4);
        if (!HitCam_BridgeCopyPreview(bridge, pixels.data(), width * 4, width, height)) return false;
        maxSpread = 0;
        for (size_t i = 0; i < pixels.size(); i += 4) {
            maxSpread = std::max({maxSpread, std::abs(pixels[i] - pixels[i + 1]), std::abs(pixels[i + 2] - pixels[i + 1])});
        }
        double artifact = 0;
        int error = 0;
        HitCam_BridgeProcessingStats(bridge, &gpu, &artifact, &error);
        return true;
    }, 20'000);
    char what[128];
    std::snprintf(what, sizeof(what), "decoded H.264, saturation -1: preview gray (largest B/R-G difference %d), GPU %.2f ms", maxSpread, gpu);
    Check(decoded && maxSpread <= 2 && gpu >= 0, what);
    HitCam_BridgeDestroy(bridge);
}

void CheckArtifactReduction() {
    if (!HitCam_ArtifactReductionAvailable()) {
        std::printf("SKIP: NVIDIA Video Effects runtime or artifact reduction models not installed\n");
        return;
    }
    struct Stats {
        double gpu = -1, artifact = -1;
        int error = 0;
    };
    auto stats = [](void* bridge) {
        Stats s;
        HitCam_BridgeProcessingStats(bridge, &s.gpu, &s.artifact, &s.error);
        return s;
    };

    // Each mode on its own bridge: after a switch the previous model keeps running until the new one is loaded.
    for (const int mode : {1, 2}) {
        TestEncoder encoder;
        encoder.UseScene();
        void* bridge = nullptr;
        // Low bitrate: visible compression artifacts, as with a weak Wi-Fi.
        if (FAILED(encoder.Initialize(1920, 1080, 1'500'000)) || FAILED(HitCam_BridgeCreate(&bridge))) {
            Check(false, "encoder and bridge for artifact reduction");
            return;
        }
        HitCam_BridgePreviewOnly(bridge);
        HitCamProcessing s = Neutral();
        s.artifactReduction = mode;
        HitCam_BridgeSetProcessing(bridge, &s);
        const ULONGLONG start = GetTickCount64();
        // The model loads in the background (TensorRT); frames pass through meanwhile.
        Decode(bridge, encoder, 3, [&] { const Stats st = stats(bridge); return st.artifact >= 0 || st.error != 0; }, 90'000);
        const ULONGLONG loaded = GetTickCount64() - start;
        double total = 0;
        int frames = 0;
        Stats last;
        Decode(bridge, encoder, 3, [&] {
            last = stats(bridge);
            if (last.artifact >= 0) {
                total += last.artifact;
                ++frames;
            }
            return frames >= 30 || last.error != 0;
        }, 20'000);
        char what[160];
        std::snprintf(what, sizeof(what), "artifact reduction %s on 1920x1080 H.264: loaded in %llu ms, %.1f ms per frame, NvCV status %d",
                      mode == 1 ? "gentle" : "strong", loaded, frames ? total / frames : -1.0, last.error);
        Check(frames >= 30 && last.error == 0, what);

        if (mode == 1) {
            // Artifact reduction and the Direct3D stage together (the first frame also creates the device).
            s.temporalStrength = 60;
            s.sharpness = 0.3f;
            s.saturation = 0.1f;
            HitCam_BridgeSetProcessing(bridge, &s);
            double gpu = 0, artifact = 0;
            frames = 0;
            Decode(bridge, encoder, 3, [&] {
                last = stats(bridge);
                if (last.artifact >= 0 && last.gpu >= 0 && ++frames > 5) {
                    gpu += last.gpu;
                    artifact += last.artifact;
                }
                return frames >= 25;
            }, 20'000);
            std::snprintf(what, sizeof(what), "artifact reduction + temporal + sharpness + saturation: AR %.1f ms, D3D11 %.1f ms per frame",
                          artifact / 20, gpu / 20);
            Check(frames >= 25, what);
        }
        // Off: the model is released with the next frame.
        const HitCamProcessing neutral = Neutral();
        HitCam_BridgeSetProcessing(bridge, &neutral);
        Stats off;
        Decode(bridge, encoder, 3, [&] { off = stats(bridge); return true; }, 5'000);
        Check(off.artifact < 0 && off.gpu < 0 && off.error == 0, "everything off: no stage runs");
        HitCam_BridgeDestroy(bridge);
    }

    {
        // Portrait (phone upright): taller than the model takes. Reported, not crashing; frames still pass.
        TestEncoder encoder;
        encoder.UseScene();
        void* bridge = nullptr;
        if (FAILED(encoder.Initialize(1080, 1920, 4'000'000)) || FAILED(HitCam_BridgeCreate(&bridge))) {
            Check(false, "portrait encoder and bridge");
            return;
        }
        HitCam_BridgePreviewOnly(bridge);
        HitCamProcessing s = Neutral();
        s.artifactReduction = 2;
        HitCam_BridgeSetProcessing(bridge, &s);
        Stats st;
        const bool settled = Decode(bridge, encoder, 0, [&] { st = stats(bridge); return st.error != 0 || st.artifact >= 0; }, 90'000);
        uint32_t width = 0, height = 0;
        uint64_t frame = 0;
        HitCam_BridgePreviewInfo(bridge, &width, &height, &frame);
        char what[160];
        std::snprintf(what, sizeof(what), "portrait 1080x1920: NvCV status %d, AR %.1f ms, preview %ux%u frame %llu",
                      st.error, st.artifact, width, height, static_cast<unsigned long long>(frame));
        // Either the model takes it (a newer SDK) or it reports why not; the picture keeps coming either way.
        Check(settled && frame > 0 && (st.error != 0 || st.artifact >= 0), what);
        std::printf("      portrait %s\n", st.error != 0 ? "reported as an error (frames pass through)" : "processed");

        // Releasing the bridge while a model loads never waits for the load.
        HitCamProcessing strong = Neutral();
        strong.artifactReduction = 1;
        void* other = nullptr;
        HitCam_BridgeCreate(&other);
        HitCam_BridgePreviewOnly(other);
        HitCam_BridgeSetProcessing(other, &strong);
        TestEncoder landscape;
        landscape.UseScene();
        landscape.Initialize(1280, 720);
        Decode(other, landscape, 0, [&] { uint32_t w, h; uint64_t f = 0; HitCam_BridgePreviewInfo(other, &w, &h, &f); return f > 0; }, 10'000);
        const ULONGLONG before = GetTickCount64();
        HitCam_BridgeDestroy(other);
        const ULONGLONG took = GetTickCount64() - before;
        std::snprintf(what, sizeof(what), "bridge released during a model load in %llu ms (no waiting)", took);
        Check(took < 300, what);
        HitCam_BridgeDestroy(bridge);
    }
}

int Run() {
    CheckNeutral();
    CheckTemporal();
    CheckNoGhosting();
    CheckAdjustments();
    CheckEnhancement();
    CheckSpeed();
    CheckDecodedPath();
    CheckArtifactReduction();
    std::printf(g_passed ? "ALL PASSED\n" : "SOME CHECKS FAILED\n");
    return g_passed ? 0 : 1;
}

}  // namespace process_check
