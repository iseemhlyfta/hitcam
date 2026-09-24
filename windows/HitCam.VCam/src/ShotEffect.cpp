#include "ShotEffect.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace hitcam {
namespace {

constexpr float kShake = 0.018f;  // peak offset, fraction of the frame height
constexpr float kPi = 3.14159265f;

// Warm muzzle colour 0xFFB347 in BT.709 video range; the core goes white.
constexpr int kFlashU = 73;
constexpr int kFlashV = 166;

float SmoothStep(float x) {
    x = std::clamp(x, 0.0f, 1.0f);
    return x * x * (3 - 2 * x);
}

uint8_t Mix(int from, int to, float alpha) {
    return static_cast<uint8_t>(std::clamp(static_cast<int>(std::lround(from + (to - from) * alpha)), 0, 255));
}

// `from` moved towards `to` by alpha / 256 (alpha 0..256), rounded.
uint8_t MixFixed(int from, int to, int alpha) { return static_cast<uint8_t>(from + (((to - from) * alpha + 128) >> 8)); }

}  // namespace

ShotState ShotEffect::At(double ms, float dirX) {
    ShotState s{0, 0, 0, 0, 1};
    if (ms < 0 || ms >= kDurationMs) return s;
    const float t = static_cast<float>(ms);
    s.flash = t < 30 ? 1.0f : 1.0f - SmoothStep((t - 30) / 80);
    s.lift = t < 150 ? 0.22f * std::exp(-t / 40) : 0.0f;
    const float decay = std::exp(-t / 55);
    const float swing = decay * std::cos(2 * kPi * t / 90);
    // The camera kicks up (the picture jumps down) and back, against the barrel.
    s.shakeY = kShake * swing;
    s.shakeX = -0.5f * kShake * swing * (dirX >= 0 ? 1.0f : -1.0f);
    s.zoom = 1 + 2.2f * kShake * decay;
    return s;
}

double ShotEffect::NowMs() {
    LARGE_INTEGER counter, frequency;
    QueryPerformanceCounter(&counter);
    QueryPerformanceFrequency(&frequency);
    return static_cast<double>(counter.QuadPart) * 1000.0 / static_cast<double>(frequency.QuadPart);
}

void ShotEffect::Fire(const HitCamShot& shot) { FireAt(shot, NowMs()); }

void ShotEffect::FireAt(const HitCamShot& shot, double ms) {
    if (!std::isfinite(shot.x) || !std::isfinite(shot.y) || !std::isfinite(shot.dirX) || !std::isfinite(shot.dirY) || !std::isfinite(shot.size)) return;
    HitCamShot clean = shot;
    const float length = std::sqrt(shot.dirX * shot.dirX + shot.dirY * shot.dirY);
    if (length > 1e-6f) {
        clean.dirX /= length;
        clean.dirY /= length;
    } else {
        clean.dirX = 1;
        clean.dirY = 0;
    }
    clean.size = std::clamp(shot.size, 0.01f, 1.0f);
    std::lock_guard guard(lock_);
    shots_.push_back({clean, ms});
    if (shots_.size() > kMaxShots) shots_.erase(shots_.begin());
}

void ShotEffect::Clear() {
    std::lock_guard guard(lock_);
    shots_.clear();
}

bool ShotEffect::Active(double nowMs) {
    std::lock_guard guard(lock_);
    shots_.erase(std::remove_if(shots_.begin(), shots_.end(), [&](const Fired& f) { return nowMs - f.ms >= kDurationMs; }), shots_.end());
    return std::any_of(shots_.begin(), shots_.end(), [&](const Fired& f) { return nowMs >= f.ms; });
}

