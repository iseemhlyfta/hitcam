#include "Compositor.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>

// Compiled shaders (fxc at build time, from shaders/Composite.hlsl).
#include "shaders/CompositeChroma.h"
#include "shaders/CompositeLuma.h"
#include "shaders/CropChroma.h"
#include "shaders/CropLuma.h"
#include "shaders/DownFirstChroma.h"
#include "shaders/DownFirstLuma.h"
#include "shaders/DualDown.h"
#include "shaders/DualUp.h"
#include "shaders/BoxX.h"
#include "shaders/BoxY.h"
#include "shaders/GuideCoefs.h"
#include "shaders/GuideStats.h"

namespace hitcam {
namespace {

// Matches cbuffer Params in Composite.hlsl.
struct Params {
    uint32_t lumaSize[2];
    uint32_t chromaSize[2];
    uint32_t gridSize[2];
    uint32_t padding0[2];
    float maskLow;
    float maskHigh;
    float guideEps;
    uint32_t mode;
    uint32_t maskValid;
    uint32_t padding1[3];
    float crop[4];
};
static_assert(sizeof(Params) == 80);

// Matches cbuffer Level in Composite.hlsl.
struct Level {
    float sourceTexel[2];
    uint32_t targetSize[2];
};
static_assert(sizeof(Level) == 16);

// Pyramid levels stop at this size: smaller ones add nothing but edge effects.
constexpr uint32_t kMinLevelSide = 4;

constexpr UINT kGroupWidth = 16, kGroupHeight = 8;
constexpr unsigned long long kDeviceRetryMs = 3000;

UINT Groups(uint32_t size, UINT group) { return (size + group - 1) / group; }

float Clamp01(float value) { return value >= 0.0f && value <= 1.0f ? value : value > 1.0f ? 1.0f : 0.0f; }

uint8_t ClampByte(int value) { return static_cast<uint8_t>(value < 0 ? 0 : value > 255 ? 255 : value); }

}  // namespace

Compositor::Compositor() = default;

Compositor::~Compositor() { ReleaseDevice(); }

void Compositor::SetBackground(const HitCamBackground& value) {
    HitCamBackground clamped = value;
    if (clamped.mode < 0 || clamped.mode > 2) clamped.mode = 0;
    clamped.strength = Clamp01(value.strength);
    clamped.edge = Clamp01(value.edge);
    clamped.dilate = Clamp01(value.dilate);
    std::lock_guard guard(lock_);
    inputs_.settings = clamped;
}

void Compositor::SetMask(const uint8_t* mask, uint32_t width, uint32_t height) {
    std::shared_ptr<const std::vector<uint8_t>> copy;
    if (mask && width > 0 && height > 0 && width <= kMaxMaskSide && height <= kMaxMaskSide) {
        copy = std::make_shared<const std::vector<uint8_t>>(mask, mask + static_cast<size_t>(width) * height);
    } else {
        width = height = 0;
    }
    std::lock_guard guard(lock_);
    inputs_.mask = std::move(copy);
    inputs_.maskWidth = width;
    inputs_.maskHeight = height;
    inputs_.maskMs = GetTickCount64();
    ++inputs_.maskVersion;
}

void Compositor::SetImage(const uint8_t* bgra, uint32_t width, uint32_t height, uint32_t stride) {
    std::shared_ptr<std::vector<uint8_t>> copy;
    // Up to 8K a side is plenty for a background; the copy is packed (pitch = width * 4).
    if (bgra && width > 0 && height > 0 && width <= 8192 && height <= 8192 && stride >= width * 4) {
        copy = std::make_shared<std::vector<uint8_t>>(static_cast<size_t>(width) * height * 4);
        for (uint32_t y = 0; y < height; ++y) {
            std::memcpy(copy->data() + static_cast<size_t>(y) * width * 4, bgra + static_cast<size_t>(y) * stride, static_cast<size_t>(width) * 4);
        }
    } else {
        width = height = 0;
    }
    std::lock_guard guard(lock_);
    inputs_.image = std::move(copy);
    inputs_.imageWidth = width;
    inputs_.imageHeight = height;
    ++inputs_.imageVersion;
}

void Compositor::SetFraming(const HitCamFraming& value) {
    auto finite = [](float v) { return v >= -1.0f && v <= 2.0f; };  // NaN fails both
    HitCamFraming framing{0, 0, 1, 1};
    if (finite(value.left) && finite(value.top) && finite(value.right) && finite(value.bottom)) {
        framing.left = std::clamp(value.left, 0.0f, 1.0f);
        framing.top = std::clamp(value.top, 0.0f, 1.0f);
        framing.right = std::clamp(value.right, framing.left, 1.0f);
        framing.bottom = std::clamp(value.bottom, framing.top, 1.0f);
    }
    std::lock_guard guard(lock_);
    inputs_.framing = framing;
    inputs_.framingMs = GetTickCount64();
}

bool Compositor::CurrentFraming(HitCamFraming* framing) {
    std::lock_guard guard(lock_);
    const HitCamFraming& f = inputs_.framing;
    // At most 8x (a crop smaller than 1/8 of the frame is not framing), and not the whole frame.
    const bool cropped = f.right - f.left >= 0.125f && f.bottom - f.top >= 0.125f &&
                         (f.left > 0.001f || f.top > 0.001f || f.right < 0.999f || f.bottom < 0.999f);
    if (!cropped || GetTickCount64() - inputs_.framingMs > kFramingStaleMs) return false;
    *framing = f;
    return true;
}

bool Compositor::Crop(uint8_t* nv12, uint32_t width, uint32_t height, const HitCamFraming& framing) {
    if (width < 16 || height < 16 || (width | height) & 1) return false;
    if (!EnsureDevice()) return false;
    if (!EnsureFrames(width, height)) {
        ReleaseDevice();
        return false;
    }
    Params params{};
    params.lumaSize[0] = width;
    params.lumaSize[1] = height;
    params.chromaSize[0] = width / 2;
    params.chromaSize[1] = height / 2;
    params.crop[0] = framing.left;
    params.crop[1] = framing.top;
    params.crop[2] = framing.right - framing.left;
    params.crop[3] = framing.bottom - framing.top;
    context_->UpdateSubresource(params_.Get(), 0, nullptr, &params, 0, 0);
    context_->UpdateSubresource(lumaIn_.texture.Get(), 0, nullptr, nv12, width, 0);
    context_->UpdateSubresource(chromaIn_.texture.Get(), 0, nullptr, nv12 + static_cast<size_t>(width) * height, width, 0);
    ID3D11Buffer* constants[] = {params_.Get()};
    context_->CSSetConstantBuffers(0, 1, constants);
    ID3D11SamplerState* samplers[] = {linearClamp_.Get()};
    context_->CSSetSamplers(0, 1, samplers);
    ID3D11ShaderResourceView* noView = nullptr;
    ID3D11UnorderedAccessView* noTarget = nullptr;
    auto dispatch = [&](ID3D11ComputeShader* shader, ID3D11ShaderResourceView* input, ID3D11UnorderedAccessView* target, uint32_t w, uint32_t h) {
        context_->CSSetShader(shader, nullptr, 0);
        context_->CSSetShaderResources(0, 1, &input);
        context_->CSSetUnorderedAccessViews(0, 1, &target, nullptr);
        context_->Dispatch(Groups(w, kGroupWidth), Groups(h, kGroupHeight), 1);
        context_->CSSetShaderResources(0, 1, &noView);
        context_->CSSetUnorderedAccessViews(0, 1, &noTarget, nullptr);
    };
    dispatch(cropLuma_.Get(), lumaIn_.srv.Get(), lumaOut_.uav.Get(), width, height);
    dispatch(cropChroma_.Get(), chromaIn_.srv.Get(), chromaOut_.uav.Get(), width / 2, height / 2);
    context_->CSSetShader(nullptr, nullptr, 0);
    if (!ReadBack(nv12, width, height)) {
        ReleaseDevice();
        return false;
    }
    return true;
}

bool Compositor::Active() {
    std::lock_guard guard(lock_);
    return inputs_.settings.mode != 0;
}

bool Compositor::Composite(uint8_t* nv12, uint32_t width, uint32_t height) {
    lastMs_ = -1;
    if (width < 16 || height < 16 || (width | height) & 1) return false;
    Inputs inputs;
    {
        std::lock_guard guard(lock_);
        inputs = inputs_;
    }
    if (inputs.settings.mode == 0) {
        // Off: the textures are not worth keeping.
        if (width_ != 0) ReleaseFrames();
        return false;
    }
    const unsigned long long age = GetTickCount64() - inputs.maskMs;
    const bool maskValid = inputs.mask && age <= kMaskStaleMs;
    if (!maskValid && !inputs.settings.failClosed) return false;

    const auto start = std::chrono::steady_clock::now();
    // Without the GPU the room must not show either (failClosed): coarse blocks on the processor until it is back.
    auto failed = [&] {
        if (!inputs.settings.failClosed) return false;
        CoverOnCpu(nv12, width, height);
        return true;
    };
    if (!EnsureDevice()) return failed();
    if (!EnsureFrames(width, height) || (maskValid && !EnsureMask(inputs)) ||
        (inputs.settings.mode == 2 && !EnsureImage(inputs, width, height))) {
        ReleaseDevice();
        return failed();
    }
    if (!Run(nv12, width, height, inputs, maskValid, age)) {
        // Device lost or out of memory: try again later.
        ReleaseDevice();
        return failed();
    }
    lastMs_ = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    return true;
}

void Compositor::CoverOnCpu(uint8_t* nv12, uint32_t width, uint32_t height) {
    // Blocks of 1/16 of the width: nothing recognizable, cheap (one pass to sum, one to fill).
    const uint32_t block = std::max<uint32_t>(16, (width / 16) & ~1u);
    uint8_t* chroma = nv12 + static_cast<size_t>(width) * height;
    for (uint32_t by = 0; by < height; by += block) {
        for (uint32_t bx = 0; bx < width; bx += block) {
            const uint32_t w = std::min(block, width - bx), h = std::min(block, height - by);
            uint64_t y = 0, u = 0, v = 0;
            for (uint32_t row = 0; row < h; ++row) {
                const uint8_t* line = nv12 + static_cast<size_t>(by + row) * width + bx;
                for (uint32_t x = 0; x < w; ++x) y += line[x];
            }
            for (uint32_t row = 0; row < h / 2; ++row) {
                const uint8_t* line = chroma + static_cast<size_t>(by / 2 + row) * width + bx;
                for (uint32_t x = 0; x + 1 < w; x += 2) {
                    u += line[x];
                    v += line[x + 1];
                }
            }
            const uint64_t pixels = static_cast<uint64_t>(w) * h, chromaPixels = std::max<uint64_t>(1, static_cast<uint64_t>(w / 2) * (h / 2));
            const uint8_t meanY = static_cast<uint8_t>(y / pixels), meanU = static_cast<uint8_t>(u / chromaPixels),
                          meanV = static_cast<uint8_t>(v / chromaPixels);
            for (uint32_t row = 0; row < h; ++row) std::memset(nv12 + static_cast<size_t>(by + row) * width + bx, meanY, w);
            for (uint32_t row = 0; row < h / 2; ++row) {
                uint8_t* line = chroma + static_cast<size_t>(by / 2 + row) * width + bx;
                for (uint32_t x = 0; x + 1 < w; x += 2) {
                    line[x] = meanU;
                    line[x + 1] = meanV;
                }
            }
        }
    }
}

bool Compositor::EnsureDevice() {
    if (device_) return true;
    const unsigned long long now = GetTickCount64();
    if (now < retryDeviceMs_) return false;
    retryDeviceMs_ = now + kDeviceRetryMs;

    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    char forced[16] = {};
    GetEnvironmentVariableA("HITCAM_GPU", forced, sizeof(forced));
    const bool warpOnly = _stricmp(forced, "warp") == 0;
    // HitCamVCamTest: no GPU at all, for the processor fallback.
    if (_stricmp(forced, "none") == 0) return false;
    HRESULT hr = E_FAIL;
    for (const D3D_DRIVER_TYPE type : {D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP}) {
        if (warpOnly && type == D3D_DRIVER_TYPE_HARDWARE) continue;
        D3D_FEATURE_LEVEL level{};
        hr = D3D11CreateDevice(nullptr, type, nullptr, D3D11_CREATE_DEVICE_SINGLETHREADED, levels,
                               static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION, &device_, &level, &context_);
        // 11_1 is unknown to plain Windows 10 runtimes without the platform update: retry with 11_0 only.
        if (hr == E_INVALIDARG) {
            hr = D3D11CreateDevice(nullptr, type, nullptr, D3D11_CREATE_DEVICE_SINGLETHREADED, levels + 1, 1,
                                   D3D11_SDK_VERSION, &device_, &level, &context_);
        }
        if (SUCCEEDED(hr)) break;
    }
    if (FAILED(hr)) return false;

    struct Shader {
        const BYTE* code;
        size_t size;
        ComPtr<ID3D11ComputeShader>* target;
    };
    const Shader shaders[] = {
        {g_GuideStats, sizeof(g_GuideStats), &guideStats_},
        {g_BoxX, sizeof(g_BoxX), &boxX_},
        {g_BoxY, sizeof(g_BoxY), &boxY_},
        {g_GuideCoefs, sizeof(g_GuideCoefs), &guideCoefs_},
        {g_DownFirstLuma, sizeof(g_DownFirstLuma), &downFirstLuma_},
        {g_DownFirstChroma, sizeof(g_DownFirstChroma), &downFirstChroma_},
        {g_DualDown, sizeof(g_DualDown), &dualDown_},
        {g_DualUp, sizeof(g_DualUp), &dualUp_},
        {g_CompositeLuma, sizeof(g_CompositeLuma), &compositeLuma_},
        {g_CompositeChroma, sizeof(g_CompositeChroma), &compositeChroma_},
        {g_CropLuma, sizeof(g_CropLuma), &cropLuma_},
        {g_CropChroma, sizeof(g_CropChroma), &cropChroma_},
    };
    for (const Shader& shader : shaders) {
        if (SUCCEEDED(hr)) hr = device_->CreateComputeShader(shader.code, shader.size, nullptr, shader.target->ReleaseAndGetAddressOf());
    }
    if (SUCCEEDED(hr)) {
        D3D11_SAMPLER_DESC desc{};
        desc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        desc.AddressU = desc.AddressV = desc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        desc.MaxLOD = D3D11_FLOAT32_MAX;
        hr = device_->CreateSamplerState(&desc, &linearClamp_);
    }
    if (SUCCEEDED(hr)) {
        D3D11_BUFFER_DESC desc{};
        desc.ByteWidth = sizeof(Params);
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        hr = device_->CreateBuffer(&desc, nullptr, &params_);
        desc.ByteWidth = sizeof(Level);
        if (SUCCEEDED(hr)) hr = device_->CreateBuffer(&desc, nullptr, &level_);
    }
    if (FAILED(hr)) {
        ReleaseDevice();
        return false;
    }
    return true;
}

bool Compositor::CreatePlane(Plane& plane, uint32_t width, uint32_t height, DXGI_FORMAT format, bool writable) {
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | (writable ? D3D11_BIND_UNORDERED_ACCESS : 0);
    if (FAILED(device_->CreateTexture2D(&desc, nullptr, plane.texture.ReleaseAndGetAddressOf()))) return false;
    if (FAILED(device_->CreateShaderResourceView(plane.texture.Get(), nullptr, plane.srv.ReleaseAndGetAddressOf()))) return false;
    return !writable || SUCCEEDED(device_->CreateUnorderedAccessView(plane.texture.Get(), nullptr, plane.uav.ReleaseAndGetAddressOf()));
}

bool Compositor::EnsureFrames(uint32_t width, uint32_t height) {
    if (width == width_ && height == height_ && lumaIn_.texture) return true;
    ReleaseFrames();
    const uint32_t cw = width / 2, ch = height / 2;
    bool ok = CreatePlane(lumaIn_, width, height, DXGI_FORMAT_R8_UNORM, false)
              && CreatePlane(chromaIn_, cw, ch, DXGI_FORMAT_R8G8_UNORM, false)
              && CreatePlane(lumaOut_, width, height, DXGI_FORMAT_R8_UNORM, true)
              && CreatePlane(chromaOut_, cw, ch, DXGI_FORMAT_R8G8_UNORM, true);
    gridWidth_ = (width + kGridScale - 1) / kGridScale;
    gridHeight_ = (height + kGridScale - 1) / kGridScale;
    for (Plane& plane : grid_) ok = ok && CreatePlane(plane, gridWidth_, gridHeight_, DXGI_FORMAT_R32G32B32A32_FLOAT, true);
    // The pyramid: halving from half size while the chroma level (the smaller one) stays usable.
    levels_ = 0;
    uint32_t lw = (width + 1) / 2, lh = (height + 1) / 2, sw = (cw + 1) / 2, sh = (ch + 1) / 2;
    while (ok && levels_ < kLevels && std::min(sw, sh) >= kMinLevelSide) {
        ok = CreatePlane(lumaDown_[levels_], lw, lh, DXGI_FORMAT_R16G16B16A16_FLOAT, true)
             && CreatePlane(lumaUp_[levels_], lw, lh, DXGI_FORMAT_R16G16B16A16_FLOAT, true)
             && CreatePlane(chromaDown_[levels_], sw, sh, DXGI_FORMAT_R16G16B16A16_FLOAT, true)
             && CreatePlane(chromaUp_[levels_], sw, sh, DXGI_FORMAT_R16G16B16A16_FLOAT, true);
        lumaLevel_[levels_][0] = lw;
        lumaLevel_[levels_][1] = lh;
        chromaLevel_[levels_][0] = sw;
        chromaLevel_[levels_][1] = sh;
        ++levels_;
        lw = (lw + 1) / 2;
        lh = (lh + 1) / 2;
        sw = (sw + 1) / 2;
        sh = (sh + 1) / 2;
    }
    ok = ok && levels_ > 0;
    for (int i = 0; i < 2 && ok; ++i) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = i == 0 ? width : cw;
        desc.Height = i == 0 ? height : ch;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = i == 0 ? DXGI_FORMAT_R8_UNORM : DXGI_FORMAT_R8G8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ok = SUCCEEDED(device_->CreateTexture2D(&desc, nullptr, i == 0 ? &lumaStaging_ : &chromaStaging_));
    }
    if (!ok) {
        ReleaseFrames();
        return false;
    }
    width_ = width;
    height_ = height;
    return true;
}

