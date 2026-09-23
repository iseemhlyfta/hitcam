#include "MediaSource.h"

#include <ksmedia.h>

#include <algorithm>
#include <chrono>
#include <cstring>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::MakeAndInitialize;

namespace hitcam {

namespace {

constexpr UINT32 kFps = 30;
constexpr struct {
    UINT32 width;
    UINT32 height;
} kSizes[] = {{1920, 1080}, {1280, 720}, {640, 360}};

// Dark gray instead of pure black, so "no signal" is distinguishable from a covered lens.
constexpr uint8_t kNoSignalLuma = 32;
constexpr uint8_t kBlackLuma = 16;
constexpr uint8_t kNeutralChroma = 128;

HRESULT CreateVideoType(UINT32 width, UINT32 height, IMFMediaType** result) {
    ComPtr<IMFMediaType> type;
    HRESULT hr = MFCreateMediaType(&type);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, kFps, 1);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_SAMPLE_SIZE, width * height * 3 / 2);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_DEFAULT_STRIDE, width);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_AVG_BITRATE, width * height * 12 * kFps);
    if (SUCCEEDED(hr)) *result = type.Detach();
    return hr;
}

void Fill(uint8_t* destination, uint32_t width, uint32_t height, uint8_t luma) {
    std::memset(destination, luma, static_cast<size_t>(width) * height);
    std::memset(destination + static_cast<size_t>(width) * height, kNeutralChroma, static_cast<size_t>(width) * height / 2);
}

// Nearest-neighbour scale of NV12 into the destination, keeping the aspect ratio (letterbox/pillarbox).
void FitNv12(const uint8_t* source, uint32_t sourceWidth, uint32_t sourceHeight,
             uint8_t* destination, uint32_t width, uint32_t height) {
    if (sourceWidth == width && sourceHeight == height) {
        std::memcpy(destination, source, static_cast<size_t>(width) * height * 3 / 2);
        return;
    }

    const double scale = std::min(static_cast<double>(width) / sourceWidth, static_cast<double>(height) / sourceHeight);
    const uint32_t outWidth = std::max<uint32_t>(2, static_cast<uint32_t>(sourceWidth * scale) & ~1u);
    const uint32_t outHeight = std::max<uint32_t>(2, static_cast<uint32_t>(sourceHeight * scale) & ~1u);
    const uint32_t left = ((width - outWidth) / 2) & ~1u;
    const uint32_t top = ((height - outHeight) / 2) & ~1u;
    if (outWidth != width || outHeight != height) Fill(destination, width, height, kBlackLuma);

    std::vector<uint32_t> columns(outWidth);
    for (uint32_t x = 0; x < outWidth; ++x) columns[x] = static_cast<uint32_t>(static_cast<uint64_t>(x) * sourceWidth / outWidth);

    for (uint32_t y = 0; y < outHeight; ++y) {
        const uint8_t* sourceRow = source + static_cast<size_t>(static_cast<uint64_t>(y) * sourceHeight / outHeight) * sourceWidth;
        uint8_t* row = destination + static_cast<size_t>(top + y) * width + left;
        for (uint32_t x = 0; x < outWidth; ++x) row[x] = sourceRow[columns[x]];
    }

    const uint8_t* sourceChroma = source + static_cast<size_t>(sourceWidth) * sourceHeight;
    uint8_t* chroma = destination + static_cast<size_t>(width) * height;
    for (uint32_t y = 0; y < outHeight / 2; ++y) {
        const uint32_t sourceY = static_cast<uint32_t>(static_cast<uint64_t>(y) * (sourceHeight / 2) / (outHeight / 2));
        const uint8_t* sourceRow = sourceChroma + static_cast<size_t>(sourceY) * sourceWidth;
        uint8_t* row = chroma + static_cast<size_t>(top / 2 + y) * width + left;
        for (uint32_t x = 0; x < outWidth / 2; ++x) {
            const uint32_t sourceX = (columns[x * 2] / 2) * 2;
            row[x * 2] = sourceRow[sourceX];
            row[x * 2 + 1] = sourceRow[sourceX + 1];
        }
    }
}

}  // namespace

