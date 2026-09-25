#include "HandScene.h"

#include <algorithm>
#include <cmath>

namespace hitcam {
namespace {

struct Yuv {
    float y, u, v;
};

// BT.709, video range, like the rest of the pipeline.
Yuv ToYuv(uint32_t rgb) {
    const float r = static_cast<float>((rgb >> 16) & 0xFF) / 255.0f;
    const float g = static_cast<float>((rgb >> 8) & 0xFF) / 255.0f;
    const float b = static_cast<float>(rgb & 0xFF) / 255.0f;
    const float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
    return {16 + 219 * y, 128 + 224 * (b - y) / 1.8556f, 128 + 224 * (r - y) / 1.5748f};
}

uint8_t Mix(uint8_t from, float to, float alpha) {
    const float value = from + (to - from) * alpha;
    return static_cast<uint8_t>(std::clamp(static_cast<int>(value + 0.5f), 0, 255));
}

// Normalized coordinates far outside the frame are refused: scaled to pixels they must still fit an int.
bool Finite(float value) { return std::isfinite(value) && std::abs(value) < 1e4f; }

struct Frame {
    uint8_t* luma;
    uint8_t* chroma;
    uint32_t pitch;
    int width, height;

    uint8_t& Y(int x, int y) const { return luma[static_cast<size_t>(y) * pitch + x]; }
    uint8_t* UV(int cx, int cy) const { return chroma + static_cast<size_t>(cy) * pitch + static_cast<size_t>(cx) * 2; }
};

// Pixels whose centre is inside the polygon (convex, or crossed: then its convex span per row), row by row.
template <class Visit>
void Spans(const float* xs, const float* ys, int corners, int width, int height, Visit&& visit) {
    float top = ys[0], bottom = ys[0];
    for (int i = 1; i < corners; ++i) {
        top = std::min(top, ys[i]);
        bottom = std::max(bottom, ys[i]);
    }
    const int y0 = std::max(0, static_cast<int>(std::ceil(top - 0.5f)));
    const int y1 = std::min(height - 1, static_cast<int>(std::floor(bottom - 0.5f)));
    for (int y = y0; y <= y1; ++y) {
        const float cy = y + 0.5f;
        float left = 1e9f, right = -1e9f;
        for (int i = 0; i < corners; ++i) {
            const float ax = xs[i], ay = ys[i], bx = xs[(i + 1) % corners], by = ys[(i + 1) % corners];
            if ((ay <= cy && by > cy) || (by <= cy && ay > cy)) {
                const float x = ax + (cy - ay) * (bx - ax) / (by - ay);
                left = std::min(left, x);
                right = std::max(right, x);
            }
        }
        const int x0 = std::max(0, static_cast<int>(std::ceil(left - 0.5f)));
        const int x1 = std::min(width - 1, static_cast<int>(std::floor(right - 0.5f)));
        if (x0 <= x1) visit(y, x0, x1);
    }
}

void DrawQuad(const Frame& f, const HitCamSceneQuad& q) {
    float xs[4], ys[4];
    for (int i = 0; i < 4; ++i) {
        xs[i] = q.x[i] * f.width;
        ys[i] = q.y[i] * f.height;
    }
    const Yuv from = ToYuv(q.rgbFrom), to = ToYuv(q.rgbTo);
    const float fx = q.fromX * f.width, fy = q.fromY * f.height;
    const float dx = q.toX * f.width - fx, dy = q.toY * f.height - fy;
    const float length2 = dx * dx + dy * dy;
    const float alpha = std::clamp(q.alpha, 0.0f, 1.0f);
    if (alpha <= 0) return;
    // The gradient position t = ((p - from) . d) / |d|^2 is linear along a row: start value and step per pixel.
    const float sx = length2 > 1e-6f ? dx / length2 : 0.0f, sy = length2 > 1e-6f ? dy / length2 : 0.0f;
    // Fixed point: t in 1/65536, colours and alpha in 1/256.
    const int a = static_cast<int>(alpha * 256 + 0.5f);
    auto row = [&](uint8_t* out, int stride, int x0, int x1, float py, float scale, float first, float last) {
        const float t0 = (x0 * scale + scale / 2 - fx) * sx + (py - fy) * sy;
        int64_t t = static_cast<int64_t>(std::clamp(t0, -1e4f, 1e4f) * 65536);
        const int64_t step = static_cast<int64_t>(std::clamp(scale * sx, -1e4f, 1e4f) * 65536);
        const int base = static_cast<int>(first * 256), range = static_cast<int>((last - first) * 256);
        for (int x = x0; x <= x1; ++x, t += step) {
            const int64_t clamped = t < 0 ? 0 : t > 65536 ? 65536 : t;
            const int colour = base + static_cast<int>((range * clamped) >> 16);   // x256
            uint8_t& v = out[static_cast<size_t>(x) * stride];
            v = static_cast<uint8_t>(v + (((colour - (v << 8)) * a + (1 << 15)) >> 16));
        }
    };
    Spans(xs, ys, 4, f.width, f.height, [&](int y, int x0, int x1) {
        row(f.luma + static_cast<size_t>(y) * f.pitch, 1, x0, x1, y + 0.5f, 1, from.y, to.y);
    });
    // Chroma: the same polygon on the half-size grid.
    for (int i = 0; i < 4; ++i) {
        xs[i] /= 2;
        ys[i] /= 2;
    }
    Spans(xs, ys, 4, f.width / 2, f.height / 2, [&](int cy, int x0, int x1) {
        uint8_t* uv = f.chroma + static_cast<size_t>(cy) * f.pitch;
        row(uv, 2, x0, x1, cy * 2 + 1.0f, 2, from.u, to.u);
        row(uv + 1, 2, x0, x1, cy * 2 + 1.0f, 2, from.v, to.v);
    });
}

// Coverage of a pixel centre by a thick segment, with a one-pixel soft edge.
void DrawLine(const Frame& f, const HitCamSceneLine& l) {
    const float ax = l.x0 * f.width, ay = l.y0 * f.height, bx = l.x1 * f.width, by = l.y1 * f.height;
    const float half = std::max(0.5f, l.width * f.height / 2);
    const float alpha = std::clamp(l.alpha, 0.0f, 1.0f);
    if (alpha <= 0) return;
    const Yuv c = ToYuv(l.rgb);
    const float dx = bx - ax, dy = by - ay, length2 = dx * dx + dy * dy;
    auto coverage = [&](float px, float py) {
        float t = length2 > 1e-6f ? ((px - ax) * dx + (py - ay) * dy) / length2 : 0.0f;
        t = std::clamp(t, 0.0f, 1.0f);
        const float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
        return std::clamp(half + 0.5f - std::sqrt(ex * ex + ey * ey), 0.0f, 1.0f);
    };
    const int x0 = std::max(0, static_cast<int>(std::floor(std::min(ax, bx) - half - 1)));
    const int x1 = std::min(f.width - 1, static_cast<int>(std::ceil(std::max(ax, bx) + half + 1)));
    const int y0 = std::max(0, static_cast<int>(std::floor(std::min(ay, by) - half - 1)));
    const int y1 = std::min(f.height - 1, static_cast<int>(std::ceil(std::max(ay, by) + half + 1)));
    for (int y = y0; y <= y1; ++y) {
        for (int x = x0; x <= x1; ++x) {
            const float a = coverage(x + 0.5f, y + 0.5f) * alpha;
            if (a > 0.004f) f.Y(x, y) = Mix(f.Y(x, y), c.y, a);
        }
    }
    for (int cy = std::max(0, y0 / 2); cy <= std::min(f.height / 2 - 1, y1 / 2); ++cy) {
        for (int cx = std::max(0, x0 / 2); cx <= std::min(f.width / 2 - 1, x1 / 2); ++cx) {
            const float a = coverage(cx * 2 + 1.0f, cy * 2 + 1.0f) * alpha;
            if (a <= 0.004f) continue;
            uint8_t* uv = f.UV(cx, cy);
            uv[0] = Mix(uv[0], c.u, a);
            uv[1] = Mix(uv[1], c.v, a);
        }
    }
}

void DrawDot(const Frame& f, const HitCamSceneDot& d) {
    const HitCamSceneLine point{d.x, d.y, d.x, d.y, d.radius * 2, d.rgb, 1.0f};
    DrawLine(f, point);
}

}  // namespace

void HandScene::Set(const HitCamSceneDot* dots, int32_t dotCount, const HitCamSceneLine* lines, int32_t lineCount,
                    const HitCamSceneQuad* quads, int32_t quadCount) {
    auto scene = std::make_shared<Scene>();
    if (dots) {
        for (int32_t i = 0; i < std::min(dotCount, kMaxDots); ++i) {
            const auto& d = dots[i];
            if (Finite(d.x) && Finite(d.y) && Finite(d.radius) && d.radius > 0 && d.radius < 0.5f) scene->dots.push_back(d);
        }
    }
    if (lines) {
        for (int32_t i = 0; i < std::min(lineCount, kMaxLines); ++i) {
            const auto& l = lines[i];
            if (Finite(l.x0) && Finite(l.y0) && Finite(l.x1) && Finite(l.y1) && Finite(l.width) && Finite(l.alpha) && l.width > 0 && l.width < 0.5f)
                scene->lines.push_back(l);
        }
    }
    if (quads) {
        for (int32_t i = 0; i < std::min(quadCount, kMaxQuads); ++i) {
            const auto& q = quads[i];
            bool ok = Finite(q.fromX) && Finite(q.fromY) && Finite(q.toX) && Finite(q.toY) && Finite(q.alpha);
            for (int c = 0; c < 4; ++c) ok = ok && Finite(q.x[c]) && Finite(q.y[c]);
            if (ok) scene->quads.push_back(q);
        }
    }
    std::shared_ptr<const Scene> result;
    if (!scene->dots.empty() || !scene->lines.empty() || !scene->quads.empty()) result = std::move(scene);
    std::lock_guard guard(lock_);
    scene_ = std::move(result);
    setMs_ = GetTickCount64();
}

std::shared_ptr<const HandScene::Scene> HandScene::Current() {
    std::lock_guard guard(lock_);
    if (scene_ && GetTickCount64() - setMs_ > kStaleMs) scene_.reset();
    return scene_;
}

void HandScene::Draw(const Scene& scene, uint8_t* luma, uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height) {
    const Frame f{luma, chroma, pitch, static_cast<int>(width & ~1u), static_cast<int>(height & ~1u)};
    if (f.width < 2 || f.height < 2) return;
    for (const auto& q : scene.quads) DrawQuad(f, q);
    for (const auto& l : scene.lines) DrawLine(f, l);
    for (const auto& d : scene.dots) DrawDot(f, d);
}

}  // namespace hitcam
