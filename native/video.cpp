// Windows Media Foundation H.264 bridge. Original code; Windows codecs only.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <codecapi.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>
#include <vector>
#include <memory>
#include <algorithm>
#include <cstring>
#include <cstdint>

using Microsoft::WRL::ComPtr;
namespace {
constexpr UINT MaxDimension = 4096;
constexpr uint64_t MaxPixels = 4096ULL * 2160;
constexpr DWORD MaxCompressed = 32 * 1024 * 1024;
struct Failure { HRESULT code; };
void Check(HRESULT hr) { if (FAILED(hr)) throw Failure{hr}; }
BYTE Clamp(int value) { return static_cast<BYTE>(std::max(0, std::min(255, value))); }
bool ValidSize(UINT width, UINT height) {
    return width >= 2 && height >= 2 && width <= MaxDimension && height <= MaxDimension &&
        !(width & 1) && !(height & 1) && uint64_t(width) * height <= MaxPixels;
}
struct Runtime {
    bool com = false, media = false;
    Runtime() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        com = SUCCEEDED(hr); if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) Check(hr);
        hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (FAILED(hr)) { if (com) CoUninitialize(); Check(hr); }
        media = true;
    }
    ~Runtime() { if (media) MFShutdown(); if (com) CoUninitialize(); }
};
struct Codec {
    Runtime runtime;
    ComPtr<IMFTransform> transform;
    ComPtr<IMFActivate> activation;
    ComPtr<IMFMediaEventGenerator> events;
    ComPtr<ICodecAPI> properties;
    UINT width = 0, height = 0, fps = 0;
    DWORD inputId = 0, outputId = 0;
    bool hardware = false, asynchronous = false, encoder = false;
    unsigned needInput = 0, haveOutput = 0;
    LONGLONG timestamp = 0;
    LONG outputStride = 0;
    UINT outputHeight = 0;
    std::vector<BYTE> output;
    wchar_t name[160] = {};
    ~Codec() {
        if (transform) { transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0); transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0); }
        if (activation) activation->ShutdownObject();
        transform.Reset(); events.Reset(); properties.Reset(); activation.Reset();
    }
    void Property(const GUID& key, ULONG value) {
        if (!properties) return;
        VARIANT item; VariantInit(&item); item.vt = VT_UI4; item.ulVal = value;
        properties->SetValue(&key, &item);
    }
    void Boolean(const GUID& key, bool value) {
        if (!properties) return;
        VARIANT item; VariantInit(&item); item.vt = VT_BOOL; item.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
        properties->SetValue(&key, &item);
    }
    void InitializeTransform() {
        ComPtr<IMFAttributes> attributes;
        if (SUCCEEDED(transform->GetAttributes(&attributes))) {
            UINT32 value = 0; attributes->GetUINT32(MF_TRANSFORM_ASYNC, &value); asynchronous = value != 0;
            if (asynchronous) { Check(attributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE)); Check(transform.As(&events)); }
            attributes->SetUINT32(MF_LOW_LATENCY, TRUE);
        }
        DWORD inputs = 0, outputs = 0; Check(transform->GetStreamCount(&inputs, &outputs));
        if (inputs != 1 || outputs != 1) Check(MF_E_INVALIDSTREAMNUMBER);
        HRESULT hr = transform->GetStreamIDs(1, &inputId, 1, &outputId);
        if (hr == E_NOTIMPL) { inputId = outputId = 0; } else Check(hr);
        transform.As(&properties);
    }
    void PumpEvents(DWORD timeout) {
        if (!asynchronous) return;
        ULONGLONG start = GetTickCount64();
        do {
            ComPtr<IMFMediaEvent> event;
            HRESULT hr = events->GetEvent(MF_EVENT_FLAG_NO_WAIT, &event);
            if (hr == MF_E_NO_EVENTS_AVAILABLE) {
                if (GetTickCount64() - start >= timeout) return;
                Sleep(1); continue;
            }
            Check(hr); HRESULT status = S_OK; Check(event->GetStatus(&status)); Check(status);
            MediaEventType kind = MEUnknown; Check(event->GetType(&kind));
            if (kind == METransformNeedInput) ++needInput;
            else if (kind == METransformHaveOutput) ++haveOutput;
            if (needInput || haveOutput) return;
        } while (GetTickCount64() - start < timeout);
    }
};
ComPtr<IMFMediaType> Type(GUID subtype, UINT width, UINT height, UINT fps) {
    ComPtr<IMFMediaType> type; Check(MFCreateMediaType(&type));
    Check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    Check(type->SetGUID(MF_MT_SUBTYPE, subtype));
    Check(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height));
    Check(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, fps, 1));
    Check(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    Check(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
    type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
    return type;
}
ComPtr<IMFSample> Sample(const BYTE* bytes, DWORD length, DWORD alignment = 0) {
    ComPtr<IMFSample> sample; ComPtr<IMFMediaBuffer> buffer;
    Check(MFCreateSample(&sample)); Check(MFCreateAlignedMemoryBuffer(length, alignment, &buffer));
    if (bytes) { BYTE* target; Check(buffer->Lock(&target, nullptr, nullptr)); std::memcpy(target, bytes, length); Check(buffer->Unlock()); Check(buffer->SetCurrentLength(length)); }
    Check(sample->AddBuffer(buffer.Get())); return sample;
}
void Start(Codec& codec) {
    Check(codec.transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
    Check(codec.transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
}
void ConfigureEncoder(Codec& codec, UINT bitrate) {
    codec.InitializeTransform();
    codec.Boolean(CODECAPI_AVLowLatencyMode, true);
    codec.Property(CODECAPI_AVEncMPVDefaultBPictureCount, 0);
    codec.Property(CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR);
    codec.Property(CODECAPI_AVEncCommonMeanBitRate, bitrate);
    codec.Property(CODECAPI_AVEncCommonQualityVsSpeed, 25);
    codec.Property(CODECAPI_AVEncMPVGOPSize, std::max(1U, codec.fps * 2));
    codec.Property(CODECAPI_AVEncNumWorkerThreads, 2);
    auto output = Type(MFVideoFormat_H264, codec.width, codec.height, codec.fps);
    Check(output->SetUINT32(MF_MT_AVG_BITRATE, bitrate));
    Check(output->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base));
    Check(codec.transform->SetOutputType(codec.outputId, output.Get(), 0));
    auto input = Type(MFVideoFormat_NV12, codec.width, codec.height, codec.fps);
    Check(codec.transform->SetInputType(codec.inputId, input.Get(), 0));
    Start(codec);
}
void DecoderOutputType(Codec& codec) {
    ComPtr<IMFMediaType> selected;
    for (DWORD index = 0; index < 64; ++index) {
        ComPtr<IMFMediaType> type;
        HRESULT hr = codec.transform->GetOutputAvailableType(codec.outputId, index, &type);
        if (hr == MF_E_NO_MORE_TYPES) break; Check(hr);
        GUID subtype; Check(type->GetGUID(MF_MT_SUBTYPE, &subtype));
        if (subtype != MFVideoFormat_NV12) continue;
        UINT32 width = 0, height = 0; Check(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height));
        if (width != codec.width || height < codec.height || height > codec.height + 32) Check(MF_E_INVALIDMEDIATYPE);
        Check(codec.transform->SetOutputType(codec.outputId, type.Get(), 0));
        UINT32 stride = 0;
        if (FAILED(type->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride))) stride = width;
        if (stride < width || stride > width + 512) Check(MF_E_INVALIDMEDIATYPE);
        codec.outputStride = static_cast<LONG>(stride); codec.outputHeight = height; selected = type; break;
    }
    if (!selected) Check(MF_E_INVALIDMEDIATYPE);
}
ComPtr<IMFSample> Output(Codec& codec, unsigned changes = 0) {
    MFT_OUTPUT_STREAM_INFO info = {}; Check(codec.transform->GetOutputStreamInfo(codec.outputId, &info));
    ComPtr<IMFSample> owned;
    if (!(info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
        DWORD minimum = codec.encoder ? codec.width * codec.height * 2 + 65536 : codec.width * (codec.height + 32) * 4;
        DWORD size = std::max(info.cbSize, minimum); if (size > MaxCompressed * 2) Check(E_OUTOFMEMORY);
        owned = Sample(nullptr, size, info.cbAlignment);
    }
    MFT_OUTPUT_DATA_BUFFER data = {}; data.dwStreamID = codec.outputId; data.pSample = owned.Get();
    DWORD flags = 0; HRESULT hr = codec.transform->ProcessOutput(0, 1, &data, &flags);
    if (data.pEvents) data.pEvents->Release();
    ComPtr<IMFSample> result;
    if (data.pSample == owned.Get()) result = owned;
    else if (data.pSample) result.Attach(data.pSample);
    if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
        if (changes >= 3) Check(MF_E_INVALIDMEDIATYPE);
        if (codec.encoder) {
            ComPtr<IMFMediaType> type; Check(codec.transform->GetOutputAvailableType(codec.outputId, 0, &type));
            GUID subtype; Check(type->GetGUID(MF_MT_SUBTYPE, &subtype));
            UINT32 width = 0, height = 0; Check(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height));
            if (subtype != MFVideoFormat_H264 || width != codec.width || height != codec.height) Check(MF_E_INVALIDMEDIATYPE);
            Check(codec.transform->SetOutputType(codec.outputId, type.Get(), 0));
        } else DecoderOutputType(codec);
        if (codec.asynchronous) {
            ULONGLONG until = GetTickCount64() + 2000;
            while (!codec.haveOutput) { codec.PumpEvents(10); if (GetTickCount64() >= until) Check(HRESULT_FROM_WIN32(WAIT_TIMEOUT)); }
            --codec.haveOutput;
        }
        return Output(codec, changes + 1);
    }
    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return nullptr;
    Check(hr); return result;
}
void Submit(Codec& codec, IMFSample* sample) {
    if (codec.asynchronous) {
        ULONGLONG until = GetTickCount64() + 2000;
        while (!codec.needInput) { codec.PumpEvents(10); if (GetTickCount64() >= until) Check(HRESULT_FROM_WIN32(WAIT_TIMEOUT)); }
        --codec.needInput;
    }
    Check(codec.transform->ProcessInput(codec.inputId, sample, 0));
}
ComPtr<IMFSample> Receive(Codec& codec) {
    if (codec.asynchronous) {
        ULONGLONG until = GetTickCount64() + 2000;
        while (!codec.haveOutput) { codec.PumpEvents(10); if (GetTickCount64() >= until) Check(HRESULT_FROM_WIN32(WAIT_TIMEOUT)); }
        --codec.haveOutput;
    }
    return Output(codec);
}
void ToNv12(const BYTE* source, int stride, UINT width, UINT height, BYTE* output) {
    BYTE* chroma = output + size_t(width) * height;
    for (UINT y = 0; y < height; y += 2) for (UINT x = 0; x < width; x += 2) {
        int sumR = 0, sumG = 0, sumB = 0;
        for (UINT dy = 0; dy < 2; ++dy) for (UINT dx = 0; dx < 2; ++dx) {
            const BYTE* pixel = source + size_t(y + dy) * stride + size_t(x + dx) * 4;
            int b = pixel[0], g = pixel[1], r = pixel[2]; sumR += r; sumG += g; sumB += b;
            output[size_t(y + dy) * width + x + dx] = Clamp(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
        }
        size_t offset = size_t(y / 2) * width + x;
        chroma[offset] = Clamp(((-26 * sumR - 87 * sumG + 112 * sumB + 512) >> 10) + 128);
        chroma[offset + 1] = Clamp(((112 * sumR - 102 * sumG - 10 * sumB + 512) >> 10) + 128);
    }
}
void FromNv12(const BYTE* source, LONG stride, UINT sourceHeight, UINT width, UINT height, BYTE* target, int targetStride) {
    const BYTE* chroma = source + size_t(stride) * sourceHeight;
    for (UINT y = 0; y < height; ++y) for (UINT x = 0; x < width; ++x) {
        int c = source[size_t(y) * stride + x] - 16;
        const BYTE* uv = chroma + size_t(y / 2) * stride + (x & ~1U);
        int d = uv[0] - 128, e = uv[1] - 128;
        BYTE* pixel = target + size_t(y) * targetStride + size_t(x) * 4;
        pixel[0] = Clamp((298 * c + 541 * d + 128) >> 8);
        pixel[1] = Clamp((298 * c - 55 * d - 136 * e + 128) >> 8);
        pixel[2] = Clamp((298 * c + 459 * e + 128) >> 8); pixel[3] = 255;
    }
}
}

