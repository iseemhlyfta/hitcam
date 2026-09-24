#include "MediaSource.h"

#include <ksmedia.h>

#include <algorithm>
#include <chrono>
#include <cstring>

#include "ErrorGuard.h"

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

// Seqlock read: spin briefly (the writer copies a frame in about a millisecond), then give the CPU away.
constexpr int kSpinAttempts = 64;
constexpr int kReadAttempts = 256;

size_t Nv12Size(uint32_t width, uint32_t height) { return static_cast<size_t>(width) * height * 3 / 2; }

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
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_SAMPLE_SIZE, static_cast<UINT32>(Nv12Size(width, height)));
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_DEFAULT_STRIDE, width);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_AVG_BITRATE, width * height * 12 * kFps);
    if (SUCCEEDED(hr)) *result = type.Detach();
    return hr;
}

// Only the types the source declared: a sample size computed from anything else could be absurd.
HRESULT CheckMediaType(IMFMediaType* type, UINT32* width, UINT32* height) {
    if (!type) return MF_E_INVALIDMEDIATYPE;
    GUID major{}, subtype{};
    if (FAILED(type->GetGUID(MF_MT_MAJOR_TYPE, &major)) || major != MFMediaType_Video) return MF_E_INVALIDMEDIATYPE;
    if (FAILED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) || subtype != MFVideoFormat_NV12) return MF_E_INVALIDMEDIATYPE;
    UINT32 w = 0, h = 0;
    if (FAILED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &w, &h))) return MF_E_INVALIDMEDIATYPE;
    UINT32 numerator = 0, denominator = 0;
    if (SUCCEEDED(MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &numerator, &denominator))
        && (denominator == 0 || static_cast<UINT64>(numerator) != static_cast<UINT64>(kFps) * denominator)) {
        return MF_E_INVALIDMEDIATYPE;
    }
    for (const auto& size : kSizes) {
        if (size.width == w && size.height == h) {
            *width = w;
            *height = h;
            return S_OK;
        }
    }
    return MF_E_INVALIDMEDIATYPE;
}

void Fill(uint8_t* destination, uint32_t width, uint32_t height, uint8_t luma) {
    std::memset(destination, luma, static_cast<size_t>(width) * height);
    std::memset(destination + static_cast<size_t>(width) * height, kNeutralChroma, static_cast<size_t>(width) * height / 2);
}

bool IsValidFrameSize(uint32_t width, uint32_t height) {
    return width >= 2 && height >= 2 && width <= kMaxSide && height <= kMaxSide && (width % 2) == 0 && (height % 2) == 0;
}

}  // namespace

// MARK: FrameReader

FrameReader::~FrameReader() { UnmapSharedFrames(header_, mapping_); }

bool FrameReader::EnsureMapped() {
    if (header_) return true;
    const ULONGLONG now = GetTickCount64();
    if (now < nextMapAttemptMs_) return false;
    nextMapAttemptMs_ = now + 1000;
    header_ = MapSharedFrames(&mapping_, SharedAccess::Read);
    return header_ != nullptr;
}

void FrameReader::Refresh() {
    for (int attempt = 0; attempt < kReadAttempts; ++attempt) {
        const LONG64 before = header_->sequence;
        if (before & 1) {
            if (attempt < kSpinAttempts) YieldProcessor();
            else SwitchToThread();
            continue;
        }
        // Nothing new since the last copy: keep it (and skip copying the same frame again).
        if (before == frameSequence_) return;
        MemoryBarrier();
        // Each field is read exactly once; only these copies are checked and used.
        const uint32_t sourceWidth = header_->width;
        const uint32_t sourceHeight = header_->height;
        const ULONGLONG updatedMs = static_cast<ULONGLONG>(header_->updatedMs);

        const bool valid = IsValidFrameSize(sourceWidth, sourceHeight);
        if (valid) {
            const size_t size = Nv12Size(sourceWidth, sourceHeight);
            scratch_.resize(size);
            std::memcpy(scratch_.data(), FrameData(header_), size);
        }
        MemoryBarrier();
        if (header_->sequence != before) continue;  // torn: the writer started the next frame meanwhile

        frameSequence_ = before;
        if (!valid) {
            // Cleared by the app (phone disconnected) or garbage: no signal.
            frameWidth_ = frameHeight_ = 0;
            return;
        }
        frame_.swap(scratch_);
        frameWidth_ = sourceWidth;
        frameHeight_ = sourceHeight;
        frameUpdatedMs_ = updatedMs;
        return;
    }
    // The writer kept us out; the last complete frame is repeated, which is what a late frame looks like anyway.
}

