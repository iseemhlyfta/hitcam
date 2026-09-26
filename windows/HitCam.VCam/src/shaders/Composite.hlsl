// Background blur or replacement of NV12 frames from a person mask, and the auto-framing crop; see Compositor.cpp.
// Luma is R8 (width x height), chroma R8G8 (width/2 x height/2), the mask R8 of any size stretched over the frame
// (1 = person). Compiled at build time with fxc, one entry point per shader (CMakeLists.txt).
//
// Blur: a dual-filter pyramid (halving down, doubling back up with tent filters, as in Bjorge's "dual Kawase" blur):
// large, smooth, and it never magnifies a small texture more than 2x, so no blocks show. The values are carried
// premultiplied by the background weight (1 - person), so the person's colours do not bleed into the room around them.
// The coarse mask edge is refined against the picture itself with a fast guided filter (He, Sun, Tang; locally
// person = a * luma + b, fitted on a grid at 1/4 of the frame and applied at full size), so it snaps to shoulders and
// hair instead of leaving a band of sharp room around the person.

cbuffer Params : register(b0) {
    uint2 lumaSize;
    uint2 chromaSize;
    uint2 gridSize;         // the guided filter's grid, 1/4 of the frame
    uint2 padding0;
    float maskLow;          // below: background; above maskHigh: person (a soft edge between)
    float maskHigh;
    float guideEps;         // guided filter regularization: luma variance below this keeps the coarse mask
    uint mode;              // 1 blur, 2 replace
    uint maskValid;         // 0: no usable mask, the whole frame is background
    uint3 padding1;
    float4 crop;            // auto-framing: left, top, width, height of the part shown (normalized)
};

// One pyramid pass: the texel size of the level read, the size of the level written.
cbuffer Level : register(b1) {
    float2 sourceTexel;
    uint2 targetSize;
};

SamplerState linearClamp : register(s0);
Texture2D<float> mask : register(t2);
Texture2D<float> guide : register(t4);    // the frame's luma
Texture2D<float4> refine : register(t5);  // guided filter: mean a, mean b on the grid

// How much of the pixel at `uv` belongs to the person: the refined mask, then the soft edge.
float PersonAt(float2 uv) {
    if (maskValid == 0) return 0;
    const float2 ab = refine.SampleLevel(linearClamp, uv, 0).xy;
    return smoothstep(maskLow, maskHigh, saturate(ab.x * guide.SampleLevel(linearClamp, uv, 0) + ab.y));
}

Texture2D<float4> levelIn : register(t0);
RWTexture2D<float4> levelOut : register(u0);

// ---- Guided filter on the grid: statistics, box means, coefficients, box means again.

// Per cell: mean luma, the mask, mean luma squared, luma * mask.
[numthreads(16, 8, 1)]
void GuideStats(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= gridSize)) return;
    const float2 cell = 1.0 / float2(gridSize);
    const float2 uv = (float2(id.xy) + 0.5) * cell;
    float i = 0, ii = 0;
    // Four bilinear taps cover the cell's 4x4 pixels.
    [unroll] for (int dy = 0; dy < 2; ++dy) {
        [unroll] for (int dx = 0; dx < 2; ++dx) {
            const float v = guide.SampleLevel(linearClamp, uv + (float2(dx, dy) - 0.5) * 0.5 * cell, 0);
            i += v;
            ii += v * v;
        }
    }
    i /= 4;
    ii /= 4;
    const float p = mask.SampleLevel(linearClamp, uv, 0);
    levelOut[id.xy] = float4(i, p, ii, i * p);
}

// Box mean over 2 * kBox + 1 cells (16 pixels either side of the edge at 1/4).
static const int kBox = 4;

[numthreads(16, 8, 1)]
void BoxX(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= gridSize)) return;
    float4 sum = 0;
    [unroll] for (int d = -kBox; d <= kBox; ++d) sum += levelIn[int2(clamp(int(id.x) + d, 0, int(gridSize.x) - 1), id.y)];
    levelOut[id.xy] = sum / (2 * kBox + 1);
}

[numthreads(16, 8, 1)]
void BoxY(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= gridSize)) return;
    float4 sum = 0;
    [unroll] for (int d = -kBox; d <= kBox; ++d) sum += levelIn[int2(id.x, clamp(int(id.y) + d, 0, int(gridSize.y) - 1))];
    levelOut[id.xy] = sum / (2 * kBox + 1);
}

// person = a * luma + b around each cell: a follows the picture where it varies, flat areas keep the coarse mask.
[numthreads(16, 8, 1)]
void GuideCoefs(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= gridSize)) return;
    const float4 m = levelIn[id.xy];
    const float variance = max(0, m.z - m.x * m.x);
    const float a = (m.w - m.x * m.y) / (variance + guideEps);
    levelOut[id.xy] = float4(a, m.y - a * m.x, 0, 0);
}

// ---- First step down: half size, as (value * weight, weight), weight = 1 - person, from the 2x2 pixels.
Texture2D<float> downLumaIn : register(t0);
Texture2D<float2> downChromaIn : register(t0);

[numthreads(16, 8, 1)]
void DownFirstLuma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= targetSize)) return;
    const int2 last = int2(lumaSize) - 1;
    float value = 0, weight = 0;
    [unroll] for (int dy = 0; dy < 2; ++dy) {
        [unroll] for (int dx = 0; dx < 2; ++dx) {
            const int2 p = min(int2(id.xy) * 2 + int2(dx, dy), last);
            const float w = 1 - PersonAt((float2(p) + 0.5) / float2(lumaSize));
            value += downLumaIn[p] * w;
            weight += w;
        }
    }
    levelOut[id.xy] = float4(value, 0, weight, 0) / 4.0;
}

