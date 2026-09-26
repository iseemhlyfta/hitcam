// Picture processing of NV12 frames (BT.709, video range) with Direct3D 11 compute shaders; see GpuProcessor.cpp.
// Luma is an R8 texture (width x height), chroma an R8G8 texture (width/2 x height/2); values are 0..1 (= 0..255).
// Compiled at build time with fxc, one entry point per shader (CMakeLists.txt).

cbuffer Params : register(b0) {
    uint2 lumaSize;
    uint2 chromaSize;
    // Temporal noise reduction: largest weight of the history, and the motion thresholds (0..1 units) below which
    // the full weight applies and above which none does.
    float historyWeight;
    float motionLow;
    float motionHigh;
    float sharpness;        // 0..~2: amount of the unsharp mask
    float sharpenThreshold; // detail below this (0..1 units) is noise and not amplified
    float saturation;       // chroma scale around 128, 1 = neutral
    float detail;           // 0..1: adaptive (CAS-style) sharpening
    float clarity;          // 0..~0.7: local contrast amount
    float vibrance;         // 0..~0.7: vibrance amount
    float padding;
    uint2 smallSize;        // size of the clarity base (1/8 of the luma, rounded up)
};

// Motion measure of a pixel: the 3x3 mean difference (robust to noise), and the pixel's own difference (a third of
// it, so a thin line moving by one pixel is not averaged away).
float MotionAt(Texture2D<float> current, Texture2D<float> history, int2 p) {
    const int2 last = int2(lumaSize) - 1;
    float sum = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy) {
        [unroll] for (int dx = -1; dx <= 1; ++dx) {
            const int2 q = clamp(p + int2(dx, dy), int2(0, 0), last);
            sum += current[q] - history[q];
        }
    }
    return max(abs(sum) / 9.0, abs(current[p] - history[p]) / 3.0);
}

float HistoryWeight(float motion) {
    return historyWeight * saturate((motionHigh - motion) / (motionHigh - motionLow));
}

// ---- Temporal luma: output = mix of this frame and the previous output where nothing moves.
Texture2D<float> lumaIn : register(t0);
Texture2D<float> lumaHistory : register(t1);
RWTexture2D<float> lumaOut : register(u0);
RWTexture2D<float> weightOut : register(u1);

[numthreads(16, 8, 1)]
void TemporalLuma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= lumaSize)) return;
    const int2 p = int2(id.xy);
    const float w = HistoryWeight(MotionAt(lumaIn, lumaHistory, p));
    lumaOut[p] = lerp(lumaIn[p], lumaHistory[p], w);
    weightOut[p] = w;
}

// ---- Temporal chroma: the luma motion of the 2x2 pixels it covers, and its own colour change.
Texture2D<float2> chromaIn : register(t0);
Texture2D<float2> chromaHistory : register(t1);
Texture2D<float> lumaWeight : register(t2);
RWTexture2D<float2> chromaOut : register(u0);

[numthreads(16, 8, 1)]
void TemporalChroma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= chromaSize)) return;
    const int2 p = int2(id.xy);
    const int2 lastLuma = int2(lumaSize) - 1;
    const int2 l = p * 2;
    const float lumaW = min(min(lumaWeight[min(l, lastLuma)], lumaWeight[min(l + int2(1, 0), lastLuma)]),
                            min(lumaWeight[min(l + int2(0, 1), lastLuma)], lumaWeight[min(l + int2(1, 1), lastLuma)]));
    const int2 last = int2(chromaSize) - 1;
    float2 sum = 0;
    [unroll] for (int dy = -1; dy <= 1; ++dy) {
        [unroll] for (int dx = -1; dx <= 1; ++dx) {
            const int2 q = clamp(p + int2(dx, dy), int2(0, 0), last);
            sum += chromaIn[q] - chromaHistory[q];
        }
    }
    const float2 own = abs(chromaIn[p] - chromaHistory[p]) / 3.0;
    const float2 mean = abs(sum) / 9.0;
    const float motion = max(max(mean.x, mean.y), max(own.x, own.y));
    const float w = min(lumaW, HistoryWeight(motion));
    chromaOut[p] = lerp(chromaIn[p], chromaHistory[p], w);
}

// ---- Clarity base: the luma's low frequencies. 8x8 means, then a separable Gaussian (sigma ~2.3 there, ~18 px of a
// 1080p frame); AdjustLuma samples it bilinearly. At 1/64 of the pixels this costs next to nothing.
Texture2D<float> clarityLuma : register(t0);
RWTexture2D<float> clarityDown : register(u0);

[numthreads(16, 8, 1)]
void ClarityDown(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallSize)) return;
    const int2 last = int2(lumaSize) - 1;
    const int2 origin = int2(id.xy) * 8;
    float sum = 0;
    [unroll] for (int dy = 0; dy < 8; ++dy) {
        [unroll] for (int dx = 0; dx < 8; ++dx) sum += clarityLuma[min(origin + int2(dx, dy), last)];
    }
    clarityDown[id.xy] = sum / 64.0;
}

Texture2D<float> claritySource : register(t0);
RWTexture2D<float> clarityBlurred : register(u0);
static const float kGauss[5] = {0.1823, 0.1659, 0.1249, 0.0779, 0.0401};  // sigma 2.3, taps -4..4, sums to 1

float BlurAlong(int2 p, int2 step) {
    const int2 last = int2(smallSize) - 1;
    float sum = claritySource[p] * kGauss[0];
    [unroll] for (int i = 1; i <= 4; ++i) {
        sum += (claritySource[clamp(p + step * i, int2(0, 0), last)] + claritySource[clamp(p - step * i, int2(0, 0), last)]) * kGauss[i];
    }
    return sum;
}

