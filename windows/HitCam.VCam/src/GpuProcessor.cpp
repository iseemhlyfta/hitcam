#include "GpuProcessor.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstring>

// Compiled shaders (fxc at build time, from shaders/Process.hlsl).
#include "shaders/AdjustChroma.h"
#include "shaders/AdjustLuma.h"
#include "shaders/ClarityBlurX.h"
#include "shaders/ClarityBlurY.h"
#include "shaders/ClarityDown.h"
#include "shaders/TemporalChroma.h"
#include "shaders/TemporalLuma.h"

namespace hitcam {
namespace {

// Matches cbuffer Params in Process.hlsl.
struct Params {
    uint32_t lumaSize[2];
    uint32_t chromaSize[2];
    float historyWeight;
    float motionLow;
    float motionHigh;
    float sharpness;
    float sharpenThreshold;
    float saturation;
    float detail;
    float clarity;
    float vibrance;
    float padding;
    uint32_t smallSize[2];
};
static_assert(sizeof(Params) % 16 == 0);

constexpr UINT kGroupWidth = 16, kGroupHeight = 8;

UINT Groups(uint32_t size, UINT group) { return (size + group - 1) / group; }

// After a failed or lost device, how long frames pass through before trying again.
constexpr unsigned long long kDeviceRetryMs = 3000;

// Scene cut: mean difference of 4x4 block means (on a 32-pixel grid) to the previous frame, 0..255 scale.
constexpr uint32_t kCutGrid = 32;
constexpr double kCutThreshold = 20.0;

float Clamp(float value, float low, float high) {
    // NaN fails every comparison: neutral.
    if (!(value >= low && value <= high)) return value > high ? high : value < low ? low : 0.0f;
    return value;
}

}  // namespace

HitCamProcessing ClampProcessing(const HitCamProcessing& s) {
    HitCamProcessing result = s;
    result.temporalStrength = std::clamp(s.temporalStrength, 0, 100);
    result.artifactReduction = std::clamp(s.artifactReduction, 0, 2);
    result.brightness = Clamp(s.brightness, -1, 1);
    result.contrast = Clamp(s.contrast, -1, 1);
    result.saturation = Clamp(s.saturation, -1, 1);
    result.sharpness = Clamp(s.sharpness, 0, 1);
    result.shadows = Clamp(s.shadows, -1, 1);
    result.highlights = Clamp(s.highlights, -1, 1);
    result.detail = Clamp(s.detail, 0, 1);
    result.clarity = Clamp(s.clarity, 0, 1);
    result.vibrance = Clamp(s.vibrance, 0, 1);
    return result;
}

GpuProcessor::GpuProcessor() = default;
GpuProcessor::~GpuProcessor() = default;

double GpuProcessor::EstimateNoise(const uint8_t* luma, uint32_t width, uint32_t height) {
    // Immerkær's Laplacian-difference mask on a sparse grid. Its response to Gaussian noise of sigma s has
    // standard deviation 6s; edges and texture give large responses, so a low percentile ignores them.
    thread_local std::vector<int> responses;
    responses.clear();
    const int stride = static_cast<int>(width);
    for (uint32_t y = 2; y + 2 < height; y += 6) {
        const uint8_t* row = luma + static_cast<size_t>(y) * width;
        for (uint32_t x = 2; x + 2 < width; x += 6) {
            const uint8_t* p = row + x;
            const int response = p[-stride - 1] - 2 * p[-stride] + p[-stride + 1]
                                 - 2 * p[-1] + 4 * p[0] - 2 * p[1]
                                 + p[stride - 1] - 2 * p[stride] + p[stride + 1];
            responses.push_back(response < 0 ? -response : response);
        }
    }
    if (responses.empty()) return 0;
    // 30th percentile of |N(0, 6s)| is 0.385 * 6s.
    const auto percentile = responses.begin() + static_cast<std::ptrdiff_t>(responses.size() * 3 / 10);
    std::nth_element(responses.begin(), percentile, responses.end());
    return *percentile / (0.385 * 6.0);
}

std::array<float, 256> GpuProcessor::BuildToneCurve(const HitCamProcessing& s) {
    std::array<float, 256> curve{};
    // Brightness is a gamma curve (black and white stay put, nothing clips); contrast an S-curve (smoothstep) or
    // its inverse; shadows and highlights lift or lower one end, most at a third of the range from it.
    const double gamma = std::pow(2.0, -1.5 * s.brightness);
    const double contrast = s.contrast;
    constexpr double kToneAmount = 0.15;
    float previous = 0;
    for (int i = 0; i < 256; ++i) {
        // Video range: 16..235 is black..white; values outside keep their distance to it.
        const double input = (i - 16) / 219.0;
        double x = std::clamp(input, 0.0, 1.0);
        const double outside = input - x;
        x = std::pow(x, gamma);
        if (contrast > 0) {
            x += contrast * (x * x * (3 - 2 * x) - x);
        } else if (contrast < 0) {
            const double inverse = 0.5 - std::sin(std::asin(1 - 2 * x) / 3);
            x += -contrast * (inverse - x);
        }
        const double shadowZone = x * (1 - x) * (1 - x) * 6.75;     // peaks at 1/3
        const double highlightZone = x * x * (1 - x) * 6.75;        // peaks at 2/3
        x += kToneAmount * (s.shadows * shadowZone + s.highlights * highlightZone);
        x = std::clamp(x, 0.0, 1.0) + outside;
        float value = static_cast<float>(std::clamp((16 + 219 * x) / 255.0, 0.0, 1.0));
        // Never inverts: dark stays darker than bright whatever the settings.
        value = std::max(value, previous);
        curve[i] = previous = value;
    }
    return curve;
}

bool GpuProcessor::EnsureDevice() {
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

    if (SUCCEEDED(hr)) hr = device_->CreateComputeShader(g_TemporalLuma, sizeof(g_TemporalLuma), nullptr, &temporalLuma_);
    if (SUCCEEDED(hr)) hr = device_->CreateComputeShader(g_TemporalChroma, sizeof(g_TemporalChroma), nullptr, &temporalChroma_);
    if (SUCCEEDED(hr)) hr = device_->CreateComputeShader(g_AdjustLuma, sizeof(g_AdjustLuma), nullptr, &adjustLuma_);
    if (SUCCEEDED(hr)) hr = device_->CreateComputeShader(g_AdjustChroma, sizeof(g_AdjustChroma), nullptr, &adjustChroma_);
    if (SUCCEEDED(hr)) hr = device_->CreateComputeShader(g_ClarityDown, sizeof(g_ClarityDown), nullptr, &clarityDown_);
    if (SUCCEEDED(hr)) hr = device_->CreateComputeShader(g_ClarityBlurX, sizeof(g_ClarityBlurX), nullptr, &clarityBlurX_);
    if (SUCCEEDED(hr)) hr = device_->CreateComputeShader(g_ClarityBlurY, sizeof(g_ClarityBlurY), nullptr, &clarityBlurY_);
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
    if (SUCCEEDED(hr)) {
        D3D11_BUFFER_DESC desc{};
        desc.ByteWidth = 256 * sizeof(float);
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        hr = device_->CreateBuffer(&desc, nullptr, &toneCurve_);
    }
    if (SUCCEEDED(hr)) {
        D3D11_SHADER_RESOURCE_VIEW_DESC view{};
        view.Format = DXGI_FORMAT_R32_FLOAT;
        view.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
        view.Buffer.NumElements = 256;
        hr = device_->CreateShaderResourceView(toneCurve_.Get(), &view, &toneCurveView_);
    }
    if (FAILED(hr)) {
        ReleaseDevice();
        return false;
    }
    curveUploaded_ = false;
    return true;
}

bool GpuProcessor::CreatePlane(Plane& plane, uint32_t width, uint32_t height, DXGI_FORMAT format, bool writable) {
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | (writable ? D3D11_BIND_UNORDERED_ACCESS : 0);
    if (FAILED(device_->CreateTexture2D(&desc, nullptr, &plane.texture))) return false;
    if (FAILED(device_->CreateShaderResourceView(plane.texture.Get(), nullptr, &plane.srv))) return false;
    return !writable || SUCCEEDED(device_->CreateUnorderedAccessView(plane.texture.Get(), nullptr, &plane.uav));
}

bool GpuProcessor::EnsureFrames(uint32_t width, uint32_t height) {
    if (width == width_ && height == height_ && lumaIn_.texture) return true;
    ReleaseFrames();
    const uint32_t cw = width / 2, ch = height / 2;
    bool ok = CreatePlane(lumaIn_, width, height, DXGI_FORMAT_R8_UNORM, false)
              && CreatePlane(chromaIn_, cw, ch, DXGI_FORMAT_R8G8_UNORM, false)
              && CreatePlane(weight_, width, height, DXGI_FORMAT_R8_UNORM, true)
              && CreatePlane(lumaOut_, width, height, DXGI_FORMAT_R8_UNORM, true)
              && CreatePlane(chromaOut_, cw, ch, DXGI_FORMAT_R8G8_UNORM, true);
    const uint32_t sw = (width + 7) / 8, sh = (height + 7) / 8;
    for (int i = 0; i < 2 && ok; ++i) ok = CreatePlane(clarity_[i], sw, sh, DXGI_FORMAT_R16_FLOAT, true);
    for (int i = 0; i < 2 && ok; ++i) {
        ok = CreatePlane(lumaHistory_[i], width, height, DXGI_FORMAT_R8_UNORM, true)
             && CreatePlane(chromaHistory_[i], cw, ch, DXGI_FORMAT_R8G8_UNORM, true);
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
    smallWidth_ = sw;
    smallHeight_ = sh;
    return true;
}

void GpuProcessor::ReleaseFrames() {
    for (Plane* plane : {&lumaIn_, &chromaIn_, &lumaHistory_[0], &lumaHistory_[1], &chromaHistory_[0], &chromaHistory_[1],
                         &weight_, &lumaOut_, &chromaOut_, &clarity_[0], &clarity_[1]}) {
        *plane = {};
    }
    lumaStaging_.Reset();
    chromaStaging_.Reset();
    width_ = height_ = 0;
    historyValid_ = false;
    cutSamples_.clear();
    noise_ = -1;
}

void GpuProcessor::ReleaseDevice() {
    ReleaseFrames();
    if (context_) {
        context_->ClearState();
        context_->Flush();
    }
    temporalLuma_.Reset();
    temporalChroma_.Reset();
    adjustLuma_.Reset();
    adjustChroma_.Reset();
    clarityDown_.Reset();
    clarityBlurX_.Reset();
    clarityBlurY_.Reset();
    linearClamp_.Reset();
    params_.Reset();
    toneCurveView_.Reset();
    toneCurve_.Reset();
    context_.Reset();
    device_.Reset();
    curveUploaded_ = false;
}

bool GpuProcessor::DetectCut(const uint8_t* luma, uint32_t width, uint32_t height) {
    const size_t count = static_cast<size_t>(width / kCutGrid) * (height / kCutGrid);
    const bool compare = cutSamples_.size() == count;
    double difference = 0;
    cutSamples_.resize(count);
    size_t index = 0;
    for (uint32_t by = 0; by < height / kCutGrid; ++by) {
        for (uint32_t bx = 0; bx < width / kCutGrid; ++bx) {
            const uint8_t* block = luma + static_cast<size_t>(by * kCutGrid + kCutGrid / 2) * width + bx * kCutGrid + kCutGrid / 2;
            int sum = 0;
            for (int y = 0; y < 4; ++y) {
                for (int x = 0; x < 4; ++x) sum += block[static_cast<size_t>(y) * width + x];
            }
            if (compare) difference += std::abs(sum - cutSamples_[index]);
            cutSamples_[index++] = sum;
        }
    }
    return compare && count > 0 && difference / 16.0 / static_cast<double>(count) > kCutThreshold;
}

bool GpuProcessor::Process(uint8_t* nv12, uint32_t width, uint32_t height, const HitCamProcessing& settings) {
    lastMs_ = -1;
    if (width < 2 || height < 2 || (width | height) & 1) return false;
    const auto start = std::chrono::steady_clock::now();
    if (!EnsureDevice()) return false;
    if (!EnsureFrames(width, height)) {
        // Out of video memory is not worth retrying every frame.
        ReleaseDevice();
        return false;
    }
    if (!Run(nv12, width, height, settings)) {
        // Device lost (driver update or reset, GPU removed) or out of memory: pass through, try again later.
        ReleaseDevice();
        return false;
    }
    lastMs_ = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    return true;
}

bool GpuProcessor::Run(uint8_t* nv12, uint32_t width, uint32_t height, const HitCamProcessing& s) {
    const bool temporal = s.temporalStrength > 0;
    const bool clarity = s.clarity > 0;
    const bool adjustLuma = HasToneCurve(s) || s.sharpness > 0 || s.detail > 0 || clarity;
    const bool adjustChroma = s.saturation != 0 || s.vibrance > 0;
    const bool lumaChanges = temporal || adjustLuma;
    const bool chromaChanges = temporal || adjustChroma;
    const uint32_t cw = width / 2, ch = height / 2;
    uint8_t* chroma = nv12 + static_cast<size_t>(width) * height;

    Params params{};
    params.lumaSize[0] = width;
    params.lumaSize[1] = height;
    params.chromaSize[0] = cw;
    params.chromaSize[1] = ch;
    params.saturation = 1.0f + s.saturation;
    // Up to 2x the fine detail; detail smaller than ~1.5 levels is noise.
    params.sharpness = 2.0f * s.sharpness;
    params.sharpenThreshold = 1.5f / 255.0f;
    // Enhancement: the UI's 0..1 at full strength gives the look of the tuned prototype at ~0.5.
    params.detail = s.detail;
    params.clarity = 0.7f * s.clarity;
    params.vibrance = 0.7f * s.vibrance;
    params.smallSize[0] = smallWidth_;
    params.smallSize[1] = smallHeight_;
    if (temporal) {
        const bool cut = DetectCut(nv12, width, height);
        const double sigma = std::min(EstimateNoise(nv12, width, height), 10.0);
        noise_ = noise_ < 0 ? sigma : noise_ * 0.9 + sigma * 0.1;
        const double strength = s.temporalStrength / 100.0;
        // Steady-state noise after the recursive mix is sqrt((1 - w) / (1 + w)) of the input: 0.27 at strength 70.
        const double weight = 0.95 * (1 - (1 - strength) * (1 - strength));
        // Differences within the noise are "no motion", well above it "motion"; more strength tolerates more.
        const double scale = 0.5 + strength;
        const double low = (1.0 + 0.8 * noise_) * scale;
        const double high = low + (3.0 + 1.5 * noise_) * scale;
        params.historyWeight = historyValid_ && !cut ? static_cast<float>(weight) : 0.0f;
        params.motionLow = static_cast<float>(low / 255.0);
        params.motionHigh = static_cast<float>(high / 255.0);
    } else {
        historyValid_ = false;
        cutSamples_.clear();
        noise_ = -1;
    }
    context_->UpdateSubresource(params_.Get(), 0, nullptr, &params, 0, 0);
    if (adjustLuma) {
        const auto curve = BuildToneCurve(s);
        if (!curveUploaded_ || curve != uploadedCurve_) {
            context_->UpdateSubresource(toneCurve_.Get(), 0, nullptr, curve.data(), 0, 0);
            uploadedCurve_ = curve;
            curveUploaded_ = true;
        }
    }

    if (lumaChanges) context_->UpdateSubresource(lumaIn_.texture.Get(), 0, nullptr, nv12, width, 0);
    if (chromaChanges) context_->UpdateSubresource(chromaIn_.texture.Get(), 0, nullptr, chroma, width, 0);

    ID3D11Buffer* constants[] = {params_.Get()};
    context_->CSSetConstantBuffers(0, 1, constants);
    ID3D11ShaderResourceView* noViews[3] = {};
    ID3D11UnorderedAccessView* noTargets[2] = {};
    auto dispatch = [&](ID3D11ComputeShader* shader, std::initializer_list<ID3D11ShaderResourceView*> views,
                        std::initializer_list<ID3D11UnorderedAccessView*> targets, uint32_t w, uint32_t h) {
        context_->CSSetShader(shader, nullptr, 0);
        context_->CSSetShaderResources(0, static_cast<UINT>(views.size()), views.begin());
        context_->CSSetUnorderedAccessViews(0, static_cast<UINT>(targets.size()), targets.begin(), nullptr);
        context_->Dispatch(Groups(w, kGroupWidth), Groups(h, kGroupHeight), 1);
        // Unbound, so the next pass may read what this one wrote.
        context_->CSSetShaderResources(0, 3, noViews);
        context_->CSSetUnorderedAccessViews(0, 2, noTargets, nullptr);
    };

    Plane* luma = &lumaIn_;
    Plane* chromaPlane = &chromaIn_;
    if (temporal) {
        const int next = 1 - history_;
        dispatch(temporalLuma_.Get(), {lumaIn_.srv.Get(), lumaHistory_[history_].srv.Get()},
                 {lumaHistory_[next].uav.Get(), weight_.uav.Get()}, width, height);
        dispatch(temporalChroma_.Get(), {chromaIn_.srv.Get(), chromaHistory_[history_].srv.Get(), weight_.srv.Get()},
                 {chromaHistory_[next].uav.Get()}, cw, ch);
        history_ = next;
        historyValid_ = true;
        luma = &lumaHistory_[next];
        chromaPlane = &chromaHistory_[next];
    }
    if (clarity) {
        dispatch(clarityDown_.Get(), {luma->srv.Get()}, {clarity_[0].uav.Get()}, smallWidth_, smallHeight_);
        dispatch(clarityBlurX_.Get(), {clarity_[0].srv.Get()}, {clarity_[1].uav.Get()}, smallWidth_, smallHeight_);
        dispatch(clarityBlurY_.Get(), {clarity_[1].srv.Get()}, {clarity_[0].uav.Get()}, smallWidth_, smallHeight_);
    }
    if (adjustLuma) {
        ID3D11SamplerState* samplers[] = {linearClamp_.Get()};
        context_->CSSetSamplers(0, 1, samplers);
        dispatch(adjustLuma_.Get(), {luma->srv.Get(), toneCurveView_.Get(), clarity_[0].srv.Get()}, {lumaOut_.uav.Get()}, width, height);
        luma = &lumaOut_;
    }
    if (adjustChroma) {
        dispatch(adjustChroma_.Get(), {chromaPlane->srv.Get()}, {chromaOut_.uav.Get()}, cw, ch);
        chromaPlane = &chromaOut_;
    }
    context_->CSSetShader(nullptr, nullptr, 0);

    if (lumaChanges) context_->CopyResource(lumaStaging_.Get(), luma->texture.Get());
    if (chromaChanges) context_->CopyResource(chromaStaging_.Get(), chromaPlane->texture.Get());

    // Both planes are mapped before anything is copied: a failure leaves the frame as it was.
    D3D11_MAPPED_SUBRESOURCE lumaMapped{}, chromaMapped{};
    bool ok = !lumaChanges || SUCCEEDED(context_->Map(lumaStaging_.Get(), 0, D3D11_MAP_READ, 0, &lumaMapped));
    const bool lumaMappedOk = ok && lumaChanges;
    ok = ok && (!chromaChanges || SUCCEEDED(context_->Map(chromaStaging_.Get(), 0, D3D11_MAP_READ, 0, &chromaMapped)));
    const bool chromaMappedOk = ok && chromaChanges;
    auto copyRows = [&](const D3D11_MAPPED_SUBRESOURCE& mapped, uint8_t* target, uint32_t rows) {
        for (uint32_t y = 0; y < rows; ++y) {
            std::memcpy(target + static_cast<size_t>(y) * width, static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch, width);
        }
    };
    if (ok && lumaChanges) copyRows(lumaMapped, nv12, height);
    if (ok && chromaChanges) copyRows(chromaMapped, chroma, ch);
    if (lumaMappedOk) context_->Unmap(lumaStaging_.Get(), 0);
    if (chromaMappedOk) context_->Unmap(chromaStaging_.Get(), 0);
    return ok;
}

}  // namespace hitcam
