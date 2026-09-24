#pragma once

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <ks.h>
#include <ksproxy.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <atomic>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

#include "Shared.h"

namespace hitcam {

class MediaSource;

// Reads the newest frame published by HitCam.exe and fits it into the negotiated output size.
class FrameReader {
public:
    ~FrameReader();
    // Fills a contiguous NV12 buffer (pitch == width). While the writer is busy (or a copy came out torn) it
    // repeats the last complete frame; "no signal" only once there is no frame or it is older than kStaleAfterMs.
    void Compose(uint8_t* destination, uint32_t width, uint32_t height);

private:
    bool EnsureMapped();
    // Copies a newer complete frame out of shared memory into frame_, if there is one.
    void Refresh();
    void Fit(uint8_t* destination, uint32_t width, uint32_t height);

    SharedHeader* header_ = nullptr;
    HANDLE mapping_ = nullptr;
    ULONGLONG nextMapAttemptMs_ = 0;

    // Last complete frame (pitch == width) and a scratch buffer the next copy goes to until it is verified.
    std::vector<uint8_t> frame_, scratch_;
    uint32_t frameWidth_ = 0, frameHeight_ = 0;
    ULONGLONG frameUpdatedMs_ = 0;
    LONG64 frameSequence_ = -1;

    // Source column of every output column, for the current scale.
    std::vector<uint32_t> columns_;
    uint32_t columnsSourceWidth_ = 0, columnsOutWidth_ = 0;
};

class MediaStream : public Microsoft::WRL::RuntimeClass<
                        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                        Microsoft::WRL::ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>,
                        IKsControl> {
public:
    HRESULT RuntimeClassInitialize(MediaSource* source, IMFStreamDescriptor* descriptor);
    ~MediaStream() override;

    HRESULT Start(IMFMediaType* type);
    HRESULT Stop();
    // Also detaches the stream from its source; the source calls it from Shutdown and from its destructor.
    void Shutdown();

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue) override;

    // IMFMediaStream
    IFACEMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
    IFACEMETHODIMP RequestSample(IUnknown* token) override;

    // IMFMediaStream2
    IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

    // IKsControl
    IFACEMETHODIMP KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
    IFACEMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
    IFACEMETHODIMP KsEvent(PKSEVENT event, ULONG eventLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;

private:
    // The sink asks a frame or two ahead; more than this means nobody is consuming, so the oldest are dropped.
    static constexpr size_t kMaxPendingRequests = 8;

    void Run();
    void RunLoop();
    HRESULT DeliverSample(IUnknown* token);
    // Requires workerLock_.
    void StopWorker();

    // Lock order: workerLock_, then lock_. The worker thread only takes lock_.
    std::mutex workerLock_;
    std::mutex lock_;
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue_;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> descriptor_;
    // Not a reference: the source owns the stream, and a counted back reference would keep both (and the worker)
    // alive forever if the source is released without Shutdown. Cleared by Shutdown, under lock_.
    MediaSource* parent_ = nullptr;
    MF_STREAM_STATE state_ = MF_STREAM_STATE_STOPPED;
    bool shutdown_ = false;

    std::deque<Microsoft::WRL::ComPtr<IUnknown>> tokens_;
    std::thread worker_;
    std::atomic<bool> running_{false};
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    LONGLONG frameDuration_ = 0;
    FrameReader reader_;
};

class MediaSource
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          Microsoft::WRL::ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
          IMFGetService,
          IKsControl> {
public:
    // `activationAttributes` come from the frame server through the Activator and become the source attributes.
    HRESULT RuntimeClassInitialize(IMFAttributes* activationAttributes);
    ~MediaSource() override;

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue) override;

    // IMFMediaSource
    IFACEMETHODIMP GetCharacteristics(DWORD* characteristics) override;
    IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) override;
    IFACEMETHODIMP Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
    IFACEMETHODIMP Stop() override;
    IFACEMETHODIMP Pause() override;
    IFACEMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    IFACEMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
    IFACEMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override;
    IFACEMETHODIMP SetD3DManager(IUnknown* manager) override;

    // IMFGetService
    IFACEMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;

    // IKsControl
    IFACEMETHODIMP KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
    IFACEMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
    IFACEMETHODIMP KsEvent(PKSEVENT event, ULONG eventLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;

private:
    std::mutex lock_;
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue_;
    Microsoft::WRL::ComPtr<IMFPresentationDescriptor> descriptor_;
    Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
    Microsoft::WRL::ComPtr<MediaStream> stream_;
    bool started_ = false;
    bool shutdown_ = false;
};

}  // namespace hitcam
