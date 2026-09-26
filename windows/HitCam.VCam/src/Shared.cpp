#include "Shared.h"

#include <aclapi.h>
#include <sddl.h>

#include <string>

namespace hitcam {
namespace {

std::wstring SidToString(PSID sid) {
    wchar_t* text = nullptr;
    if (!ConvertSidToStringSidW(sid, &text)) return {};
    std::wstring result = text;
    LocalFree(text);
    return result;
}

std::wstring CurrentUserSid() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
    alignas(TOKEN_USER) BYTE buffer[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE];
    DWORD size = 0;
    std::wstring result;
    if (GetTokenInformation(token, TokenUser, buffer, sizeof(buffer), &size)) result = SidToString(reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid);
    CloseHandle(token);
    return result;
}

// Whoever creates the section: see the access model in Shared.h.
std::wstring CreationSddl() {
    return std::wstring(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;") + FrameServerSid() + L")(A;;GA;;;IU)S:(ML;;NW;;;ME)";
}

// The writer takes the section over for its own user; the label stays as created.
bool RestrictToCurrentUser(HANDLE handle, const std::wstring& sddl) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) return false;
    BOOL present = FALSE, defaulted = FALSE;
    PACL dacl = nullptr;
    bool ok = GetSecurityDescriptorDacl(descriptor, &present, &dacl, &defaulted) && present && dacl
              && SetSecurityInfo(handle, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                                 nullptr, nullptr, dacl, nullptr) == ERROR_SUCCESS;
    LocalFree(descriptor);
    return ok;
}

SharedHeader* MapView(HANDLE handle, DWORD access) {
    return static_cast<SharedHeader*>(MapViewOfFile(handle, access, 0, 0, kSharedMemorySize));
}

// A fresh section is zero-filled; stamp it so both sides agree on the layout.
void Stamp(SharedHeader* header) {
    if (InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&header->magic), static_cast<LONG>(kMagic), 0) == 0) {
        header->version = kVersion;
    }
}

HANDLE CreateSection(const std::wstring& sddl, bool* created) {
    *created = false;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    // Never fall back to the default DACL: in the service that would be LOCAL SERVICE's, not the user's.
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) return nullptr;
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
    HANDLE handle = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0,
                                       static_cast<DWORD>(kSharedMemorySize), kSharedMemoryName);
    *created = handle && GetLastError() != ERROR_ALREADY_EXISTS;
    LocalFree(descriptor);
    return handle;
}

// Strings are built before any handle is opened, so an allocation failure cannot leak one.
SharedHeader* MapForReading(HANDLE* mapping) {
    const std::wstring creation = CreationSddl();
    bool created = false;
    HANDLE handle = CreateSection(creation, &created);
    // Not allowed to create Global objects: the section can only be opened once the other side created it.
    if (!handle) handle = OpenFileMappingW(FILE_MAP_READ, FALSE, kSharedMemoryName);
    if (!handle) return nullptr;
    if (created) {
        // Only the creator writes, once, to stamp the header; frames are then only ever read.
        if (SharedHeader* writable = MapView(handle, FILE_MAP_WRITE)) {
            Stamp(writable);
            UnmapViewOfFile(writable);
        }
    }
    SharedHeader* header = MapView(handle, FILE_MAP_READ);
    if (!header) {
        CloseHandle(handle);
        return nullptr;
    }
    *mapping = handle;
    return header;
}

SharedHeader* MapForWriting(HANDLE* mapping) {
    const std::wstring creation = CreationSddl();
    const std::wstring user = CurrentUserSid();
    const std::wstring restricted = user.empty() ? std::wstring()
                                                 : std::wstring(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;") + FrameServerSid() + L")(A;;GA;;;" + user + L")";

    // Open first: a normal user cannot create it anyway, and WRITE_DAC is needed to restrict it.
    HANDLE handle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE | READ_CONTROL | WRITE_DAC, FALSE, kSharedMemoryName);
    if (!handle) handle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kSharedMemoryName);
    if (!handle) {
        bool created = false;
        handle = CreateSection(creation, &created);
    }
    if (!handle) return nullptr;
    // Best effort: without it the section keeps the creator's DACL (interactive users), as older versions did.
    if (!restricted.empty()) RestrictToCurrentUser(handle, restricted);

    SharedHeader* header = MapView(handle, FILE_MAP_READ | FILE_MAP_WRITE);
    if (!header) {
        CloseHandle(handle);
        return nullptr;
    }
    Stamp(header);
    // A writer that died between BeginWrite and EndWrite left the sequence odd. This one is the only writer now, so
    // it brings the sequence back to "idle"; otherwise every later frame would look busy or torn to the reader.
    if (header->sequence & 1) InterlockedIncrement64(&header->sequence);
    *mapping = handle;
    return header;
}

}  // namespace

const wchar_t* FrameServerSid() {
    static const std::wstring sid = [] {
        BYTE buffer[SECURITY_MAX_SID_SIZE];
        DWORD size = sizeof(buffer);
        wchar_t domain[256];
        DWORD domainSize = ARRAYSIZE(domain);
        SID_NAME_USE use{};
        std::wstring result;
        if (LookupAccountNameW(nullptr, kFrameServerAccount, buffer, &size, domain, &domainSize, &use)) result = SidToString(buffer);
        return result.empty() ? std::wstring(L"LS") : result;
    }();
    return sid.c_str();
}

SharedHeader* MapSharedFrames(HANDLE* mapping, SharedAccess access) {
    *mapping = nullptr;
    SharedHeader* header = nullptr;
    try {
        header = access == SharedAccess::Read ? MapForReading(mapping) : MapForWriting(mapping);
    } catch (...) {
        // Out of memory building the descriptors; the caller retries later.
        if (*mapping) CloseHandle(*mapping);
        *mapping = nullptr;
        return nullptr;
    }
    if (header && (header->magic != kMagic || header->version != kVersion)) {
        UnmapSharedFrames(header, *mapping);
        *mapping = nullptr;
        return nullptr;
    }
    return header;
}

void UnmapSharedFrames(SharedHeader* header, HANDLE mapping) {
    if (header) UnmapViewOfFile(header);
    if (mapping) CloseHandle(mapping);
}

}  // namespace hitcam
