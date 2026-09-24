#pragma once

#include <windows.h>
#include <cstdint>

// Frames travel from HitCam.exe to the media source over a named shared-memory section.
// The media source runs inside the Windows Camera Frame Server service (LOCAL SERVICE, session 0),
// so the section lives in the Global namespace. A normal user cannot create Global objects,
// so whichever side can create it does, and the other side opens it.
//
// Access model:
//  - The creator (normally the frame server) gives full access to SYSTEM, Administrators, the frame server's
//    service SID (NT SERVICE\FrameServer; LOCAL SERVICE if it cannot be resolved) and interactive users, since
//    it cannot know which user's HitCam.exe will publish. A medium mandatory label keeps low-integrity
//    (sandboxed) processes from writing even when they run as that user.
//  - HitCam.exe, right after opening it, replaces the DACL with SYSTEM, Administrators, the frame server and
//    its own user: other users and other LOCAL SERVICE services can then neither watch the picture nor inject
//    frames. The section lives while any handle is open, so as long as a camera app keeps the camera open,
//    another user's HitCam cannot take it over.
//  - The media source maps it read-only: nothing the service holds can be used to write frames.
namespace hitcam {

// {FB88D813-B99A-4C03-8C22-A6C972983FCD}
inline constexpr GUID kMediaSourceClsid = {0xfb88d813, 0xb99a, 0x4c03, {0x8c, 0x22, 0xa6, 0xc9, 0x72, 0x98, 0x3f, 0xcd}};
inline constexpr wchar_t kMediaSourceClsidString[] = L"{FB88D813-B99A-4C03-8C22-A6C972983FCD}";
inline constexpr wchar_t kFriendlyName[] = L"HitCam";

inline constexpr wchar_t kSharedMemoryName[] = L"Global\\HitCamVirtualCameraFrames";
inline constexpr wchar_t kFrameServerAccount[] = L"NT SERVICE\\FrameServer";

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
    // NV12 frame in `data`, luma pitch == width; 0 means no signal. The reader reads each exactly once per frame.
    volatile uint32_t width;
    volatile uint32_t height;
    // GetTickCount64() of the last published frame.
    volatile LONG64 updatedMs;
    uint8_t reserved[32];
};

inline constexpr size_t kSharedMemorySize = sizeof(SharedHeader) + kMaxFrameBytes;

inline uint8_t* FrameData(SharedHeader* header) { return reinterpret_cast<uint8_t*>(header + 1); }

enum class SharedAccess {
    // The media source: creates the section when it may and maps it read-only.
    Read,
    // HitCam.exe: opens (or, when it may, creates) the section, restricts it to its own user, maps it read-write.
    Write,
};

// Returns nullptr if the section can be neither created nor opened yet.
SharedHeader* MapSharedFrames(HANDLE* mapping, SharedAccess access = SharedAccess::Write);
void UnmapSharedFrames(SharedHeader* header, HANDLE mapping);

// SID string of the frame server service, or "LS" (LOCAL SERVICE) if it cannot be resolved. Cached.
const wchar_t* FrameServerSid();

}  // namespace hitcam