[numthreads(16, 8, 1)]
void DownFirstChroma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= targetSize)) return;
    const int2 last = int2(chromaSize) - 1;
    float2 value = 0;
    float weight = 0;
    [unroll] for (int dy = 0; dy < 2; ++dy) {
        [unroll] for (int dx = 0; dx < 2; ++dx) {
            const int2 p = min(int2(id.xy) * 2 + int2(dx, dy), last);
            const float w = 1 - PersonAt((float2(p) + 0.5) / float2(chromaSize));
            value += downChromaIn[p] * w;
            weight += w;
        }
    }
    levelOut[id.xy] = float4(value, weight, 0) / 4.0;
}

// ---- Dual filter: down (centre and four diagonal corners) and up (tent of eight around), on premultiplied values.

[numthreads(16, 8, 1)]
void DualDown(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= targetSize)) return;
    const float2 uv = (float2(id.xy) + 0.5) / float2(targetSize);
    const float2 d = sourceTexel;
    float4 sum = levelIn.SampleLevel(linearClamp, uv, 0) * 4;
    sum += levelIn.SampleLevel(linearClamp, uv - d, 0);
    sum += levelIn.SampleLevel(linearClamp, uv + d, 0);
    sum += levelIn.SampleLevel(linearClamp, uv + float2(d.x, -d.y), 0);
    sum += levelIn.SampleLevel(linearClamp, uv - float2(d.x, -d.y), 0);
    levelOut[id.xy] = sum / 8;
}

[numthreads(16, 8, 1)]
void DualUp(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= targetSize)) return;
    const float2 uv = (float2(id.xy) + 0.5) / float2(targetSize);
    const float2 h = sourceTexel * 0.5;
    float4 sum = levelIn.SampleLevel(linearClamp, uv + float2(-h.x * 2, 0), 0);
    sum += levelIn.SampleLevel(linearClamp, uv + float2(-h.x, h.y), 0) * 2;
    sum += levelIn.SampleLevel(linearClamp, uv + float2(0, h.y * 2), 0);
    sum += levelIn.SampleLevel(linearClamp, uv + float2(h.x, h.y), 0) * 2;
    sum += levelIn.SampleLevel(linearClamp, uv + float2(h.x * 2, 0), 0);
    sum += levelIn.SampleLevel(linearClamp, uv + float2(h.x, -h.y), 0) * 2;
    sum += levelIn.SampleLevel(linearClamp, uv + float2(0, -h.y * 2), 0);
    sum += levelIn.SampleLevel(linearClamp, uv + float2(-h.x, -h.y), 0) * 2;
    levelOut[id.xy] = sum / 12;
}

// ---- Composite: the person over the blurred background (the pyramid's half-size top, 2x bilinear) or the picture.
Texture2D<float> lumaSource : register(t0);
Texture2D<float4> lumaBlurred : register(t1);
Texture2D<float> imageLuma : register(t3);
RWTexture2D<float> lumaResult : register(u0);

[numthreads(16, 8, 1)]
void CompositeLuma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= lumaSize)) return;
    const int2 p = int2(id.xy);
    const float2 uv = (float2(p) + 0.5) / float2(lumaSize);
    const float source = lumaSource[p];
    float background;
    if (mode == 2 && maskValid != 0) {
        background = imageLuma[p];
    } else {
        const float4 b = lumaBlurred.SampleLevel(linearClamp, uv, 0);
        // Deep inside the person there is no background to blur: the pixel itself (the mask keeps it anyway).
        background = b.z > 1e-3 ? b.x / b.z : source;
    }
    lumaResult[p] = lerp(background, source, PersonAt(uv));
}

Texture2D<float2> chromaSource : register(t0);
Texture2D<float4> chromaBlurred : register(t1);
Texture2D<float2> imageChroma : register(t3);
RWTexture2D<float2> chromaResult : register(u0);

[numthreads(16, 8, 1)]
void CompositeChroma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= chromaSize)) return;
    const int2 p = int2(id.xy);
    const float2 uv = (float2(p) + 0.5) / float2(chromaSize);
    const float2 source = chromaSource[p];
    float2 background;
    if (mode == 2 && maskValid != 0) {
        background = imageChroma[p];
    } else {
        const float4 b = chromaBlurred.SampleLevel(linearClamp, uv, 0);
        background = b.z > 1e-3 ? b.xy / b.z : source;
    }
    chromaResult[p] = lerp(background, source, PersonAt(uv));
}

// ---- Crop (auto-framing): the part `crop` of the frame scaled back to the whole frame, bilinear. Runs last, after
// faces, boxes and hands are drawn, so what is hidden stays hidden.
Texture2D<float> cropLumaIn : register(t0);
RWTexture2D<float> cropLumaOut : register(u0);

[numthreads(16, 8, 1)]
void CropLuma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= lumaSize)) return;
    const float2 uv = crop.xy + (float2(id.xy) + 0.5) / float2(lumaSize) * crop.zw;
    cropLumaOut[id.xy] = cropLumaIn.SampleLevel(linearClamp, uv, 0);
}

Texture2D<float2> cropChromaIn : register(t0);
RWTexture2D<float2> cropChromaOut : register(u0);

[numthreads(16, 8, 1)]
void CropChroma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= chromaSize)) return;
    const float2 uv = crop.xy + (float2(id.xy) + 0.5) / float2(chromaSize) * crop.zw;
    cropChromaOut[id.xy] = cropChromaIn.SampleLevel(linearClamp, uv, 0);
}
