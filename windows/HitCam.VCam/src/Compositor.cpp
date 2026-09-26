#include "Compositor.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>

// Compiled shaders (fxc at build time, from shaders/Composite.hlsl).
#include "shaders/BlurChromaX.h"
#include "shaders/BlurChromaY.h"
#include "shaders/BlurLumaX.h"
#include "shaders/BlurLumaY.h"
#include "shaders/CompositeChroma.h"
#include "shaders/CompositeLuma.h"
#include "shaders/DownChroma.h"
#include "shaders/DownLuma.h"

namespace hitcam {
namespace {

// Matches cbuffer Params in Composite.hlsl.
struct Params {
    uint32_t lumaSize[2];
    uint32_t chromaSize[2];
    uint32_t smallSize[2];
    uint32_t smallChromaSize[2];
    float maskLow;
    float maskHigh;
    float blurStep;
    uint32_t mode;
    uint32_t maskValid;
    uint32_t padding[3];
};
static_assert(sizeof(Params) % 16 == 0);

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
    if (!EnsureDevice()) return false;
    if (!EnsureFrames(width, height) || (maskValid && !EnsureMask(inputs)) ||
        (inputs.settings.mode == 2 && !EnsureImage(inputs, width, height))) {
        ReleaseDevice();
        return false;
    }
    if (!Run(nv12, width, height, inputs, maskValid, age)) {
        // Device lost or out of memory: pass through, try again later.
        ReleaseDevice();
        return false;
    }
    lastMs_ = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    return true;
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
        {g_DownLuma, sizeof(g_DownLuma), &downLuma_},
        {g_DownChroma, sizeof(g_DownChroma), &downChroma_},
        {g_BlurLumaX, sizeof(g_BlurLumaX), &blurLumaX_},
        {g_BlurLumaY, sizeof(g_BlurLumaY), &blurLumaY_},
        {g_BlurChromaX, sizeof(g_BlurChromaX), &blurChromaX_},
        {g_BlurChromaY, sizeof(g_BlurChromaY), &blurChromaY_},
        {g_CompositeLuma, sizeof(g_CompositeLuma), &compositeLuma_},
        {g_CompositeChroma, sizeof(g_CompositeChroma), &compositeChroma_},
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
    const uint32_t sw = (width + 7) / 8, sh = (height + 7) / 8;
    const uint32_t scw = (cw + 7) / 8, sch = (ch + 7) / 8;
    bool ok = CreatePlane(lumaIn_, width, height, DXGI_FORMAT_R8_UNORM, false)
              && CreatePlane(chromaIn_, cw, ch, DXGI_FORMAT_R8G8_UNORM, false)
              && CreatePlane(lumaOut_, width, height, DXGI_FORMAT_R8_UNORM, true)
              && CreatePlane(chromaOut_, cw, ch, DXGI_FORMAT_R8G8_UNORM, true);
    for (int i = 0; i < 2 && ok; ++i) {
        ok = CreatePlane(smallLuma_[i], sw, sh, DXGI_FORMAT_R16G16B16A16_FLOAT, true)
             && CreatePlane(smallChroma_[i], scw, sch, DXGI_FORMAT_R16G16B16A16_FLOAT, true);
    }
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
            const uint8_t* p = sample(x * 2, y * 2);
            chroma[(static_cast<size_t>(y) * cw + x) * 2] = ClampByte(128 + ((-26 * p[2] - 87 * p[1] + 112 * p[0] + 128) >> 8));
            chroma[(static_cast<size_t>(y) * cw + x) * 2 + 1] = ClampByte(128 + ((112 * p[2] - 102 * p[1] - 10 * p[0] + 128) >> 8));
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
    params.smallSize[0] = (width + 7) / 8;
    params.smallSize[1] = (height + 7) / 8;
    params.smallChromaSize[0] = (cw + 7) / 8;
    params.smallChromaSize[1] = (ch + 7) / 8;
    // The edge sits around 0.5 of the mask; more room (dilate, an older mask) moves it outwards.
    const float aging = maskAgeMs <= kMaskFreshMs ? 0.0f
                        : 0.2f * std::min(1.0f, static_cast<float>(maskAgeMs - kMaskFreshMs) / (kMaskStaleMs - kMaskFreshMs));
    const float centre = 0.5f - 0.35f * s.dilate - aging;
    const float soft = 0.04f + 0.4f * s.edge;
    params.maskLow = std::max(0.01f, centre - soft / 2);
    params.maskHigh = std::max(params.maskLow + 0.01f, centre + soft / 2);
    params.blurStep = 1.0f + 3.0f * s.strength;
    params.mode = static_cast<uint32_t>(s.mode == 2 && inputs.image ? 2 : 1);
    params.maskValid = maskValid ? 1 : 0;
    context_->UpdateSubresource(params_.Get(), 0, nullptr, &params, 0, 0);

