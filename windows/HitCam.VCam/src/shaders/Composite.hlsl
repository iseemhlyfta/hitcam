// Background blur or replacement of NV12 frames from a person mask; see Compositor.cpp.
// Luma is R8 (width x height), chroma R8G8 (width/2 x height/2), the mask R8 of any size stretched over the frame
// (1 = person). The background is blurred at 1/8 size, weighted by (1 - mask), so the person's colours do not bleed
// into the blurred room around them. Compiled at build time with fxc, one entry point per shader (CMakeLists.txt).

cbuffer Params : register(b0) {
    uint2 lumaSize;
    uint2 chromaSize;
    uint2 smallSize;        // luma / 8, rounded up
    uint2 smallChromaSize;  // chroma / 8, rounded up
    float maskLow;          // below: background; above maskHigh: person (a soft edge between)
    float maskHigh;
    float blurStep;         // spacing of the blur taps, in 1/8-size pixels
    uint mode;              // 1 blur, 2 replace
    uint maskValid;         // 0: no usable mask, the whole frame is background
    uint3 padding;
    float4 crop;            // auto-framing: left, top, width, height of the part shown (normalized)
};

SamplerState linearClamp : register(s0);
Texture2D<float> mask : register(t2);

// How much of the pixel at `uv` (0..1 of the frame) belongs to the person, with the soft edge applied.
float PersonAt(float2 uv) {
    if (maskValid == 0) return 0;
    return smoothstep(maskLow, maskHigh, mask.SampleLevel(linearClamp, uv, 0));
}

// ---- Down: the background at 1/8 size as (value * weight, weight), weight = 1 - person.
Texture2D<float> downLumaIn : register(t0);
RWTexture2D<float4> downLumaOut : register(u0);

[numthreads(16, 8, 1)]
void DownLuma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallSize)) return;
    const int2 last = int2(lumaSize) - 1;
    const int2 origin = int2(id.xy) * 8;
    float value = 0, weight = 0;
    [unroll] for (int dy = 0; dy < 8; ++dy) {
        [unroll] for (int dx = 0; dx < 8; ++dx) {
            const int2 p = min(origin + int2(dx, dy), last);
            const float w = 1 - PersonAt((float2(p) + 0.5) / float2(lumaSize));
            value += downLumaIn[p] * w;
            weight += w;
        }
    }
    downLumaOut[id.xy] = float4(value, 0, weight, 0) / 64.0;
}

Texture2D<float2> downChromaIn : register(t0);
RWTexture2D<float4> downChromaOut : register(u0);

[numthreads(16, 8, 1)]
void DownChroma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallChromaSize)) return;
    const int2 last = int2(chromaSize) - 1;
    const int2 origin = int2(id.xy) * 8;
    float2 value = 0;
    float weight = 0;
    [unroll] for (int dy = 0; dy < 8; ++dy) {
        [unroll] for (int dx = 0; dx < 8; ++dx) {
            const int2 p = min(origin + int2(dx, dy), last);
            const float w = 1 - PersonAt((float2(p) + 0.5) / float2(chromaSize));
            value += downChromaIn[p] * w;
            weight += w;
        }
    }
    downChromaOut[id.xy] = float4(value, weight, 0) / 64.0;
}

// ---- Blur: separable Gaussian over the weighted values (sigma 2.3 taps, blurStep apart, bilinear); stronger blur runs
// more passes rather than spacing the taps out, which leaves a grid.
Texture2D<float4> blurIn : register(t0);
RWTexture2D<float4> blurOut : register(u0);
static const float kGauss[5] = {0.1823, 0.1659, 0.1249, 0.0779, 0.0401};

float4 BlurAlong(uint2 id, uint2 size, float2 direction) {
    const float2 uv = (float2(id) + 0.5) / float2(size);
    const float2 step = direction * blurStep / float2(size);
    float4 sum = blurIn.SampleLevel(linearClamp, uv, 0) * kGauss[0];
    [unroll] for (int i = 1; i <= 4; ++i) {
        sum += (blurIn.SampleLevel(linearClamp, uv + step * i, 0) + blurIn.SampleLevel(linearClamp, uv - step * i, 0)) * kGauss[i];
    }
    return sum;
}

[numthreads(16, 8, 1)]
void BlurLumaX(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallSize)) return;
    blurOut[id.xy] = BlurAlong(id.xy, smallSize, float2(1, 0));
}

[numthreads(16, 8, 1)]
void BlurLumaY(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallSize)) return;
    blurOut[id.xy] = BlurAlong(id.xy, smallSize, float2(0, 1));
}

[numthreads(16, 8, 1)]
void BlurChromaX(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallChromaSize)) return;
    blurOut[id.xy] = BlurAlong(id.xy, smallChromaSize, float2(1, 0));
}

[numthreads(16, 8, 1)]
void BlurChromaY(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= smallChromaSize)) return;
    blurOut[id.xy] = BlurAlong(id.xy, smallChromaSize, float2(0, 1));
}

// ---- Composite: the person over the blurred background or the replacement picture.
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