void FrameReader::Compose(uint8_t* destination, uint32_t width, uint32_t height) {
    if (EnsureMapped()) {
        if (frame_.capacity() < kMaxFrameBytes) {
            // Once, so a resolution change never reallocates in the middle of streaming.
            frame_.reserve(kMaxFrameBytes);
            scratch_.reserve(kMaxFrameBytes);
        }
        Refresh();
    }
    if (frameWidth_ != 0 && GetTickCount64() - frameUpdatedMs_ < kStaleAfterMs) {
        Fit(destination, width, height);
        return;
    }
    Fill(destination, width, height, kNoSignalLuma);
}

// Nearest-neighbour scale of NV12 into the destination, keeping the aspect ratio (letterbox/pillarbox).
void FrameReader::Fit(uint8_t* destination, uint32_t width, uint32_t height) {
    const uint8_t* source = frame_.data();
    const uint32_t sourceWidth = frameWidth_;
    const uint32_t sourceHeight = frameHeight_;
    if (sourceWidth == width && sourceHeight == height) {
        std::memcpy(destination, source, Nv12Size(width, height));
        return;
    }

    const double scale = std::min(static_cast<double>(width) / sourceWidth, static_cast<double>(height) / sourceHeight);
    const uint32_t outWidth = std::clamp<uint32_t>(static_cast<uint32_t>(sourceWidth * scale) & ~1u, 2, width);
    const uint32_t outHeight = std::clamp<uint32_t>(static_cast<uint32_t>(sourceHeight * scale) & ~1u, 2, height);
    const uint32_t left = ((width - outWidth) / 2) & ~1u;
    const uint32_t top = ((height - outHeight) / 2) & ~1u;
    if (outWidth != width || outHeight != height) Fill(destination, width, height, kBlackLuma);

    if (columnsSourceWidth_ != sourceWidth || columnsOutWidth_ != outWidth) {
        columns_.resize(outWidth);
        for (uint32_t x = 0; x < outWidth; ++x) columns_[x] = static_cast<uint32_t>(static_cast<uint64_t>(x) * sourceWidth / outWidth);
        columnsSourceWidth_ = sourceWidth;
        columnsOutWidth_ = outWidth;
    }
    const uint32_t* columns = columns_.data();

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

// MARK: MediaStream

HRESULT MediaStream::RuntimeClassInitialize(MediaSource* source, IMFStreamDescriptor* descriptor) {
    parent_ = source;
    descriptor_ = descriptor;
    return MFCreateEventQueue(&queue_);
}

MediaStream::~MediaStream() {
    // Normally already stopped by Shutdown; a joinable std::thread in a destructor would be std::terminate.
    std::lock_guard worker(workerLock_);
    StopWorker();
}

HRESULT MediaStream::Start(IMFMediaType* type) {
    return Guarded([&]() -> HRESULT {
        UINT32 width = 0, height = 0;
        HRESULT hr = CheckMediaType(type, &width, &height);
        if (FAILED(hr)) return hr;

        std::lock_guard worker(workerLock_);
        StopWorker();
        {
            std::lock_guard guard(lock_);
            if (shutdown_) return MF_E_SHUTDOWN;
            width_ = width;
            height_ = height;
            frameDuration_ = 10'000'000LL / kFps;
            tokens_.clear();
            state_ = MF_STREAM_STATE_RUNNING;
            running_ = true;
        }
        try {
            worker_ = std::thread([this] { Run(); });
        } catch (...) {
            std::lock_guard guard(lock_);
            running_ = false;
            state_ = MF_STREAM_STATE_STOPPED;
            throw;
        }

        PROPVARIANT time;
        PropVariantInit(&time);
        time.vt = VT_I8;
        time.hVal.QuadPart = MFGetSystemTime();
        return queue_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, &time);
    });
}

HRESULT MediaStream::Stop() {
    return Guarded([&]() -> HRESULT {
        std::lock_guard worker(workerLock_);
        StopWorker();
        {
            std::lock_guard guard(lock_);
            if (shutdown_) return MF_E_SHUTDOWN;
            state_ = MF_STREAM_STATE_STOPPED;
            tokens_.clear();
        }
        return queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    });
}

void MediaStream::Shutdown() {
    // Held throughout, so a concurrent Start cannot slip a new worker in before shutdown_ is set.
    std::lock_guard worker(workerLock_);
    StopWorker();
    std::lock_guard guard(lock_);
    if (shutdown_) return;
    shutdown_ = true;
    tokens_.clear();
    queue_->Shutdown();
    parent_ = nullptr;
}