    context_->UpdateSubresource(lumaIn_.texture.Get(), 0, nullptr, nv12, width, 0);
    context_->UpdateSubresource(chromaIn_.texture.Get(), 0, nullptr, chroma, width, 0);

    ID3D11Buffer* constants[] = {params_.Get()};
    context_->CSSetConstantBuffers(0, 1, constants);
    ID3D11SamplerState* samplers[] = {linearClamp_.Get()};
    context_->CSSetSamplers(0, 1, samplers);
    ID3D11ShaderResourceView* noViews[4] = {};
    ID3D11UnorderedAccessView* noTargets[1] = {};
    // Slots: t0 input, t1 blurred background, t2 mask, t3 replacement picture.
    auto dispatch = [&](ID3D11ComputeShader* shader, ID3D11ShaderResourceView* input, ID3D11ShaderResourceView* blurred,
                        ID3D11ShaderResourceView* image, ID3D11UnorderedAccessView* target, uint32_t w, uint32_t h) {
        ID3D11ShaderResourceView* views[4] = {input, blurred, maskValid ? mask_.srv.Get() : nullptr, image};
        context_->CSSetShader(shader, nullptr, 0);
        context_->CSSetShaderResources(0, 4, views);
        context_->CSSetUnorderedAccessViews(0, 1, &target, nullptr);
        context_->Dispatch(Groups(w, kGroupWidth), Groups(h, kGroupHeight), 1);
        context_->CSSetShaderResources(0, 4, noViews);
        context_->CSSetUnorderedAccessViews(0, 1, noTargets, nullptr);
    };
    const uint32_t sw = params.smallSize[0], sh = params.smallSize[1];
    const uint32_t scw = params.smallChromaSize[0], sch = params.smallChromaSize[1];
    dispatch(downLuma_.Get(), lumaIn_.srv.Get(), nullptr, nullptr, smallLuma_[0].uav.Get(), sw, sh);
    dispatch(blurLumaX_.Get(), smallLuma_[0].srv.Get(), nullptr, nullptr, smallLuma_[1].uav.Get(), sw, sh);
    dispatch(blurLumaY_.Get(), smallLuma_[1].srv.Get(), nullptr, nullptr, smallLuma_[0].uav.Get(), sw, sh);
    dispatch(downChroma_.Get(), chromaIn_.srv.Get(), nullptr, nullptr, smallChroma_[0].uav.Get(), scw, sch);
    dispatch(blurChromaX_.Get(), smallChroma_[0].srv.Get(), nullptr, nullptr, smallChroma_[1].uav.Get(), scw, sch);
    dispatch(blurChromaY_.Get(), smallChroma_[1].srv.Get(), nullptr, nullptr, smallChroma_[0].uav.Get(), scw, sch);
    const bool replace = params.mode == 2;
    dispatch(compositeLuma_.Get(), lumaIn_.srv.Get(), smallLuma_[0].srv.Get(), replace ? imageLuma_.srv.Get() : nullptr,
             lumaOut_.uav.Get(), width, height);
    dispatch(compositeChroma_.Get(), chromaIn_.srv.Get(), smallChroma_[0].srv.Get(), replace ? imageChroma_.srv.Get() : nullptr,
             chromaOut_.uav.Get(), cw, ch);
    context_->CSSetShader(nullptr, nullptr, 0);

    context_->CopyResource(lumaStaging_.Get(), lumaOut_.texture.Get());
    context_->CopyResource(chromaStaging_.Get(), chromaOut_.texture.Get());
    // Both planes are mapped before anything is copied: a failure leaves the frame as it was.
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
    for (Plane* plane : {&lumaIn_, &chromaIn_, &lumaOut_, &chromaOut_, &smallLuma_[0], &smallLuma_[1], &smallChroma_[0], &smallChroma_[1],
                         &imageLuma_, &imageChroma_}) {
        *plane = {};
    }
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
    downLuma_.Reset();
    downChroma_.Reset();
    blurLumaX_.Reset();
    blurLumaY_.Reset();
    blurChromaX_.Reset();
    blurChromaY_.Reset();
    compositeLuma_.Reset();
    compositeChroma_.Reset();
    linearClamp_.Reset();
    params_.Reset();
    context_.Reset();
    device_.Reset();
}

}  // namespace hitcam