extern "C" __declspec(dllexport) HRESULT __cdecl lume_video_encoder_create(int width, int height, int fps, int bitrateKbps, int hardwareMode, void** result) noexcept {
    if (!result) return E_POINTER; *result = nullptr;
    if (!ValidSize(width, height) || fps < 1 || fps > 240 || bitrateKbps < 250 || bitrateKbps > 100000 || hardwareMode < 0 || hardwareMode > 2) return E_INVALIDARG;
    try {
        Runtime enumeration;
        if (hardwareMode != 2) {
            IMFActivate** devices = nullptr; UINT32 count = 0;
            MFT_REGISTER_TYPE_INFO input = {MFMediaType_Video, MFVideoFormat_NV12}, output = {MFMediaType_Video, MFVideoFormat_H264};
            HRESULT enumerate = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, &input, &output, &devices, &count);
            std::unique_ptr<Codec> chosen;
            if (SUCCEEDED(enumerate)) for (UINT32 i = 0; i < count; ++i) {
                if (!chosen) try {
                    auto codec = std::make_unique<Codec>(); codec->width = width; codec->height = height; codec->fps = fps; codec->encoder = true; codec->hardware = true; codec->activation = devices[i];
                    Check(devices[i]->ActivateObject(IID_PPV_ARGS(&codec->transform)));
                    devices[i]->GetString(MFT_FRIENDLY_NAME_Attribute, codec->name, ARRAYSIZE(codec->name), nullptr);
                    ConfigureEncoder(*codec, bitrateKbps * 1000U); chosen = std::move(codec);
                } catch (...) { }
                devices[i]->Release();
            }
            CoTaskMemFree(devices);
            if (chosen) { *result = chosen.release(); return S_OK; }
            if (hardwareMode == 1) return MF_E_TOPO_CODEC_NOT_FOUND;
        }
        auto codec = std::make_unique<Codec>(); codec->width = width; codec->height = height; codec->fps = fps; codec->encoder = true;
        Check(CoCreateInstance(CLSID_CMSH264EncoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&codec->transform)));
        wcscpy_s(codec->name, L"Windows software H.264"); ConfigureEncoder(*codec, bitrateKbps * 1000U);
        *result = codec.release(); return S_OK;
    } catch (Failure error) { return error.code; } catch (...) { return E_OUTOFMEMORY; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl lume_video_decoder_create(int width, int height, void** result) noexcept {
    if (!result) return E_POINTER; *result = nullptr;
    if (!ValidSize(width, height)) return E_INVALIDARG;
    try {
        auto codec = std::make_unique<Codec>(); codec->width = width; codec->height = height; codec->fps = 60;
        Check(CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&codec->transform)));
        codec->InitializeTransform(); codec->Property(CODECAPI_AVLowLatencyMode, 1);
        auto input = Type(MFVideoFormat_H264, width, height, 60);
        Check(codec->transform->SetInputType(codec->inputId, input.Get(), 0)); DecoderOutputType(*codec); Start(*codec);
        *result = codec.release(); return S_OK;
    } catch (Failure error) { return error.code; } catch (...) { return E_OUTOFMEMORY; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl lume_video_encode(void* handle, const BYTE* bgra, int stride, int keyframe, const BYTE** data, int* length) noexcept {
    Codec* codec = static_cast<Codec*>(handle);
    if (!codec || !codec->encoder || !bgra || !data || !length || stride < int(codec->width * 4)) return E_INVALIDARG;
    *data = nullptr; *length = 0;
    try {
        std::vector<BYTE> nv12(size_t(codec->width) * codec->height * 3 / 2);
        ToNv12(bgra, stride, codec->width, codec->height, nv12.data());
        auto sample = Sample(nv12.data(), static_cast<DWORD>(nv12.size()));
        Check(sample->SetSampleTime(codec->timestamp)); Check(sample->SetSampleDuration(10000000LL / codec->fps)); codec->timestamp += 10000000LL / codec->fps;
        if (keyframe) codec->Property(CODECAPI_AVEncVideoForceKeyFrame, 1);
        Submit(*codec, sample.Get()); auto encoded = Receive(*codec); if (!encoded) return S_FALSE;
        ComPtr<IMFMediaBuffer> buffer; Check(encoded->ConvertToContiguousBuffer(&buffer));
        BYTE* bytes = nullptr; DWORD size = 0; Check(buffer->Lock(&bytes, nullptr, &size));
        if (size < 4 || size > MaxCompressed) { buffer->Unlock(); return MF_E_BUFFERTOOSMALL; }
        try { codec->output.assign(bytes, bytes + size); } catch (...) { buffer->Unlock(); throw; }
        Check(buffer->Unlock()); *data = codec->output.data(); *length = static_cast<int>(codec->output.size()); return S_OK;
    } catch (Failure error) { return error.code; } catch (...) { return E_OUTOFMEMORY; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl lume_video_decode(void* handle, const BYTE* encoded, int length, BYTE* bgra, int stride) noexcept {
    Codec* codec = static_cast<Codec*>(handle);
    if (!codec || codec->encoder || !encoded || !bgra || length < 4 || length > int(MaxCompressed) || stride < int(codec->width * 4)) return E_INVALIDARG;
    try {
        auto sample = Sample(encoded, length); Check(sample->SetSampleTime(codec->timestamp)); Check(sample->SetSampleDuration(166667)); codec->timestamp += 166667;
        Submit(*codec, sample.Get()); auto decoded = Receive(*codec); if (!decoded) return S_FALSE;
        ComPtr<IMFMediaBuffer> buffer; Check(decoded->ConvertToContiguousBuffer(&buffer));
        BYTE* bytes = nullptr; DWORD size = 0; Check(buffer->Lock(&bytes, nullptr, &size));
        size_t required = size_t(codec->outputStride) * codec->outputHeight * 3 / 2;
        if (required > size) { buffer->Unlock(); return MF_E_BUFFERTOOSMALL; }
        FromNv12(bytes, codec->outputStride, codec->outputHeight, codec->width, codec->height, bgra, stride); Check(buffer->Unlock()); return S_OK;
    } catch (Failure error) { return error.code; } catch (...) { return E_OUTOFMEMORY; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl lume_video_info(void* handle, int* hardware, wchar_t* name, int capacity) noexcept {
    Codec* codec = static_cast<Codec*>(handle);
    if (!codec || !hardware || !name || capacity < 1) return E_INVALIDARG;
    *hardware = codec->hardware ? 1 : 0; wcsncpy_s(name, capacity, codec->name, _TRUNCATE); return S_OK;
}
extern "C" __declspec(dllexport) void __cdecl lume_video_destroy(void* handle) noexcept { delete static_cast<Codec*>(handle); }
#include "media.cpp"
