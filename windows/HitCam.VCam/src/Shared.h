#pragma once

#include <windows.h>
#include <cstdint>

// Frames travel from HitCam.exe to the media source over a named shared-memory section.
// The media source runs inside the Windows Camera Frame Server service (LOCAL SERVICE, session 0),
// so the section lives in the Global namespace. A normal user cannot create Global objects,
// so whichever side can create it does, and the other side opens it.
namespace hitcam {

// {FB88D813-B99A-4C03-8C22-A6C972983FCD}
inline constexpr GUID kMediaSourceClsid = {0xfb88d813, 0xb99a, 0x4c03, {0x8c, 0x22, 0xa6, 0xc9, 0x72, 0x98, 0x3f, 0xcd}};
inline constexpr wchar_t kMediaSourceClsidString[] = L"{FB88D813-B99A-4C03-8C22-A6C972983FCD}";
inline constexpr wchar_t kFriendlyName[] = L"HitCam";

inline constexpr wchar_t kSharedMemoryName[] = L"Global\\HitCamVirtualCameraFrames";
// SYSTEM, Administrators, LOCAL SERVICE and interactive users get full access.
inline constexpr wchar_t kSharedMemorySddl[] = L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)(A;;GA;;;IU)";

inline constexpr uint32_t kMagic = 0x4D414348;  // "HCAM"
inline constexpr uint32_t kVersion = 1;
// Portrait 1080p (1080x1920) must fit as well as landscape.
inline constexpr uint32_t kMaxSide = 1920;
inline constexpr uint32_t kMaxFrameBytes = kMaxSide * kMaxSide * 3 / 2;
// The camera shows "no signal" when the app stops publishing for this long.
inline constexpr uint64_t kStaleAfterMs = 1500;

struct SharedHeader {
    uint32_t magic;
    uint32_t version;
    // Seqlock: odd while the writer is copying a frame.
    volatile LONG64 sequence;
    // NV12 frame in `data`, luma pitch == width; 0 means no signal.
    uint32_t width;
    uint32_t height;
    // GetTickCount64() of the last published frame.
    volatile LONG64 updatedMs;
    uint8_t reserved[32];
};

inline constexpr size_t kSharedMemorySize = sizeof(SharedHeader) + kMaxFrameBytes;

inline uint8_t* FrameData(SharedHeader* header) { return reinterpret_cast<uint8_t*>(header + 1); }

// Creates the section when allowed, otherwise opens the existing one. Returns nullptr if neither works yet.
SharedHeader* MapSharedFrames(HANDLE* mapping);
void UnmapSharedFrames(SharedHeader* header, HANDLE mapping);

}  // namespace hitcam
