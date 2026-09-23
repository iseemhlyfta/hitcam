#pragma once

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <mutex>

#include "MediaSource.h"

namespace hitcam {

// The camera frame server creates the registered class and expects an IMFActivate that produces the media source.
// Attributes the frame server sets here are passed on to the source.
class Activator : public Microsoft::WRL::RuntimeClass<
                      Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                      Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>> {
public:
    HRESULT RuntimeClassInitialize() { return MFCreateAttributes(&attributes_, 4); }

    // IMFActivate
    IFACEMETHODIMP ActivateObject(REFIID riid, void** object) override {
        if (!object) return E_POINTER;
        *object = nullptr;
        std::lock_guard guard(lock_);
        if (!source_) {
            HRESULT hr = Microsoft::WRL::MakeAndInitialize<MediaSource>(&source_, attributes_.Get());
            if (FAILED(hr)) return hr;
        }
        return source_.CopyTo(riid, object);
    }

    IFACEMETHODIMP ShutdownObject() override {
        std::lock_guard guard(lock_);
        if (source_) source_->Shutdown();
        source_.Reset();
        return S_OK;
    }

    IFACEMETHODIMP DetachObject() override {
        std::lock_guard guard(lock_);
        source_.Reset();
        return S_OK;
    }

    // IMFAttributes, delegated
    IFACEMETHODIMP GetItem(REFGUID key, PROPVARIANT* item) override { return attributes_->GetItem(key, item); }
    IFACEMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) override { return attributes_->GetItemType(key, type); }
    IFACEMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT item, BOOL* result) override { return attributes_->CompareItem(key, item, result); }
    IFACEMETHODIMP Compare(IMFAttributes* other, MF_ATTRIBUTES_MATCH_TYPE type, BOOL* result) override { return attributes_->Compare(other, type, result); }
    IFACEMETHODIMP GetUINT32(REFGUID key, UINT32* item) override { return attributes_->GetUINT32(key, item); }
    IFACEMETHODIMP GetUINT64(REFGUID key, UINT64* item) override { return attributes_->GetUINT64(key, item); }
    IFACEMETHODIMP GetDouble(REFGUID key, double* item) override { return attributes_->GetDouble(key, item); }
    IFACEMETHODIMP GetGUID(REFGUID key, GUID* item) override { return attributes_->GetGUID(key, item); }
    IFACEMETHODIMP GetStringLength(REFGUID key, UINT32* length) override { return attributes_->GetStringLength(key, length); }
    IFACEMETHODIMP GetString(REFGUID key, LPWSTR item, UINT32 size, UINT32* length) override { return attributes_->GetString(key, item, size, length); }
    IFACEMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* item, UINT32* length) override { return attributes_->GetAllocatedString(key, item, length); }
    IFACEMETHODIMP GetBlobSize(REFGUID key, UINT32* size) override { return attributes_->GetBlobSize(key, size); }
    IFACEMETHODIMP GetBlob(REFGUID key, UINT8* buffer, UINT32 size, UINT32* written) override { return attributes_->GetBlob(key, buffer, size, written); }
    IFACEMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** buffer, UINT32* size) override { return attributes_->GetAllocatedBlob(key, buffer, size); }
    IFACEMETHODIMP GetUnknown(REFGUID key, REFIID riid, LPVOID* object) override { return attributes_->GetUnknown(key, riid, object); }
    IFACEMETHODIMP SetItem(REFGUID key, REFPROPVARIANT item) override { return attributes_->SetItem(key, item); }
    IFACEMETHODIMP DeleteItem(REFGUID key) override { return attributes_->DeleteItem(key); }
    IFACEMETHODIMP DeleteAllItems() override { return attributes_->DeleteAllItems(); }
    IFACEMETHODIMP SetUINT32(REFGUID key, UINT32 item) override { return attributes_->SetUINT32(key, item); }
    IFACEMETHODIMP SetUINT64(REFGUID key, UINT64 item) override { return attributes_->SetUINT64(key, item); }
    IFACEMETHODIMP SetDouble(REFGUID key, double item) override { return attributes_->SetDouble(key, item); }
    IFACEMETHODIMP SetGUID(REFGUID key, REFGUID item) override { return attributes_->SetGUID(key, item); }
    IFACEMETHODIMP SetString(REFGUID key, LPCWSTR item) override { return attributes_->SetString(key, item); }
    IFACEMETHODIMP SetBlob(REFGUID key, const UINT8* buffer, UINT32 size) override { return attributes_->SetBlob(key, buffer, size); }
    IFACEMETHODIMP SetUnknown(REFGUID key, IUnknown* item) override { return attributes_->SetUnknown(key, item); }
    IFACEMETHODIMP LockStore() override { return attributes_->LockStore(); }
    IFACEMETHODIMP UnlockStore() override { return attributes_->UnlockStore(); }
    IFACEMETHODIMP GetCount(UINT32* count) override { return attributes_->GetCount(count); }
    IFACEMETHODIMP GetItemByIndex(UINT32 index, GUID* key, PROPVARIANT* item) override { return attributes_->GetItemByIndex(index, key, item); }
    IFACEMETHODIMP CopyAllItems(IMFAttributes* destination) override { return attributes_->CopyAllItems(destination); }

private:
    std::mutex lock_;
    Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
    Microsoft::WRL::ComPtr<MediaSource> source_;
};

}  // namespace hitcam
