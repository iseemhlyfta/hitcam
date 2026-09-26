#include "FaceEffect.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace hitcam {
namespace {

struct Yuv {
    uint8_t y, u, v;
};

// BT.709, video range, like the rest of the pipeline.
Yuv ToYuv(uint32_t rgb) {
    const float r = static_cast<float>((rgb >> 16) & 0xFF) / 255.0f;
    const float g = static_cast<float>((rgb >> 8) & 0xFF) / 255.0f;
    const float b = static_cast<float>(rgb & 0xFF) / 255.0f;
    const float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
    auto byte = [](float v) { return static_cast<uint8_t>(std::clamp(static_cast<int>(std::lround(v)), 16, 240)); };
    return {static_cast<uint8_t>(std::clamp(static_cast<int>(std::lround(16 + 219 * y)), 16, 235)), byte(128 + 224 * (b - y) / 1.8556f),
            byte(128 + 224 * (r - y) / 1.5748f)};
}

bool Finite(float v) { return std::isfinite(v) && std::abs(v) < 1e4f; }

// The region in pixels, clipped to the frame, edges on even pixels (whole chroma samples).
struct Area {
    int x0, y0, x1, y1;
    bool Empty() const { return x1 <= x0 || y1 <= y0; }
};

Area ToArea(const HitCamFaceRegion& r, int w, int h) {
    auto edge = [](float v, int size, bool up) {
        const float px = std::clamp(v, 0.0f, 1.0f) * static_cast<float>(size);
        const int p = up ? static_cast<int>(std::ceil(px)) : static_cast<int>(std::floor(px));
        return std::clamp(up ? (p + 1) & ~1 : p & ~1, 0, size);
    };
    return {edge(std::min(r.left, r.right), w, false), edge(std::min(r.top, r.bottom), h, false), edge(std::max(r.left, r.right), w, true),
            edge(std::max(r.top, r.bottom), h, true)};
}

// Mosaic of one plane: every cell × cell block (clipped to the area) becomes its average. `step` is 1 for luma,
// 2 for interleaved chroma (then called once per channel with the channel's offset).
void MosaicPlane(uint8_t* plane, uint32_t pitch, int x0, int y0, int x1, int y1, int cell, int step) {
    for (int by = y0; by < y1; by += cell) {
        const int ey = std::min(by + cell, y1);
        for (int bx = x0; bx < x1; bx += cell) {
            const int ex = std::min(bx + cell, x1);
            uint32_t sum = 0, count = 0;
            for (int y = by; y < ey; ++y) {
                const uint8_t* row = plane + static_cast<size_t>(y) * pitch;
                for (int x = bx; x < ex; ++x) {
                    sum += row[static_cast<size_t>(x) * step];
                    ++count;
                }
            }
            const uint8_t average = static_cast<uint8_t>((sum + count / 2) / count);
            for (int y = by; y < ey; ++y) {
                uint8_t* row = plane + static_cast<size_t>(y) * pitch;
                for (int x = bx; x < ex; ++x) row[static_cast<size_t>(x) * step] = average;
            }
        }
    }
}

// One box-blur pass along a line of `n` samples `step` apart, edges repeated; running sum, O(n).
void BoxLine(uint8_t* line, int n, size_t step, int radius, std::vector<uint8_t>& scratch) {
    scratch.resize(static_cast<size_t>(n));
    for (int i = 0; i < n; ++i) scratch[static_cast<size_t>(i)] = line[static_cast<size_t>(i) * step];
    const int window = 2 * radius + 1;
    auto at = [&](int i) { return static_cast<int>(scratch[static_cast<size_t>(std::clamp(i, 0, n - 1))]); };
    int sum = 0;
    for (int i = -radius; i <= radius; ++i) sum += at(i);
    for (int i = 0; i < n; ++i) {
        line[static_cast<size_t>(i) * step] = static_cast<uint8_t>((sum + window / 2) / window);
        sum += at(i + radius + 1) - at(i - radius);
    }
}

// One vertical box pass over the area, row by row (running column sums): walking down columns misses the cache.
void BoxColumns(uint8_t* plane, uint32_t pitch, int x0, int y0, int x1, int y1, int radius, int step, std::vector<uint8_t>& scratch,
                std::vector<int>& sums) {
    const int cols = x1 - x0, rows = y1 - y0, window = 2 * radius + 1;
    scratch.resize(static_cast<size_t>(cols) * rows);
    for (int y = 0; y < rows; ++y) {
        const uint8_t* src = plane + static_cast<size_t>(y0 + y) * pitch + static_cast<size_t>(x0) * step;
        uint8_t* dst = scratch.data() + static_cast<size_t>(y) * cols;
        for (int x = 0; x < cols; ++x) dst[x] = src[static_cast<size_t>(x) * step];
    }
    auto row = [&](int i) { return scratch.data() + static_cast<size_t>(std::clamp(i, 0, rows - 1)) * cols; };
    sums.assign(static_cast<size_t>(cols), 0);
    for (int i = -radius; i <= radius; ++i) {
        const uint8_t* r = row(i);
        for (int x = 0; x < cols; ++x) sums[static_cast<size_t>(x)] += r[x];
    }
    for (int y = 0; y < rows; ++y) {
        uint8_t* dst = plane + static_cast<size_t>(y0 + y) * pitch + static_cast<size_t>(x0) * step;
        const uint8_t* in = row(y + radius + 1);
        const uint8_t* out = row(y - radius);
        for (int x = 0; x < cols; ++x) {
            int& s = sums[static_cast<size_t>(x)];
            dst[static_cast<size_t>(x) * step] = static_cast<uint8_t>((s + window / 2) / window);
            s += in[x] - out[x];
        }
    }
}

// Blur of one plane inside the area: two horizontal and two vertical box passes (close to a Gaussian).
void BlurPlane(uint8_t* plane, uint32_t pitch, int x0, int y0, int x1, int y1, int radius, int step, std::vector<uint8_t>& scratch,
               std::vector<int>& sums) {
    if (radius < 1 || x1 <= x0 || y1 <= y0) return;
    for (int pass = 0; pass < 2; ++pass) {
        for (int y = y0; y < y1; ++y) BoxLine(plane + static_cast<size_t>(y) * pitch + static_cast<size_t>(x0) * step, x1 - x0, step, radius, scratch);
        BoxColumns(plane, pitch, x0, y0, x1, y1, radius, step, scratch, sums);
    }
}

void FillArea(uint8_t* luma, uint8_t* chroma, uint32_t pitch, const Area& a, Yuv c) {
    for (int y = a.y0; y < a.y1; ++y) std::memset(luma + static_cast<size_t>(y) * pitch + a.x0, c.y, static_cast<size_t>(a.x1 - a.x0));
    for (int cy = a.y0 / 2; cy < a.y1 / 2; ++cy) {
        uint8_t* row = chroma + static_cast<size_t>(cy) * pitch;
        for (int cx = a.x0 / 2; cx < a.x1 / 2; ++cx) {
            row[cx * 2] = c.u;
            row[cx * 2 + 1] = c.v;
        }
    }
}

}  // namespace

