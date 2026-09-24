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
    float2 padding;
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

// ---- Luma adjustments: unsharp mask (noise-gated, halo-limited), then the tone curve (256-entry table, output 0..1).
Texture2D<float> lumaSource : register(t0);
Buffer<float> toneCurve : register(t1);
RWTexture2D<float> lumaAdjusted : register(u0);

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
    const float x = saturate(y) * 255.0;
    const uint i = min(uint(x), 254u);
    lumaAdjusted[p] = saturate(lerp(toneCurve[i], toneCurve[i + 1], x - i));
}

// ---- Chroma adjustments: saturation around neutral (128), kept in the video range.
Texture2D<float2> chromaSource : register(t0);
RWTexture2D<float2> chromaAdjusted : register(u0);

[numthreads(16, 8, 1)]
void AdjustChroma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= chromaSize)) return;
    const int2 p = int2(id.xy);
    const float2 c = chromaSource[p] * 255.0 - 128.0;
    chromaAdjusted[p] = clamp(c * saturation + 128.0, 16.0, 240.0) / 255.0;
}