bool Compositor::EnsureMask(const Inputs& inputs) {
    if (inputs.maskWidth != maskWidth_ || inputs.maskHeight != maskHeight_ || !mask_.texture) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = inputs.maskWidth;
        desc.Height = inputs.maskHeight;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(device_->CreateTexture2D(&desc, nullptr, mask_.texture.ReleaseAndGetAddressOf()))) return false;
        if (FAILED(device_->CreateShaderResourceView(mask_.texture.Get(), nullptr, mask_.srv.ReleaseAndGetAddressOf()))) return false;
        maskWidth_ = inputs.maskWidth;
        maskHeight_ = inputs.maskHeight;
        maskVersion_ = 0;
    }
    if (maskVersion_ != inputs.maskVersion) {
        context_->UpdateSubresource(mask_.texture.Get(), 0, nullptr, inputs.mask->data(), inputs.maskWidth, 0);
        maskVersion_ = inputs.maskVersion;
    }
    return true;
}

bool Compositor::EnsureImage(const Inputs& inputs, uint32_t width, uint32_t height) {
    if (!inputs.image) return true;  // no picture: the shader blurs instead
    if (imageVersion_ == inputs.imageVersion && imageFrameWidth_ == width && imageFrameHeight_ == height && imageLuma_.texture) return true;
    const uint32_t cw = width / 2, ch = height / 2;
    if (!CreatePlane(imageLuma_, width, height, DXGI_FORMAT_R8_UNORM, false) ||
        !CreatePlane(imageChroma_, cw, ch, DXGI_FORMAT_R8G8_UNORM, false)) {
        return false;
    }
    // Once per picture and frame size, on the CPU: scaled to cover the frame (cropped, never stretched), to NV12
    // BT.709 video range like the phones' streams.
    const uint32_t iw = inputs.imageWidth, ih = inputs.imageHeight;
    const double scale = std::max(static_cast<double>(width) / iw, static_cast<double>(height) / ih);
    const double offsetX = (iw * scale - width) / 2, offsetY = (ih * scale - height) / 2;
    const uint8_t* image = inputs.image->data();
    auto sample = [&](uint32_t x, uint32_t y) {
        const uint32_t sx = std::min<uint32_t>(iw - 1, static_cast<uint32_t>((x + 0.5 + offsetX) / scale));
        const uint32_t sy = std::min<uint32_t>(ih - 1, static_cast<uint32_t>((y + 0.5 + offsetY) / scale));
        return image + (static_cast<size_t>(sy) * iw + sx) * 4;
    };
    std::vector<uint8_t> luma(static_cast<size_t>(width) * height), chroma(static_cast<size_t>(cw) * ch * 2);
    for (uint32_t y = 0; y < height; ++y) {
        for (uint32_t x = 0; x < width; ++x) {
            const uint8_t* p = sample(x, y);
            luma[static_cast<size_t>(y) * width + x] = ClampByte(16 + ((47 * p[2] + 157 * p[1] + 16 * p[0] + 128) >> 8));
        }
    }
    for (uint32_t y = 0; y < ch; ++y) {
        for (uint32_t x = 0; x < cw; ++x) {
            // The 2x2 pixels a chroma sample covers, averaged (no colour fringes on fine picture detail).
            int r = 0, g = 0, b = 0;
            for (uint32_t dy = 0; dy < 2; ++dy) {
                for (uint32_t dx = 0; dx < 2; ++dx) {
                    const uint8_t* p = sample(x * 2 + dx, y * 2 + dy);
                    b += p[0];
                    g += p[1];
                    r += p[2];
                }
            }
            chroma[(static_cast<size_t>(y) * cw + x) * 2] = ClampByte(128 + ((-26 * r - 87 * g + 112 * b + 512) >> 10));
            chroma[(static_cast<size_t>(y) * cw + x) * 2 + 1] = ClampByte(128 + ((112 * r - 102 * g - 10 * b + 512) >> 10));
        }
    }
    context_->UpdateSubresource(imageLuma_.texture.Get(), 0, nullptr, luma.data(), width, 0);
    context_->UpdateSubresource(imageChroma_.texture.Get(), 0, nullptr, chroma.data(), cw * 2, 0);
    imageVersion_ = inputs.imageVersion;
    imageFrameWidth_ = width;
    imageFrameHeight_ = height;
    return true;
}

