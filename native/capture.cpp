// Small DXGI desktop duplication bridge. No third-party code or runtime DLLs.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <vector>
#include <memory>
#include <cstring>
#include <cstdint>
#include <new>

using Microsoft::WRL::ComPtr;

struct Capture {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11Texture2D> staging;
    std::vector<uint32_t> pixels;
    std::vector<UINT> horizontal;
    UINT width = 0, height = 0;
    bool initialized = false;
};

extern "C" __declspec(dllexport) HRESULT __cdecl lume_create(int x, int y, void** result) noexcept {
    if (!result) return E_POINTER;
    *result = nullptr;
    try {
        ComPtr<IDXGIFactory1> factory;
        HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (FAILED(hr)) return hr;
        for (UINT a = 0; ; ++a) {
            ComPtr<IDXGIAdapter1> adapter;
            hr = factory->EnumAdapters1(a, &adapter);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(hr)) return hr;
            for (UINT o = 0; ; ++o) {
                ComPtr<IDXGIOutput> output;
                hr = adapter->EnumOutputs(o, &output);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                if (FAILED(hr)) return hr;
                DXGI_OUTPUT_DESC desc = {};
                if (FAILED(output->GetDesc(&desc)) || !desc.AttachedToDesktop || desc.DesktopCoordinates.left != x || desc.DesktopCoordinates.top != y) continue;
                if (desc.Rotation != DXGI_MODE_ROTATION_IDENTITY && desc.Rotation != DXGI_MODE_ROTATION_UNSPECIFIED) return DXGI_ERROR_UNSUPPORTED;
                auto capture = std::make_unique<Capture>();
                D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
                hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &capture->device, nullptr, &capture->context);
                if (FAILED(hr)) return hr;
                ComPtr<IDXGIOutput1> output1;
                hr = output.As(&output1);
                if (FAILED(hr)) return hr;
                hr = output1->DuplicateOutput(capture->device.Get(), &capture->duplication);
                if (FAILED(hr)) return hr;
                DXGI_OUTDUPL_DESC duplicationDesc = {};
                capture->duplication->GetDesc(&duplicationDesc);
                capture->width = duplicationDesc.ModeDesc.Width;
                capture->height = duplicationDesc.ModeDesc.Height;
                if (!capture->width || !capture->height || (uint64_t)capture->width * capture->height > 33554432 || duplicationDesc.ModeDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) return DXGI_ERROR_UNSUPPORTED;
                D3D11_TEXTURE2D_DESC texture = {};
                texture.Width = capture->width; texture.Height = capture->height;
                texture.MipLevels = 1; texture.ArraySize = 1; texture.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                texture.SampleDesc.Count = 1; texture.Usage = D3D11_USAGE_STAGING; texture.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                hr = capture->device->CreateTexture2D(&texture, nullptr, &capture->staging);
                if (FAILED(hr)) return hr;
                capture->pixels.resize((size_t)capture->width * capture->height);
                *result = capture.release();
                return S_OK;
            }
        }
        return DXGI_ERROR_NOT_FOUND;
    } catch (...) { return E_OUTOFMEMORY; }
}

extern "C" __declspec(dllexport) HRESULT __cdecl lume_capture(void* handle, void* destination, int width, int height, int stride) noexcept {
    Capture* capture = static_cast<Capture*>(handle);
    if (!capture || !destination || width < 1 || height < 1 || width > 16384 || height > 16384 || (uint64_t)width * height > 33554432 || stride < width * 4) return E_INVALIDARG;
    DXGI_OUTDUPL_FRAME_INFO frame = {};
    bool changed = false;
    ComPtr<IDXGIResource> resource;
    HRESULT hr = capture->duplication->AcquireNextFrame(capture->initialized ? 0 : 1000, &frame, &resource);
    if (SUCCEEDED(hr)) {
        changed = !capture->initialized || frame.LastPresentTime.QuadPart != 0;
        if (changed) {
        ComPtr<ID3D11Texture2D> texture;
        hr = resource.As(&texture);
        if (SUCCEEDED(hr)) {
            capture->context->CopyResource(capture->staging.Get(), texture.Get());
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            hr = capture->context->Map(capture->staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
            if (SUCCEEDED(hr)) {
                for (UINT row = 0; row < capture->height; ++row) {
                    std::memcpy(capture->pixels.data() + (size_t)row * capture->width, static_cast<const BYTE*>(mapped.pData) + (size_t)row * mapped.RowPitch, (size_t)capture->width * 4);
                }
                capture->context->Unmap(capture->staging.Get(), 0);
                capture->initialized = true;
            }
        }
        }
        capture->duplication->ReleaseFrame();
        if (FAILED(hr)) return hr;
    } else if (hr != DXGI_ERROR_WAIT_TIMEOUT || !capture->initialized) {
        return hr;
    }
    if (capture->horizontal.size() != static_cast<size_t>(width)) {
        try { capture->horizontal.resize(width); } catch (...) { return E_OUTOFMEMORY; }
        for (int x = 0; x < width; ++x) capture->horizontal[x] = static_cast<UINT>((uint64_t)x * capture->width / width);
    }
    for (int y = 0; y < height; ++y) {
        uint32_t* target = reinterpret_cast<uint32_t*>(static_cast<BYTE*>(destination) + (size_t)y * stride);
        const uint32_t* source = capture->pixels.data() + (size_t)((uint64_t)y * capture->height / height) * capture->width;
        if ((UINT)width == capture->width) std::memcpy(target, source, (size_t)width * 4);
        else for (int x = 0; x < width; ++x) target[x] = source[capture->horizontal[x]];
    }
    return changed ? S_OK : S_FALSE;
}

extern "C" __declspec(dllexport) void __cdecl lume_destroy(void* handle) noexcept { delete static_cast<Capture*>(handle); }
