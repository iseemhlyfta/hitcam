#include "DShowOutput.h"

#include <softcamcore/SenderAPI.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>

namespace hitcam::dshow {
namespace {

// Matches the Media Foundation camera's "no signal" gray (luma 32).
constexpr uint8_t kNoSignalGray = 19;
// After this long without frames the camera shows "no signal", as the Media Foundation one does.
constexpr auto kStaleAfter = std::chrono::milliseconds(1500);
// How often the "no signal" picture is repeated.
constexpr auto kIdleInterval = std::chrono::milliseconds(200);

uint8_t Clamp(int value) { return static_cast<uint8_t>(value < 0 ? 0 : value > 255 ? 255 : value); }

class Output {
public:
    HRESULT Start() {
        std::lock_guard guard(lock_);
        if (camera_) return S_OK;
        camera_ = softcam::sender::CreateCamera(kWidth, kHeight, 0.0f);
        if (!camera_) return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
        bgr_.assign(static_cast<size_t>(kWidth) * kHeight * 3, 0);
        running_ = true;
        hasFrame_ = false;
        lastFrame_ = {};
        worker_ = std::thread([this] { Run(); });
        return S_OK;
    }

    void Stop() {
        std::thread worker;
        {
            std::lock_guard guard(lock_);
            if (!camera_) return;
            running_ = false;
            worker = std::move(worker_);
        }
        wake_.notify_all();
        if (worker.joinable()) worker.join();
        std::lock_guard guard(lock_);
        softcam::sender::DeleteCamera(camera_);
        camera_ = nullptr;
    }

    bool IsActive() {
        std::lock_guard guard(lock_);
        return camera_ != nullptr;
    }

    void Submit(const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height) {
        width &= ~1u;
        height &= ~1u;
        if (width < 2 || height < 2) return;
        {
            std::lock_guard guard(lock_);
            if (!camera_) return;
            // Packed NV12; a frame not sent yet is simply replaced by the newer one.
            pending_.resize(static_cast<size_t>(width) * height * 3 / 2);
            uint8_t* out = pending_.data();
            for (uint32_t y = 0; y < height; ++y) std::memcpy(out + static_cast<size_t>(y) * width, luma + static_cast<size_t>(y) * pitch, width);
            out += static_cast<size_t>(width) * height;
            for (uint32_t y = 0; y < height / 2; ++y) std::memcpy(out + static_cast<size_t>(y) * width, chroma + static_cast<size_t>(y) * pitch, width);
            pendingWidth_ = width;
            pendingHeight_ = height;
            hasFrame_ = true;
        }
        wake_.notify_one();
    }

    void ClearSignal() {
        std::lock_guard guard(lock_);
        hasFrame_ = false;
        lastFrame_ = {};
    }

private:
    void Run() {
        std::vector<uint8_t> frame;
        std::unique_lock guard(lock_);
        while (running_) {
            wake_.wait_for(guard, kIdleInterval, [this] { return hasFrame_ || !running_; });
            if (!running_) break;
            const auto now = std::chrono::steady_clock::now();
            if (hasFrame_) {
                frame.swap(pending_);
                const uint32_t width = pendingWidth_;
                const uint32_t height = pendingHeight_;
                hasFrame_ = false;
                lastFrame_ = now;
                guard.unlock();
                ConvertToBgr(frame.data(), frame.data() + static_cast<size_t>(width) * height, width, width, height, bgr_.data());
                softcam::sender::SendFrame(camera_, bgr_.data());
                guard.lock();
            } else if (now - lastFrame_ >= kStaleAfter) {
                guard.unlock();
                std::fill(bgr_.begin(), bgr_.end(), kNoSignalGray);
                softcam::sender::SendFrame(camera_, bgr_.data());
                guard.lock();
            }
        }
    }