bool Compositor::Run(uint8_t* nv12, uint32_t width, uint32_t height, const Inputs& inputs, bool maskValid,
                     unsigned long long maskAgeMs) {
    const HitCamBackground& s = inputs.settings;
    const uint32_t cw = width / 2, ch = height / 2;
    uint8_t* chroma = nv12 + static_cast<size_t>(width) * height;

    Params params{};
    params.lumaSize[0] = width;
    params.lumaSize[1] = height;
    params.chromaSize[0] = cw;
    params.chromaSize[1] = ch;
    params.gridSize[0] = gridWidth_;
    params.gridSize[1] = gridHeight_;
    // The edge sits around 0.5 of the mask; more room (dilate, an older mask) moves it outwards.
    const float aging = maskAgeMs <= kMaskFreshMs ? 0.0f
                        : 0.2f * std::min(1.0f, static_cast<float>(maskAgeMs - kMaskFreshMs) / (kMaskStaleMs - kMaskFreshMs));
    const float centre = 0.5f - 0.35f * s.dilate - aging;
    const float soft = 0.04f + 0.4f * s.edge;
    params.maskLow = std::max(0.01f, centre - soft / 2);
    params.maskHigh = std::max(params.maskLow + 0.01f, centre + soft / 2);
    // The mask follows the picture where its luma varies more than this (std 0.03: a shoulder against a wall), and
    // stays the coarse mask on flat areas and noise.
    params.guideEps = 1e-3f;
    // Pyramid depth from the strength: 2 levels below half size is a light blur, 5 a heavy one.
    const int depth = std::min(levels_ - 1, 2 + static_cast<int>(std::lround(s.strength * 3)));
    params.mode = static_cast<uint32_t>(s.mode == 2 && inputs.image ? 2 : 1);
    params.maskValid = maskValid ? 1 : 0;
    context_->UpdateSubresource(params_.Get(), 0, nullptr, &params, 0, 0);

    context_->UpdateSubresource(lumaIn_.texture.Get(), 0, nullptr, nv12, width, 0);
    context_->UpdateSubresource(chromaIn_.texture.Get(), 0, nullptr, chroma, width, 0);

    ID3D11Buffer* constants[] = {params_.Get(), level_.Get()};
    context_->CSSetConstantBuffers(0, 2, constants);
    ID3D11SamplerState* samplers[] = {linearClamp_.Get()};
    context_->CSSetSamplers(0, 1, samplers);
    ID3D11ShaderResourceView* noViews[6] = {};
    ID3D11UnorderedAccessView* noTargets[1] = {};
    ID3D11ShaderResourceView* refined = nullptr;  // the guided filter's result, once computed
    // Slots: t0 input, t1 blurred background, t2 mask, t3 replacement picture, t4 the frame's luma, t5 refined mask.
    auto dispatch = [&](ID3D11ComputeShader* shader, ID3D11ShaderResourceView* input, ID3D11ShaderResourceView* blurred,
                        ID3D11ShaderResourceView* image, ID3D11UnorderedAccessView* target, uint32_t w, uint32_t h) {
        ID3D11ShaderResourceView* views[6] = {input, blurred, maskValid ? mask_.srv.Get() : nullptr, image, lumaIn_.srv.Get(), refined};
        context_->CSSetShader(shader, nullptr, 0);
        context_->CSSetShaderResources(0, 6, views);
        context_->CSSetUnorderedAccessViews(0, 1, &target, nullptr);
        context_->Dispatch(Groups(w, kGroupWidth), Groups(h, kGroupHeight), 1);
        context_->CSSetShaderResources(0, 6, noViews);
        context_->CSSetUnorderedAccessViews(0, 1, noTargets, nullptr);
    };
    if (maskValid) {
        const uint32_t gw = gridWidth_, gh = gridHeight_;
        dispatch(guideStats_.Get(), nullptr, nullptr, nullptr, grid_[0].uav.Get(), gw, gh);
        dispatch(boxX_.Get(), grid_[0].srv.Get(), nullptr, nullptr, grid_[1].uav.Get(), gw, gh);
        dispatch(boxY_.Get(), grid_[1].srv.Get(), nullptr, nullptr, grid_[0].uav.Get(), gw, gh);
        dispatch(guideCoefs_.Get(), grid_[0].srv.Get(), nullptr, nullptr, grid_[1].uav.Get(), gw, gh);
        dispatch(boxX_.Get(), grid_[1].srv.Get(), nullptr, nullptr, grid_[0].uav.Get(), gw, gh);
        dispatch(boxY_.Get(), grid_[0].srv.Get(), nullptr, nullptr, grid_[1].uav.Get(), gw, gh);
        refined = grid_[1].srv.Get();
    }
    auto level = [&](const uint32_t (&source)[2], const uint32_t (&target)[2]) {
        const Level value{{1.0f / source[0], 1.0f / source[1]}, {target[0], target[1]}};
        context_->UpdateSubresource(level_.Get(), 0, nullptr, &value, 0, 0);
    };
    // A replaced background needs no blur (without a valid mask the shader blurs the whole frame even in mode 2).
    const bool replace = params.mode == 2 && maskValid;
    if (!replace) {
        struct Chain {
            ID3D11ComputeShader* first;
            ID3D11ShaderResourceView* input;
            Plane* down;
            Plane* up;
            uint32_t (*sizes)[2];
        };
        const Chain chains[] = {{downFirstLuma_.Get(), lumaIn_.srv.Get(), lumaDown_, lumaUp_, lumaLevel_},
                                {downFirstChroma_.Get(), chromaIn_.srv.Get(), chromaDown_, chromaUp_, chromaLevel_}};
        for (const Chain& c : chains) {
            level(c.sizes[0], c.sizes[0]);
            dispatch(c.first, c.input, nullptr, nullptr, c.down[0].uav.Get(), c.sizes[0][0], c.sizes[0][1]);
            for (int i = 1; i <= depth; ++i) {
                level(c.sizes[i - 1], c.sizes[i]);
                dispatch(dualDown_.Get(), c.down[i - 1].srv.Get(), nullptr, nullptr, c.down[i].uav.Get(), c.sizes[i][0], c.sizes[i][1]);
            }
            // Back up to half size; with no depth the half-size level is the result.
            for (int i = depth - 1; i >= 0; --i) {
                const Plane& source = i == depth - 1 ? c.down[depth] : c.up[i + 1];
                level(c.sizes[i + 1], c.sizes[i]);
                dispatch(dualUp_.Get(), source.srv.Get(), nullptr, nullptr, c.up[i].uav.Get(), c.sizes[i][0], c.sizes[i][1]);
            }
        }
    }
    ID3D11ShaderResourceView* lumaBlurred = replace ? nullptr : (depth > 0 ? lumaUp_[0] : lumaDown_[0]).srv.Get();
    ID3D11ShaderResourceView* chromaBlurred = replace ? nullptr : (depth > 0 ? chromaUp_[0] : chromaDown_[0]).srv.Get();
    dispatch(compositeLuma_.Get(), lumaIn_.srv.Get(), lumaBlurred, replace ? imageLuma_.srv.Get() : nullptr,
             lumaOut_.uav.Get(), width, height);
    dispatch(compositeChroma_.Get(), chromaIn_.srv.Get(), chromaBlurred, replace ? imageChroma_.srv.Get() : nullptr,
             chromaOut_.uav.Get(), cw, ch);
    context_->CSSetShader(nullptr, nullptr, 0);

    return ReadBack(nv12, width, height);
}