// MARK: FrameReader

FrameReader::~FrameReader() { UnmapSharedFrames(header_, mapping_); }

bool FrameReader::EnsureMapped() {
    if (header_) return true;
    const ULONGLONG now = GetTickCount64();
    if (now < nextMapAttemptMs_) return false;
    nextMapAttemptMs_ = now + 1000;
    header_ = MapSharedFrames(&mapping_);
    return header_ != nullptr;
}

void FrameReader::Compose(uint8_t* destination, uint32_t width, uint32_t height) {
    if (EnsureMapped()) {
        for (int attempt = 0; attempt < 3; ++attempt) {
            const LONG64 before = header_->sequence;
            if (before & 1) {
                Sleep(1);
                continue;
            }
            MemoryBarrier();
            const uint32_t sourceWidth = header_->width;
            const uint32_t sourceHeight = header_->height;
            const ULONGLONG age = GetTickCount64() - static_cast<ULONGLONG>(header_->updatedMs);
            const bool valid = sourceWidth >= 2 && sourceHeight >= 2 && sourceWidth <= kMaxSide && sourceHeight <= kMaxSide
                               && (sourceWidth % 2) == 0 && (sourceHeight % 2) == 0 && age < kStaleAfterMs;
            if (!valid) break;

            FitNv12(FrameData(header_), sourceWidth, sourceHeight, destination, width, height);
            MemoryBarrier();
            if (header_->sequence == before) return;
        }
    }
    Fill(destination, width, height, kNoSignalLuma);
}

// MARK: MediaStream

HRESULT MediaStream::RuntimeClassInitialize(MediaSource* source, IMFStreamDescriptor* descriptor) {
    HRESULT hr = MFCreateEventQueue(&queue_);
    if (SUCCEEDED(hr)) hr = source->QueryInterface(IID_PPV_ARGS(&source_));
    descriptor_ = descriptor;
    return hr;
}

HRESULT MediaStream::Start(IMFMediaType* type) {
    StopWorker();

    UINT32 width = 0, height = 0, numerator = kFps, denominator = 1;
    HRESULT hr = MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height);
    if (FAILED(hr)) return hr;
    MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &numerator, &denominator);
    if (numerator == 0 || denominator == 0) {
        numerator = kFps;
        denominator = 1;
    }

    {
        std::lock_guard guard(lock_);
        if (shutdown_) return MF_E_SHUTDOWN;
        width_ = width;
        height_ = height;
        frameDuration_ = 10'000'000LL * denominator / numerator;
        tokens_.clear();
        state_ = MF_STREAM_STATE_RUNNING;
    }

    running_ = true;
    worker_ = std::thread([this] { Run(); });

    PROPVARIANT time;
    PropVariantInit(&time);
    time.vt = VT_I8;
    time.hVal.QuadPart = MFGetSystemTime();
    return queue_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, &time);
}

HRESULT MediaStream::Stop() {
    StopWorker();
    {
        std::lock_guard guard(lock_);
        if (shutdown_) return MF_E_SHUTDOWN;
        state_ = MF_STREAM_STATE_STOPPED;
        tokens_.clear();
    }
    return queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
}

void MediaStream::Shutdown() {
    StopWorker();
    std::lock_guard guard(lock_);
    if (shutdown_) return;
    shutdown_ = true;
    tokens_.clear();
    queue_->Shutdown();
    // Breaks the source <-> stream reference cycle.
    source_.Reset();
}

void MediaStream::StopWorker() {
    running_ = false;
    if (worker_.joinable()) worker_.join();
}