[numthreads(16, 8, 1)]
void ClarityBlurX(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallSize)) return;
    clarityBlurred[id.xy] = BlurAlong(int2(id.xy), int2(1, 0));
}

[numthreads(16, 8, 1)]
void ClarityBlurY(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallSize)) return;
    clarityBlurred[id.xy] = BlurAlong(int2(id.xy), int2(0, 1));
}

// ---- Luma adjustments: unsharp mask (noise-gated, halo-limited), clarity and adaptive sharpening (enhancement), then
// the tone curve (256-entry table, output 0..1).
Texture2D<float> lumaSource : register(t0);
Buffer<float> toneCurve : register(t1);
Texture2D<float> clarityBase : register(t2);
SamplerState linearClamp : register(s0);
RWTexture2D<float> lumaAdjusted : register(u0);

// Video-range luma (16..235) as 0..1.
float Level(float v) { return saturate((v * 255.0 - 16.0) / 219.0); }

[numthreads(16, 8, 1)]
void AdjustLuma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= lumaSize)) return;
    const int2 p = int2(id.xy);
    const int2 last = int2(lumaSize) - 1;
    float y = lumaSource[p];
    if (sharpness > 0) {
        // 5x5 binomial blur; the local 3x3 range limits overshoot (halos) around strong edges.
        static const float kTaps[5] = {1, 4, 6, 4, 1};
        float blur = 0;
        float low = y, high = y;
        [unroll] for (int dy = -2; dy <= 2; ++dy) {
            [unroll] for (int dx = -2; dx <= 2; ++dx) {
                const float v = lumaSource[clamp(p + int2(dx, dy), int2(0, 0), last)];
                blur += v * kTaps[dx + 2] * kTaps[dy + 2];
                if (abs(dx) <= 1 && abs(dy) <= 1) {
                    low = min(low, v);
                    high = max(high, v);
                }
            }
        }
        const float detail = y - blur / 256.0;
        const float gated = sign(detail) * max(abs(detail) - sharpenThreshold, 0.0);
        const float margin = 3.0 / 255.0;
        y = clamp(y + sharpness * gated, low - margin, high + margin);
    }
    const float original = lumaSource[p];
    if (clarity > 0) {
        // Local contrast: the difference to the low frequencies, mostly in the midtones (never pushes black or white).
        // Only moderate differences (texture, shape) are raised; big ones (a head against a bright wall) fade out, so
        // strong edges get no halos.
        const float base = clarityBase.SampleLevel(linearClamp, (float2(p) + 0.5) / float2(lumaSize), 0);
        const float level = 2.0 * Level(original) - 1.0;
        const float difference = original - base;
        const float relative = difference / (20.0 / 255.0);
        y += clamp(clarity * difference * exp(-relative * relative) * (1.0 - level * level), -0.05, 0.05);
    }
    if (detail > 0) {
        // CAS: sharpening shrinks where local contrast is already high (no halos), and a range gate leaves flat areas
        // (compression blocks, skin, walls) alone.
        const float n = lumaSource[clamp(p + int2(0, -1), int2(0, 0), last)];
        const float s = lumaSource[clamp(p + int2(0, 1), int2(0, 0), last)];
        const float w = lumaSource[clamp(p + int2(-1, 0), int2(0, 0), last)];
        const float e = lumaSource[clamp(p + int2(1, 0), int2(0, 0), last)];
        float low = min(original, min(min(n, s), min(w, e)));
        float high = max(original, max(max(n, s), max(w, e)));
        [unroll] for (int cy = -1; cy <= 1; cy += 2) {
            [unroll] for (int cx = -1; cx <= 1; cx += 2) {
                const float v = lumaSource[clamp(p + int2(cx, cy), int2(0, 0), last)];
                low = min(low, v);
                high = max(high, v);
            }
        }
        const float lowLevel = Level(low), highLevel = Level(high);
        const float amplitude = sqrt(saturate(min(lowLevel, 1.0 - highLevel) / max(highLevel, 1e-4)));
        const float weight = amplitude * (-1.0 / (8.0 - 3.0 * detail));
        const float sharpened = (original + weight * (n + s + w + e)) / (1.0 + 4.0 * weight);
        const float gate = saturate((highLevel - lowLevel - 0.012) / 0.03);
        y += (sharpened - original) * gate;
    }
    const float x = saturate(y) * 255.0;
    const uint i = min(uint(x), 254u);
    lumaAdjusted[p] = saturate(lerp(toneCurve[i], toneCurve[i + 1], x - i));
}

// ---- Chroma adjustments: saturation around neutral (128) and vibrance, kept in the video range.
Texture2D<float2> chromaSource : register(t0);
RWTexture2D<float2> chromaAdjusted : register(u0);

[numthreads(16, 8, 1)]
void AdjustChroma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= chromaSize)) return;
    const int2 p = int2(id.xy);
    float2 c = (chromaSource[p] * 255.0 - 128.0) * saturation;
    // No chroma, no hue: atan2(0, 0) is NaN on the GPU, and the clamp below turns NaN into green.
    if (vibrance > 0 && dot(c, c) > 1e-6) {
        // Muted colours gain the most, saturated ones little; skin (hue ~135 degrees in U/V) is spared.
        const float2 uv = c / 224.0;
        const float gain = vibrance * saturate(1.0 - length(uv) / 0.25);
        const float hue = degrees(atan2(uv.y, uv.x));
        const float skin = exp(-((hue - 135.0) / 25.0) * ((hue - 135.0) / 25.0));
        c *= 1.0 + gain * (1.0 - 0.6 * skin);
    }
    chromaAdjusted[p] = clamp(c + 128.0, 16.0, 240.0) / 255.0;
}