// lumaOut_ and chromaOut_ into the frame. Both planes are mapped before anything is copied: a failure leaves the frame as
// it was.
bool Compositor::ReadBack(uint8_t* nv12, uint32_t width, uint32_t height) {
    uint8_t* chroma = nv12 + static_cast<size_t>(width) * height;
    const uint32_t ch = height / 2;
    context_->CopyResource(lumaStaging_.Get(), lumaOut_.texture.Get());
    context_->CopyResource(chromaStaging_.Get(), chromaOut_.texture.Get());
    D3D11_MAPPED_SUBRESOURCE lumaMapped{}, chromaMapped{};
    const bool lumaOk = SUCCEEDED(context_->Map(lumaStaging_.Get(), 0, D3D11_MAP_READ, 0, &lumaMapped));
    const bool chromaOk = lumaOk && SUCCEEDED(context_->Map(chromaStaging_.Get(), 0, D3D11_MAP_READ, 0, &chromaMapped));
    if (lumaOk && chromaOk) {
        for (uint32_t y = 0; y < height; ++y) {
            std::memcpy(nv12 + static_cast<size_t>(y) * width, static_cast<const uint8_t*>(lumaMapped.pData) + static_cast<size_t>(y) * lumaMapped.RowPitch, width);
        }
        for (uint32_t y = 0; y < ch; ++y) {
            std::memcpy(chroma + static_cast<size_t>(y) * width, static_cast<const uint8_t*>(chromaMapped.pData) + static_cast<size_t>(y) * chromaMapped.RowPitch, width);
        }
    }
    if (lumaOk) context_->Unmap(lumaStaging_.Get(), 0);
    if (chromaOk) context_->Unmap(chromaStaging_.Get(), 0);
    return lumaOk && chromaOk;
}