void MediaStream::StopWorker() {
    running_ = false;
    if (!worker_.joinable()) return;
    // A thread cannot join itself (std::terminate). The worker holds no references, so this is only a guard.
    if (worker_.get_id() == std::this_thread::get_id()) worker_.detach();
    else worker_.join();
}

void MediaStream::Run() {
    try {
        RunLoop();
    } catch (...) {
        // Out of memory: tell the pipeline instead of taking the whole frame server process down.
        running_ = false;
        queue_->QueueEventParamVar(MEError, GUID_NULL, E_OUTOFMEMORY, nullptr);
    }
}

// Paces delivery to the negotiated frame rate: at most one sample per frame interval, only when requested.
void MediaStream::RunLoop() {
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
        if (!requested) continue;
        const HRESULT hr = DeliverSample(token.Get());
        // A request that cannot be answered is reported, not silently dropped: the sink would wait for it.
        if (FAILED(hr) && hr != MF_E_SHUTDOWN) queue_->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr);
    }
}

HRESULT MediaStream::DeliverSample(IUnknown* token) {
    const size_t size = Nv12Size(width_, height_);
    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = MFCreateMemoryBuffer(static_cast<DWORD>(size), &buffer);
    if (FAILED(hr)) return hr;

    BYTE* data = nullptr;
    DWORD capacity = 0;
    hr = buffer->Lock(&data, &capacity, nullptr);
    if (FAILED(hr)) return hr;
    if (capacity < size) {
        buffer->Unlock();
        return E_UNEXPECTED;
    }
    reader_.Compose(data, width_, height_);
    buffer->Unlock();
    hr = buffer->SetCurrentLength(static_cast<DWORD>(size));

    ComPtr<IMFSample> sample;
    if (SUCCEEDED(hr)) hr = MFCreateSample(&sample);
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
    *source = nullptr;
    std::lock_guard guard(lock_);
    // parent_ is cleared under this lock before the source goes away (Shutdown or its destructor). Like
    // Microsoft's SimpleMediaSource, this cannot help a caller that releases the last source reference on
    // another thread at this very moment; such a caller has no source to ask about anyway.
    if (shutdown_ || !parent_) return MF_E_SHUTDOWN;
    return parent_->QueryInterface(IID_PPV_ARGS(source));
}

IFACEMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor) {
    if (!descriptor) return E_POINTER;
    std::lock_guard guard(lock_);
    if (shutdown_) return MF_E_SHUTDOWN;
    return descriptor_.CopyTo(descriptor);
}

IFACEMETHODIMP MediaStream::RequestSample(IUnknown* token) {
    return Guarded([&]() -> HRESULT {
        std::lock_guard guard(lock_);
        if (shutdown_) return MF_E_SHUTDOWN;
        // SetStreamState(RUNNING) alone does not start the worker: without it nobody would ever answer.
        if (state_ != MF_STREAM_STATE_RUNNING || !running_) return MF_E_MEDIA_SOURCE_WRONGSTATE;
        if (tokens_.size() >= kMaxPendingRequests) tokens_.pop_front();
        tokens_.emplace_back(token);
        return S_OK;
    });
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

MediaSource::~MediaSource() {
    // Released without Shutdown: stop the worker and detach the stream, which may outlive us.
    if (stream_) stream_->Shutdown();
    if (queue_) queue_->Shutdown();
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

    return Guarded([&]() -> HRESULT {
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
            // Rejected before any event is queued, so the pipeline sees a clean failure.
            UINT32 width = 0, height = 0;
            if (SUCCEEDED(hr)) hr = CheckMediaType(type.Get(), &width, &height);
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
    });
}

IFACEMETHODIMP MediaSource::Stop() {
    return Guarded([&]() -> HRESULT {
        std::lock_guard guard(lock_);
        if (shutdown_) return MF_E_SHUTDOWN;
        HRESULT hr = stream_->Stop();
        if (FAILED(hr)) return hr;
        return queue_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
    });
}

IFACEMETHODIMP MediaSource::Pause() {
    // A live camera cannot pause (MFMEDIASOURCE_CAN_PAUSE is not advertised).
    return MF_E_INVALID_STATE_TRANSITION;
}

IFACEMETHODIMP MediaSource::Shutdown() {
    return Guarded([&]() -> HRESULT {
        std::lock_guard guard(lock_);
        if (shutdown_) return MF_E_SHUTDOWN;
        shutdown_ = true;
        if (stream_) stream_->Shutdown();
        queue_->Shutdown();
        return S_OK;
    });
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
