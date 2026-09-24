#include "Overlay.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace hitcam {
namespace {

// Labels larger than this (in either direction) are ignored: no real label is, and it bounds the copy.
constexpr int32_t kMaxLabelWidth = 4096;
constexpr int32_t kMaxLabelHeight = 1024;

struct Yuv {
    uint8_t y, u, v;
};

// BT.709, video range (as the rest of the pipeline).
Yuv ToYuv(uint32_t rgb) {
    const int r = static_cast<int>((rgb >> 16) & 0xFF);
    const int g = static_cast<int>((rgb >> 8) & 0xFF);
    const int b = static_cast<int>(rgb & 0xFF);
    // 16-bit fixed point, rounded; chroma's + (128 << 16) is the offset and keeps the shifted value positive.
    const int y = ((11966 * r + 40254 * g + 4064 * b + 32768) >> 16) + 16;
    const int u = (-6596 * r - 22188 * g + 28784 * b + (128 << 16) + 32768) >> 16;
    const int v = (28784 * r - 26145 * g - 2639 * b + (128 << 16) + 32768) >> 16;
    auto clamp = [](int value) { return static_cast<uint8_t>(std::clamp(value, 16, 240)); };
    return {static_cast<uint8_t>(std::clamp(y, 16, 235)), clamp(u), clamp(v)};
}

uint8_t Blend(int from, int to, int alpha) {
    const int delta = (to - from) * alpha;
    return static_cast<uint8_t>(from + (delta >= 0 ? (delta + 127) / 255 : (delta - 127) / 255));
}

struct Target {
    uint8_t* luma;
    uint8_t* chroma;
    uint32_t pitch;
    int width, height;

    // Fills [x0, x1) x [y0, y1), clipped to the frame; colours every chroma sample that covers a filled pixel.
    void Fill(int x0, int y0, int x1, int y1, Yuv c) const {
        x0 = std::max(x0, 0);
        y0 = std::max(y0, 0);
        x1 = std::min(x1, width);
        y1 = std::min(y1, height);
        if (x0 >= x1 || y0 >= y1) return;
        for (int y = y0; y < y1; ++y) std::memset(luma + static_cast<size_t>(y) * pitch + x0, c.y, static_cast<size_t>(x1 - x0));
        for (int cy = y0 / 2; cy < (y1 + 1) / 2; ++cy) {
            uint8_t* row = chroma + static_cast<size_t>(cy) * pitch;
            for (int cx = x0 / 2; cx < (x1 + 1) / 2; ++cx) {
                row[cx * 2] = c.u;
                row[cx * 2 + 1] = c.v;
            }
        }
    }

    // Blends white text (alpha, `w` x `h`) at (x, y), clipped to the frame. Chroma goes towards gray by the
    // average alpha of its 2x2 pixels, so the text is white rather than tinted.
    void Text(int x, int y, const uint8_t* alpha, int w, int h) const {
        const int x0 = std::max(x, 0), y0 = std::max(y, 0);
        const int x1 = std::min(x + w, width), y1 = std::min(y + h, height);
        if (x0 >= x1 || y0 >= y1) return;
        for (int py = y0; py < y1; ++py) {
            const uint8_t* a = alpha + static_cast<size_t>(py - y) * w;
            uint8_t* row = luma + static_cast<size_t>(py) * pitch;
            for (int px = x0; px < x1; ++px) {
                if (a[px - x]) row[px] = Blend(row[px], 235, a[px - x]);
            }
        }
        for (int cy = y0 / 2; cy < (y1 + 1) / 2; ++cy) {
            uint8_t* row = chroma + static_cast<size_t>(cy) * pitch;
            for (int cx = x0 / 2; cx < (x1 + 1) / 2; ++cx) {
                int sum = 0;
                for (int py = std::max(cy * 2, y0); py < std::min(cy * 2 + 2, y1); ++py) {
                    for (int px = std::max(cx * 2, x0); px < std::min(cx * 2 + 2, x1); ++px) {
                        sum += alpha[static_cast<size_t>(py - y) * w + (px - x)];
                    }
                }
                if (sum == 0) continue;
                const int average = (sum + 2) / 4;
                row[cx * 2] = Blend(row[cx * 2], 128, average);
                row[cx * 2 + 1] = Blend(row[cx * 2 + 1], 128, average);
            }
        }
    }
};

// Normalized coordinate -> pixel edge, rounded to an even pixel so edges fall on whole chroma samples.
// `up` rounds odd pixels up (right / bottom edges), else down.
int Edge(float value, int size, bool up) {
    const float clamped = std::clamp(value, 0.0f, 1.0f);
    int pixel = static_cast<int>(std::lround(clamped * static_cast<float>(size)));
    pixel = up ? (pixel + 1) & ~1 : pixel & ~1;
    return std::clamp(pixel, 0, size);
}

}  // namespace