// Paces delivery to the negotiated frame rate: at most one sample per frame interval, only when requested.
void MediaStream::Run() {
    using namespace std::chrono;
    const auto interval = duration_cast<steady_clock::duration>(nanoseconds(frameDuration_ * 100));
    auto next = steady_clock::now();
    while (running_) {
        next += interval;
        std::this_thread::sleep_until(next);
        const auto now = steady_clock::now();
        if (now - next > interval * 3) next = now;

        ComPtr<IUnknown> token;
        bool requested = false;
        {
            std::lock_guard guard(lock_);
            if (!tokens_.empty()) {
                token = std::move(tokens_.front());
                tokens_.pop_front();
                requested = true;
            }
        }
        if (requested) DeliverSample(token.Get());
    }
}

HRESULT MediaStream::DeliverSample(IUnknown* token) {
    const DWORD size = width_ * height_ * 3 / 2;
    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = MFCreateMemoryBuffer(size, &buffer);
    if (FAILED(hr)) return hr;

    BYTE* data = nullptr;
    hr = buffer->Lock(&data, nullptr, nullptr);
    if (FAILED(hr)) return hr;
    reader_.Compose(data, width_, height_);
    buffer->Unlock();
    buffer->SetCurrentLength(size);

    ComPtr<IMFSample> sample;
    hr = MFCreateSample(&sample);
    if (SUCCEEDED(hr)) hr = sample->AddBuffer(buffer.Get());
    if (SUCCEEDED(hr)) hr = sample->SetSampleTime(MFGetSystemTime());
    if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(frameDuration_);
    if (SUCCEEDED(hr) && token) hr = sample->SetUnknown(MFSampleExtension_Token, token);
    if (SUCCEEDED(hr)) hr = queue_->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
    return hr;
}

IFACEMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event) {
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard guard(lock_);
        if (shutdown_) return MF_E_SHUTDOWN;
        queue = queue_;
    }
    return queue->GetEvent(flags, event);
}

IFACEMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) {
    std::lock_guard guard(lock_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->BeginGetEvent(callback, state);
}

IFACEMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) {
    std::lock_guard guard(lock_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->EndGetEvent(result, event);
}

IFACEMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue) {
    std::lock_guard guard(lock_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->QueueEventParamVar(type, extendedType, status, eventValue);
}

IFACEMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source) {
    if (!source) return E_POINTER;
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    return source_.CopyTo(source);
}

IFACEMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor) {
    if (!descriptor) return E_POINTER;
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    return descriptor_.CopyTo(descriptor);
}

IFACEMETHODIMP MediaStream::RequestSample(IUnknown* token) {
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    if (state_ != MF_STREAM_STATE_RUNNING) return MF_E_MEDIA_SOURCE_WRONGSTATE;
    tokens_.emplace_back(token);
    return S_OK;
}

IFACEMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE state) {
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    // Start/Stop go through the source; here the state only gates RequestSample.
    state_ = state;
    if (state != MF_STREAM_STATE_RUNNING) tokens_.clear();
    return S_OK;
}

IFACEMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* state) {
    if (!state) return E_POINTER;
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    *state = state_;
    return S_OK;
}

IFACEMETHODIMP MediaStream::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) {
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaStream::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) {
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaStream::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) {
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

// MARK: MediaSource

HRESULT MediaSource::RuntimeClassInitialize(IMFAttributes* activationAttributes) {
    HRESULT hr = MFCreateEventQueue(&queue_);
    if (SUCCEEDED(hr)) hr = MFCreateAttributes(&attributes_, 4);
    if (SUCCEEDED(hr) && activationAttributes) hr = activationAttributes->CopyAllItems(attributes_.Get());

    ComPtr<IMFMediaType> types[ARRAYSIZE(kSizes)];
    IMFMediaType* rawTypes[ARRAYSIZE(kSizes)] = {};
    for (size_t i = 0; SUCCEEDED(hr) && i < ARRAYSIZE(kSizes); ++i) {
        hr = CreateVideoType(kSizes[i].width, kSizes[i].height, &types[i]);
        rawTypes[i] = types[i].Get();
    }

    ComPtr<IMFStreamDescriptor> stream;
    if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(0, ARRAYSIZE(rawTypes), rawTypes, &stream);
    ComPtr<IMFMediaTypeHandler> handler;
    if (SUCCEEDED(hr)) hr = stream->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(rawTypes[0]);
    if (SUCCEEDED(hr)) hr = stream->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE);
    if (SUCCEEDED(hr)) hr = stream->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
    if (SUCCEEDED(hr)) hr = stream->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
    if (SUCCEEDED(hr)) hr = stream->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);
    if (SUCCEEDED(hr)) hr = MFCreatePresentationDescriptor(1, stream.GetAddressOf(), &descriptor_);
    if (SUCCEEDED(hr)) hr = descriptor_->SelectStream(0);
    if (SUCCEEDED(hr)) hr = MakeAndInitialize<MediaStream>(&stream_, this, stream.Get());
    return hr;
}

IFACEMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event) {
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard guard(lock_);
        if (shutdown_) return MF_E_SHUTDOWN;
        queue = queue_;
    }
    return queue->GetEvent(flags, event);
}

IFACEMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) {
    std::lock_guard guard(lock_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->BeginGetEvent(callback, state);
}

IFACEMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) {
    std::lock_guard guard(lock_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->EndGetEvent(result, event);
}

IFACEMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue) {
    std::lock_guard guard(lock_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->QueueEventParamVar(type, extendedType, status, eventValue);
}

IFACEMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics) {
    if (!characteristics) return E_POINTER;
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

IFACEMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) {
    if (!descriptor) return E_POINTER;
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    return descriptor_->Clone(descriptor);
}

IFACEMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) {
    if (!descriptor || !startPosition) return E_INVALIDARG;
    if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;

    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;

    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> stream;
    HRESULT hr = descriptor->GetStreamDescriptorByIndex(0, &selected, &stream);
    if (FAILED(hr)) return hr;

    if (selected) {
        ComPtr<IMFMediaTypeHandler> handler;
        ComPtr<IMFMediaType> type;
        hr = stream->GetMediaTypeHandler(&handler);
        if (SUCCEEDED(hr)) hr = handler->GetCurrentMediaType(&type);
        if (SUCCEEDED(hr)) {
            ComPtr<IUnknown> unknown;
            stream_.As(&unknown);
            hr = queue_->QueueEventParamUnk(started_ ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK, unknown.Get());
        }
        if (SUCCEEDED(hr)) hr = stream_->Start(type.Get());
    } else {
        hr = stream_->Stop();
    }
    if (FAILED(hr)) return hr;

    started_ = true;
    PROPVARIANT time;
    PropVariantInit(&time);
    time.vt = VT_I8;
    time.hVal.QuadPart = MFGetSystemTime();
    return queue_->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
}

IFACEMETHODIMP MediaSource::Stop() {
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    HRESULT hr = stream_->Stop();
    if (FAILED(hr)) return hr;
    return queue_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

IFACEMETHODIMP MediaSource::Pause() {
    // A live camera cannot pause (MFMEDIASOURCE_CAN_PAUSE is not advertised).
    return MF_E_INVALID_STATE_TRANSITION;
}

IFACEMETHODIMP MediaSource::Shutdown() {
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    shutdown_ = true;
    if (stream_) stream_->Shutdown();
    queue_->Shutdown();
    return S_OK;
}

IFACEMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes) {
    if (!attributes) return E_POINTER;
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    return attributes_.CopyTo(attributes);
}

IFACEMETHODIMP MediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) {
    if (!attributes) return E_POINTER;
    if (streamId != 0) return MF_E_INVALIDSTREAMNUMBER;
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> stream;
    HRESULT hr = descriptor_->GetStreamDescriptorByIndex(0, &selected, &stream);
    if (FAILED(hr)) return hr;
    return stream->QueryInterface(IID_PPV_ARGS(attributes));
}

IFACEMETHODIMP MediaSource::SetD3DManager(IUnknown*) {
    // Samples are system memory; the frame server uploads them to the GPU itself if needed.
    return S_OK;
}

IFACEMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* object) {
    if (object) *object = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

IFACEMETHODIMP MediaSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) {
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) {
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) {
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

}  // namespace hitcam