void ShotEffect::Draw(uint8_t* nv12, uint32_t width, uint32_t height, double nowMs) {
    std::vector<Fired> shots;
    {
        std::lock_guard guard(lock_);
        shots = shots_;
    }
    shots.erase(std::remove_if(shots.begin(), shots.end(), [&](const Fired& f) { return nowMs < f.ms || nowMs - f.ms >= kDurationMs; }), shots.end());
    const int w = static_cast<int>(width & ~1u), h = static_cast<int>(height & ~1u);
    if (shots.empty() || w < 2 || h < 2) return;
    uint8_t* luma = nv12;
    uint8_t* chroma = nv12 + static_cast<size_t>(width) * height;

    // The newest shot moves the picture.
    const Fired& newest = *std::max_element(shots.begin(), shots.end(), [](const Fired& a, const Fired& b) { return a.ms < b.ms; });
    const ShotState move = At(nowMs - newest.ms, newest.shot.dirX);
    const float cx = w / 2.0f, cy = h / 2.0f;
    const float offsetX = move.shakeX * h, offsetY = move.shakeY * h;

    // 1. Shake and zoom: each output pixel takes the nearest source pixel (the picture moves too fast to notice).
    if (move.zoom > 1.0001f || std::abs(offsetX) >= 0.5f || std::abs(offsetY) >= 0.5f) {
        const size_t bytes = static_cast<size_t>(width) * height * 3 / 2;
        scratch_.assign(nv12, nv12 + bytes);
        columns_.resize(static_cast<size_t>(w));
        rows_.resize(static_cast<size_t>(h));
        for (int x = 0; x < w; ++x) {
            const float source = cx + (x + 0.5f - cx - offsetX) / move.zoom;
            columns_[x] = std::clamp(static_cast<int>(std::floor(source)), 0, w - 1);
        }
        for (int y = 0; y < h; ++y) {
            const float source = cy + (y + 0.5f - cy - offsetY) / move.zoom;
            rows_[y] = std::clamp(static_cast<int>(std::floor(source)), 0, h - 1);
        }
        const uint8_t* srcLuma = scratch_.data();
        const uint8_t* srcChroma = scratch_.data() + static_cast<size_t>(width) * height;
        for (int y = 0; y < h; ++y) {
            const uint8_t* in = srcLuma + static_cast<size_t>(rows_[y]) * width;
            uint8_t* out = luma + static_cast<size_t>(y) * width;
            for (int x = 0; x < w; ++x) out[x] = in[columns_[x]];
        }
        for (int y = 0; y < h / 2; ++y) {
            const uint8_t* in = srcChroma + static_cast<size_t>(rows_[y * 2] / 2) * width;
            uint8_t* out = chroma + static_cast<size_t>(y) * width;
            for (int x = 0; x < w / 2; ++x) {
                const int source = columns_[x * 2] / 2;
                out[x * 2] = in[source * 2];
                out[x * 2 + 1] = in[source * 2 + 1];
            }
        }
    }

    // 2. The whole frame flashes brighter for an instant (through lookup tables: every pixel is touched).
    if (move.lift > 0.004f) {
        uint8_t brighter[256], paler[256];
        for (int i = 0; i < 256; ++i) {
            brighter[i] = Mix(i, 235, move.lift);
            paler[i] = Mix(i, 128, move.lift * 0.5f);
        }
        for (int y = 0; y < h; ++y) {
            uint8_t* row = luma + static_cast<size_t>(y) * width;
            for (int x = 0; x < w; ++x) row[x] = brighter[row[x]];
        }
        for (int y = 0; y < h / 2; ++y) {
            uint8_t* row = chroma + static_cast<size_t>(y) * width;
            for (int x = 0; x < w; ++x) row[x] = paler[row[x]];
        }
    }

    // 3. Muzzle flashes: a white-hot flame along the barrel in a warm glow. They move with the picture.
    for (const Fired& fired : shots) {
        const HitCamShot& s = fired.shot;
        const float flash = At(nowMs - fired.ms, s.dirX).flash;
        if (flash <= 0.01f) continue;
        const float size = s.size * h;
        const float radius = std::max(6.0f, 0.75f * size * (0.8f + 0.2f * flash));
        float mx = s.x * w + s.dirX * 0.25f * size, my = s.y * h + s.dirY * 0.25f * size;
        mx = (mx - cx) * move.zoom + cx + offsetX;
        my = (my - cy) * move.zoom + cy + offsetY;
        const float reach = 2 * radius;
        const int x0 = std::max(0, static_cast<int>(mx - reach)) & ~1, x1 = std::min(w, static_cast<int>(mx + reach) + 2) & ~1;
        const int y0 = std::max(0, static_cast<int>(my - reach)) & ~1, y1 = std::min(h, static_cast<int>(my + reach) + 2) & ~1;
        // Flame: an ellipse along the barrel (squared distance, no roots); glow: a round falloff around it.
        const float alongScale = 1 / (1.6f * radius), acrossScale = 1 / (0.55f * radius), reach2 = 1 / (reach * reach);
        auto strength = [&](float px, float py, float& core, float& glow) {
            const float dx = px - mx, dy = py - my;
            const float along = (dx * s.dirX + dy * s.dirY - 0.4f * radius) * alongScale;
            const float across = (-dx * s.dirY + dy * s.dirX) * acrossScale;
            const float c = std::max(0.0f, 1 - (along * along + across * across));
            core = c * (2 - c);
            const float g = std::max(0.0f, 1 - (dx * dx + dy * dy) * reach2);
            glow = 0.6f * g * g;
        };
        for (int y = y0; y < y1; ++y) {
            uint8_t* row = luma + static_cast<size_t>(y) * width;
            for (int x = x0; x < x1; ++x) {
                float core, glow;
                strength(x + 0.5f, y + 0.5f, core, glow);
                const int a = static_cast<int>(std::max(core, glow) * flash * 256);
                if (a > 0) row[x] = MixFixed(row[x], 235, a);
            }
        }
        for (int y = y0 / 2; y < y1 / 2; ++y) {
            uint8_t* row = chroma + static_cast<size_t>(y) * width;
            for (int x = x0 / 2; x < x1 / 2; ++x) {
                float core, glow;
                strength(x * 2 + 1.0f, y * 2 + 1.0f, core, glow);
                const float a = std::max(core, glow) * flash;
                if (a <= 0.004f) continue;
                const float white = std::clamp(core * 1.5f - 0.5f, 0.0f, 1.0f);
                const int u = static_cast<int>(std::lround(kFlashU + (128 - kFlashU) * white));
                const int v = static_cast<int>(std::lround(kFlashV + (128 - kFlashV) * white));
                row[x * 2] = Mix(row[x * 2], u, a);
                row[x * 2 + 1] = Mix(row[x * 2 + 1], v, a);
            }
        }
    }
}

}  // namespace hitcam