void Overlay::Set(const HitCamOverlayBox* boxes, int32_t count) {
    std::shared_ptr<Boxes> copy;
    if (boxes && count > 0) {
        copy = std::make_shared<Boxes>();
        count = std::min(count, kMaxBoxes);
        copy->reserve(static_cast<size_t>(count));
        for (int32_t i = 0; i < count; ++i) {
            const HitCamOverlayBox& in = boxes[i];
            if (!std::isfinite(in.left) || !std::isfinite(in.top) || !std::isfinite(in.right) || !std::isfinite(in.bottom)) continue;
            Box box{in.left, in.top, in.right, in.bottom, in.rgb & 0xFFFFFF, 0, 0, {}};
            if (in.label && in.labelWidth > 0 && in.labelHeight > 0 && in.labelWidth <= kMaxLabelWidth && in.labelHeight <= kMaxLabelHeight) {
                box.labelWidth = in.labelWidth;
                box.labelHeight = in.labelHeight;
                box.label.assign(in.label, in.label + static_cast<size_t>(in.labelWidth) * in.labelHeight);
            }
            copy->push_back(std::move(box));
        }
        if (copy->empty()) copy.reset();
    }
    std::lock_guard guard(lock_);
    boxes_ = std::move(copy);
    setMs_ = GetTickCount64();
}

std::shared_ptr<const Overlay::Boxes> Overlay::Current() {
    std::lock_guard guard(lock_);
    if (boxes_ && GetTickCount64() - setMs_ > kStaleMs) boxes_.reset();
    return boxes_;
}

void Overlay::Draw(const Boxes& boxes, uint8_t* luma, uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height) {
    const Target target{luma, chroma, pitch, static_cast<int>(width & ~1u), static_cast<int>(height & ~1u)};
    const int w = target.width, h = target.height;
    if (w < 2 || h < 2) return;
    const int thickness = std::max(2, h / 360);
    for (const Box& box : boxes) {
        const int x0 = Edge(std::min(box.left, box.right), w, false), x1 = Edge(std::max(box.left, box.right), w, true);
        const int y0 = Edge(std::min(box.top, box.bottom), h, false), y1 = Edge(std::max(box.top, box.bottom), h, true);
        if (x1 <= x0 || y1 <= y0) continue;
        const Yuv colour = ToYuv(box.rgb);
        target.Fill(x0, y0, x1, y0 + thickness, colour);
        target.Fill(x0, y1 - thickness, x1, y1, colour);
        target.Fill(x0, y0, x0 + thickness, y1, colour);
        target.Fill(x1 - thickness, y0, x1, y1, colour);

        if (box.label.empty()) continue;
        const int pad = thickness;
        const int tabWidth = box.labelWidth + 2 * pad, tabHeight = box.labelHeight + 2 * pad;
        // Above the box's top-left corner, or just inside its top when there is no room above.
        int tabX = std::max(0, std::min(x0, w - tabWidth));
        int tabY = y0 - tabHeight >= 0 ? y0 - tabHeight : y0;
        tabY = std::max(0, std::min(tabY, h - tabHeight));
        target.Fill(tabX, tabY, tabX + tabWidth, tabY + tabHeight, colour);
        target.Text(tabX + pad, tabY + pad, box.label.data(), box.labelWidth, box.labelHeight);
    }
}

}  // namespace hitcam
