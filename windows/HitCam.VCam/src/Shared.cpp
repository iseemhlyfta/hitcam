#include "Shared.h"

#include <sddl.h>

namespace hitcam {

SharedHeader* MapSharedFrames(HANDLE* mapping) {
    *mapping = nullptr;

    PSECURITY_DESCRIPTOR descriptor = nullptr;
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), nullptr, FALSE};
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(kSharedMemorySddl, SDDL_REVISION_1, &descriptor, nullptr)) {
        attributes.lpSecurityDescriptor = descriptor;
    }

    HANDLE handle = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0,
                                       static_cast<DWORD>(kSharedMemorySize), kSharedMemoryName);
    if (descriptor) LocalFree(descriptor);
    if (!handle) {
        // No SeCreateGlobalPrivilege: the section can only be opened once the other side created it.
        handle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kSharedMemoryName);
        if (!handle) return nullptr;
    }

    auto* header = static_cast<SharedHeader*>(MapViewOfFile(handle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, kSharedMemorySize));
    if (!header) {
        CloseHandle(handle);
        return nullptr;
    }
    // A fresh section is zero-filled; stamp it so both sides agree on the layout.
    if (InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&header->magic), static_cast<LONG>(kMagic), 0) == 0) {
        header->version = kVersion;
    }
    if (header->magic != kMagic || header->version != kVersion) {
        UnmapViewOfFile(header);
        CloseHandle(handle);
        return nullptr;
    }
    *mapping = handle;
    return header;
}

void UnmapSharedFrames(SharedHeader* header, HANDLE mapping) {
    if (header) UnmapViewOfFile(header);
    if (mapping) CloseHandle(mapping);
}

}  // namespace hitcam
