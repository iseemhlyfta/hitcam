#pragma once

#include <windows.h>
#include <cstdint>

// The sending side of the DirectShow "HitCam" camera (Windows 10; see dshow/HitCamDShow.cpp for the camera itself).
// Frames go through Softcam's shared memory as 1920x1080 BGR24: smaller or portrait frames are scaled to fit with
// black bars. While no frames come, a dark "no signal" picture keeps apps that opened the camera running.
namespace hitcam::dshow {

inline constexpr int kWidth = 1920;
inline constexpr int kHeight = 1080;

// Creates the camera for this process's lifetime. Fails if another process (another HitCam) already sends.
HRESULT Start();
void Stop();
bool IsActive();

// Copies an NV12 frame (BT.709, video range) for sending; the conversion happens on the output's own thread.
void Submit(const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height);
void ClearSignal();

// NV12 → top-down BGR24 of kWidth x kHeight, letterboxed. Exposed for the tests.
void ConvertToBgr(const uint8_t* luma, const uint8_t* chroma, uint32_t pitch, uint32_t width, uint32_t height, uint8_t* bgr);

}  // namespace hitcam::dshow