void Compositor::ReleaseFrames() {
    for (Plane* plane : {&lumaIn_, &chromaIn_, &lumaOut_, &chromaOut_, &imageLuma_, &imageChroma_, &grid_[0], &grid_[1]}) *plane = {};
    gridWidth_ = gridHeight_ = 0;
    for (int i = 0; i < kLevels; ++i) lumaDown_[i] = lumaUp_[i] = chromaDown_[i] = chromaUp_[i] = {};
    levels_ = 0;
    lumaStaging_.Reset();
    chromaStaging_.Reset();
    width_ = height_ = 0;
    imageVersion_ = 0;
    imageFrameWidth_ = imageFrameHeight_ = 0;
}

void Compositor::ReleaseDevice() {
    ReleaseFrames();
    mask_ = {};
    maskWidth_ = maskHeight_ = 0;
    maskVersion_ = 0;
    if (context_) {
        context_->ClearState();
        context_->Flush();
    }
    guideStats_.Reset();
    boxX_.Reset();
    boxY_.Reset();
    guideCoefs_.Reset();
    downFirstLuma_.Reset();
    downFirstChroma_.Reset();
    dualDown_.Reset();
    dualUp_.Reset();
    level_.Reset();
    compositeLuma_.Reset();
    compositeChroma_.Reset();
    cropLuma_.Reset();
    cropChroma_.Reset();
    linearClamp_.Reset();
    params_.Reset();
    context_.Reset();
    device_.Reset();
}

}  // namespace hitcam