int FaceEffect::MosaicCell(int side, float strength) {
    const float fraction = 1.0f / 24 + (1.0f / 6 - 1.0f / 24) * std::clamp(strength, 0.0f, 1.0f);
    return std::max(2, static_cast<int>(std::lround(side * fraction)) & ~1);
}

int FaceEffect::BlurRadius(int side, float strength) {
    const float fraction = 1.0f / 40 + (1.0f / 8 - 1.0f / 40) * std::clamp(strength, 0.0f, 1.0f);
    return std::max(1, static_cast<int>(std::lround(side * fraction)));
}

void FaceEffect::Set(const HitCamFaceRegion* regions, int32_t count) {
    std::shared_ptr<const std::vector<HitCamFaceRegion>> result;
    if (regions && count > 0) {
        auto copy = std::make_shared<std::vector<HitCamFaceRegion>>();
        for (int32_t i = 0; i < std::min(count, kMaxRegions); ++i) {
            const HitCamFaceRegion& r = regions[i];
            if (Finite(r.left) && Finite(r.top) && Finite(r.right) && Finite(r.bottom) && Finite(r.strength) && r.effect >= Mosaic &&
                r.effect <= None)
                copy->push_back(r);
        }
        if (!copy->empty()) result = std::move(copy);
    }
    std::lock_guard guard(lock_);
    regions_ = std::move(result);
    setMs_ = GetTickCount64();
}

std::shared_ptr<const std::vector<HitCamFaceRegion>> FaceEffect::Current() {
    std::lock_guard guard(lock_);
    if (regions_ && GetTickCount64() - setMs_ > kStaleMs) regions_.reset();
    return regions_;
}

void FaceEffect::Draw(const std::vector<HitCamFaceRegion>& regions, uint8_t* luma, uint8_t* chroma, uint32_t pitch, uint32_t width,
                      uint32_t height) {
    const int w = static_cast<int>(width & ~1u), h = static_cast<int>(height & ~1u);
    if (w < 2 || h < 2) return;
    std::vector<uint8_t> scratch;
    std::vector<int> sums;
    for (const auto& r : regions) {
        const Area a = ToArea(r, w, h);
        if (a.Empty()) continue;
        const int side = std::max(a.x1 - a.x0, a.y1 - a.y0);
        switch (r.effect) {
            case Mosaic: {
                const int cell = MosaicCell(side, r.strength);
                MosaicPlane(luma, pitch, a.x0, a.y0, a.x1, a.y1, cell, 1);
                MosaicPlane(chroma, pitch, a.x0 / 2, a.y0 / 2, a.x1 / 2, a.y1 / 2, cell / 2, 2);
                MosaicPlane(chroma + 1, pitch, a.x0 / 2, a.y0 / 2, a.x1 / 2, a.y1 / 2, cell / 2, 2);
                break;
            }
            case Blur: {
                const int radius = BlurRadius(side, r.strength);
                BlurPlane(luma, pitch, a.x0, a.y0, a.x1, a.y1, radius, 1, scratch, sums);
                BlurPlane(chroma, pitch, a.x0 / 2, a.y0 / 2, a.x1 / 2, a.y1 / 2, std::max(1, radius / 2), 2, scratch, sums);
                BlurPlane(chroma + 1, pitch, a.x0 / 2, a.y0 / 2, a.x1 / 2, a.y1 / 2, std::max(1, radius / 2), 2, scratch, sums);
                break;
            }
            case Fill:
                FillArea(luma, chroma, pitch, a, ToYuv(r.rgb));
                break;
            default:
                break;
        }
        if (r.frame) {
            const int t = std::max(2, h / 360) & ~1;
            const Yuv c = ToYuv(r.frameRgb);
            FillArea(luma, chroma, pitch, {a.x0, a.y0, a.x1, std::min(a.y1, a.y0 + t)}, c);
            FillArea(luma, chroma, pitch, {a.x0, std::max(a.y0, a.y1 - t), a.x1, a.y1}, c);
            FillArea(luma, chroma, pitch, {a.x0, a.y0, std::min(a.x1, a.x0 + t), a.y1}, c);
            FillArea(luma, chroma, pitch, {std::max(a.x0, a.x1 - t), a.y0, a.x1, a.y1}, c);
        }
    }
}

}  // namespace hitcam