    std::mutex lock_;
    std::condition_variable wake_;
    std::thread worker_;
    softcam::sender::CameraHandle camera_ = nullptr;
    bool running_ = false;
    bool hasFrame_ = false;
    std::vector<uint8_t> pending_;
    uint32_t pendingWidth_ = 0;
    uint32_t pendingHeight_ = 0;
    std::chrono::steady_clock::time_point lastFrame_{};
    // Only the worker writes it while running.
    std::vector<uint8_t> bgr_;

public:
    // At process exit the worker is already gone and joining from DLL unload would deadlock.
    ~Output() {
        if (worker_.joinable()) worker_.detach();
    }
};

Output& Instance() {
    static Output output;
    return output;
}

}  // namespace

HRESULT Start() { return Instance().Start(); }
void Stop() { Instance().Stop(); }
bool IsActive() { return Instance().IsActive(); }

void Submit(const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height) {
    Instance().Submit(luma, chroma, pitch, width, height);
}

void ClearSignal() { Instance().ClearSignal(); }

void ConvertToBgr(const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height, uint8_t* bgr) {
    // Fit inside the output keeping the aspect ratio; the rest stays black.
    const double scale = std::min(static_cast<double>(kWidth) / width, static_cast<double>(kHeight) / height);
    const int outWidth = std::clamp(static_cast<int>(width * scale + 0.5), 1, kWidth);
    const int outHeight = std::clamp(static_cast<int>(height * scale + 0.5), 1, kHeight);
    const int left = (kWidth - outWidth) / 2;
    const int top = (kHeight - outHeight) / 2;
    if (outWidth != kWidth || outHeight != kHeight) std::memset(bgr, 0, static_cast<size_t>(kWidth) * kHeight * 3);

    // Source position of every output column in 16.16 fixed point (bilinear luma, nearest chroma).
    std::vector<uint32_t> columns(outWidth);
    const double stepX = static_cast<double>(width) / outWidth;
    for (int x = 0; x < outWidth; ++x) {
        const double sourceX = std::clamp((x + 0.5) * stepX - 0.5, 0.0, static_cast<double>(width - 1));
        columns[x] = static_cast<uint32_t>(sourceX * 65536.0);
    }
    const double stepY = static_cast<double>(height) / outHeight;

    for (int y = 0; y < outHeight; ++y) {
        const double sourceY = std::clamp((y + 0.5) * stepY - 0.5, 0.0, static_cast<double>(height - 1));
        const uint32_t y0 = static_cast<uint32_t>(sourceY);
        const uint32_t y1 = std::min(y0 + 1, height - 1);
        const uint32_t fy = static_cast<uint32_t>((sourceY - y0) * 256.0);
        const uint8_t* row0 = luma + static_cast<size_t>(y0) * pitch;
        const uint8_t* row1 = luma + static_cast<size_t>(y1) * pitch;
        const uint8_t* chromaRow = chroma + static_cast<size_t>(std::min<uint32_t>(static_cast<uint32_t>(sourceY + 0.5), height - 1) / 2) * pitch;
        uint8_t* out = bgr + (static_cast<size_t>(top + y) * kWidth + left) * 3;
        for (int x = 0; x < outWidth; ++x) {
            const uint32_t x0 = columns[x] >> 16;
            const uint32_t x1 = std::min(x0 + 1, width - 1);
            const uint32_t fx = (columns[x] >> 8) & 0xFF;
            const uint32_t top2 = row0[x0] * (256 - fx) + row0[x1] * fx;
            const uint32_t bottom2 = row1[x0] * (256 - fx) + row1[x1] * fx;
            const int lumaValue = static_cast<int>((top2 * (256 - fy) + bottom2 * fy + 32768) >> 16);
            const uint32_t chromaX = std::min<uint32_t>(x0 + (fx >= 128 ? 1 : 0), width - 1) & ~1u;
            // BT.709, video range (what phone H.264 uses for HD), as in the preview.
            const int c = 298 * (lumaValue - 16);
            const int d = chromaRow[chromaX] - 128;
            const int e = chromaRow[chromaX + 1] - 128;
            out[x * 3 + 0] = Clamp((c + 541 * d + 128) >> 8);
            out[x * 3 + 1] = Clamp((c - 55 * d - 136 * e + 128) >> 8);
            out[x * 3 + 2] = Clamp((c + 459 * e + 128) >> 8);
        }
    }
}

}  // namespace hitcam::dshow
