// Original WASAPI and Media Foundation session media bridge. Included by video.cpp.
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <mfreadwrite.h>
namespace SessionMedia {
using Microsoft::WRL::ComPtr;
struct Error { HRESULT hr; };
void Require(HRESULT hr) { if (FAILED(hr)) throw Error{hr}; }
struct Environment {
    bool com = false, mf = false;
    Environment(bool media) {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED); com = SUCCEEDED(hr);
        if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) Require(hr);
        if (media) { hr = MFStartup(MF_VERSION); if (FAILED(hr)) { if (com) CoUninitialize(); Require(hr); } mf = true; }
    }
    ~Environment() { if (mf) MFShutdown(); if (com) CoUninitialize(); }
};
struct Audio {
    Environment env{false};
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> capture;
    ComPtr<IAudioRenderClient> render;
    UINT capacity = 0;
    std::vector<BYTE> pending;
    ~Audio() { if (client) client->Stop(); capture.Reset(); render.Reset(); client.Reset(); }
    void Open(int mode) {
        bool playback = mode == 1, microphone = mode == 2;
        ComPtr<IMMDeviceEnumerator> devices; ComPtr<IMMDevice> device;
        Require(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&devices)));
        Require(devices->GetDefaultAudioEndpoint(microphone ? eCapture : eRender, microphone ? eCommunications : eConsole, &device));
        Require(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.GetAddressOf())));
        WAVEFORMATEX format{}; format.wFormatTag = WAVE_FORMAT_PCM; format.nChannels = 2; format.nSamplesPerSec = 48000;
        format.wBitsPerSample = 16; format.nBlockAlign = 4; format.nAvgBytesPerSec = 192000;
        DWORD flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        if (!playback && !microphone) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
        Require(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 1000000, 0, &format, nullptr));
        Require(client->GetBufferSize(&capacity));
        if (playback) Require(client->GetService(IID_PPV_ARGS(&render)));
        else Require(client->GetService(IID_PPV_ARGS(&capture)));
        Require(client->Start());
    }
};
struct Recorder {
    Environment env{true};
    ComPtr<IMFSinkWriter> sink;
    std::unique_ptr<Codec> encoder;
    UINT width = 0, height = 0, fps = 0;
    DWORD stream = 0;
    DWORD sound = 0;
    bool audio = false;
    LONGLONG audioEnd = 0;
    LONGLONG last = -1;
    ~Recorder() { sink.Reset(); encoder.reset(); }
    void Open(const wchar_t* path, UINT w, UINT h, UINT rate, bool includeAudio = false) {
        width = w; height = h; fps = rate; audio = includeAudio;
        ComPtr<IMFAttributes> attrs; Require(MFCreateAttributes(&attrs, 3));
        Require(attrs->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4));
        attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
        attrs->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
        Require(MFCreateSinkWriterFromURL(path, nullptr, attrs.Get(), &sink));
        // Encode every submitted image explicitly. Sink-writer RGB conversion can
        // thin a high-rate sequence to its encoder's nominal frame rate.
        void* codec = nullptr;
        Require(lume_video_encoder_create(w, h, std::min<UINT>(rate, 60), std::min<UINT>(30000, std::max<UINT>(1500, w * h * 4 / 1000)), 0, &codec));
        encoder.reset(static_cast<Codec*>(codec));
        ComPtr<IMFMediaType> output; Require(encoder->transform->GetOutputCurrentType(encoder->outputId, &output));
        Require(MFSetAttributeRatio(output.Get(), MF_MT_FRAME_RATE, rate, 1));
        Require(sink->AddStream(output.Get(), &stream));
        // Matching H.264 input/output types request packet passthrough, not encoding.
        Require(sink->SetInputMediaType(stream, output.Get(), nullptr));
        if (audio) {
            ComPtr<IMFMediaType> encoded; Require(MFCreateMediaType(&encoded));
            Require(encoded->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio)); Require(encoded->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC));
            Require(encoded->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2)); Require(encoded->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000));
            Require(encoded->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16)); Require(encoded->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 20000));
            Require(encoded->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0)); Require(sink->AddStream(encoded.Get(), &sound));
            ComPtr<IMFMediaType> pcm; Require(MFCreateMediaType(&pcm)); Require(pcm->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
            Require(pcm->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM)); Require(pcm->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2));
            Require(pcm->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000)); Require(pcm->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
            Require(pcm->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4)); Require(pcm->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 192000));
            Require(sink->SetInputMediaType(sound, pcm.Get(), nullptr));
        }
        Require(sink->BeginWriting());
    }
    void Frame(const BYTE* pixels, int stride, LONGLONG time) {
        if (time <= last || time < 0 || stride < static_cast<int>(width * 4)) Require(E_INVALIDARG);
        try {
            std::vector<BYTE> nv12(size_t(width) * height * 3 / 2);
            ToNv12(pixels, stride, width, height, nv12.data());
            auto input = Sample(nv12.data(), static_cast<DWORD>(nv12.size()));
            Check(input->SetSampleTime(encoder->timestamp)); Check(input->SetSampleDuration(10000000LL / encoder->fps));
            encoder->timestamp += 10000000LL / encoder->fps;
            Submit(*encoder, input.Get()); auto encoded = Receive(*encoder);
            // Low-latency, no-B-frame encoders must return each image. Surface a
            // driver failure instead of silently saving fewer images than requested.
            if (!encoded) Require(MF_E_TRANSFORM_NEED_MORE_INPUT);
            Require(encoded->SetSampleTime(time)); Require(encoded->SetSampleDuration(10000000LL / fps));
            Require(encoded->SetUINT64(MFSampleExtension_DecodeTimestamp, time));
            Require(sink->WriteSample(stream, encoded.Get())); last = time;
        } catch (Failure error) { Require(error.code); }
    }
    void AudioFrame(const BYTE* pcm, UINT count, LONGLONG time) {
        if (!audio || !pcm || count < 4 || count > 19200 || count % 4 || time < audioEnd) Require(E_INVALIDARG);
        ComPtr<IMFMediaBuffer> buffer; Require(MFCreateMemoryBuffer(count, &buffer)); BYTE* data = nullptr;
        Require(buffer->Lock(&data, nullptr, nullptr)); std::memcpy(data, pcm, count); buffer->Unlock(); Require(buffer->SetCurrentLength(count));
        ComPtr<IMFSample> sample; Require(MFCreateSample(&sample)); Require(sample->AddBuffer(buffer.Get()));
        LONGLONG duration = static_cast<LONGLONG>(count / 4) * 10000000 / 48000;
        Require(sample->SetSampleTime(time)); Require(sample->SetSampleDuration(duration)); Require(sink->WriteSample(sound, sample.Get())); audioEnd = time + duration;
    }
};
}
extern "C" __declspec(dllexport) int __cdecl LumeMediaVersion() { return 2; }
extern "C" __declspec(dllexport) HRESULT __cdecl LumeAudioOpen(int playback, void** result) {
    if (!result || playback < 0 || playback > 2) return E_INVALIDARG; *result = nullptr;
    try { auto audio = std::make_unique<SessionMedia::Audio>(); audio->Open(playback); *result = audio.release(); return S_OK; }
    catch (SessionMedia::Error e) { return e.hr; } catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl LumeAudioRead(void* handle, BYTE* destination, UINT bytes, UINT* written) {
    if (!handle || !destination || !written || bytes < 4 || bytes > 192000 || bytes % 4) return E_INVALIDARG; *written = 0;
    auto audio = static_cast<SessionMedia::Audio*>(handle); if (!audio->capture) return E_INVALIDARG;
    try {
        if (!audio->pending.empty()) { UINT count = std::min<UINT>(bytes, static_cast<UINT>(audio->pending.size())); std::memcpy(destination, audio->pending.data(), count); *written = count; audio->pending.erase(audio->pending.begin(), audio->pending.begin() + count); if (*written == bytes) return S_OK; }
        UINT available = 0; SessionMedia::Require(audio->capture->GetNextPacketSize(&available));
        while (available) {
            BYTE* data = nullptr; UINT frames = 0; DWORD flags = 0;
            SessionMedia::Require(audio->capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr));
            if (frames > 48000) { audio->capture->ReleaseBuffer(frames); return E_INVALIDARG; }
            UINT count = std::min(frames * 4, bytes - *written);
            if (count) { if (flags & AUDCLNT_BUFFERFLAGS_SILENT) std::memset(destination + *written, 0, count); else std::memcpy(destination + *written, data, count); *written += count; }
            if (count < frames * 4) { audio->pending.resize(frames * 4 - count); if (flags & AUDCLNT_BUFFERFLAGS_SILENT) std::memset(audio->pending.data(), 0, audio->pending.size()); else std::memcpy(audio->pending.data(), data + count, audio->pending.size()); }
            audio->capture->ReleaseBuffer(frames); if (*written == bytes) break;
            SessionMedia::Require(audio->capture->GetNextPacketSize(&available));
        }
        return S_OK;
    } catch (SessionMedia::Error e) { return e.hr; } catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl LumeAudioWrite(void* handle, const BYTE* source, UINT bytes, UINT* written) {
    if (!handle || !source || !written || bytes > 192000 || bytes % 4) return E_INVALIDARG; *written = 0;
    auto audio = static_cast<SessionMedia::Audio*>(handle); if (!audio->render) return E_INVALIDARG;
    try {
        UINT padding = 0; SessionMedia::Require(audio->client->GetCurrentPadding(&padding));
        UINT frames = std::min(bytes / 4, audio->capacity - std::min(audio->capacity, padding));
        if (frames) { BYTE* data = nullptr; SessionMedia::Require(audio->render->GetBuffer(frames, &data)); std::memcpy(data, source, frames * 4); SessionMedia::Require(audio->render->ReleaseBuffer(frames, 0)); *written = frames * 4; }
        return S_OK;
    } catch (SessionMedia::Error e) { return e.hr; } catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) void __cdecl LumeAudioClose(void* handle) { delete static_cast<SessionMedia::Audio*>(handle); }
extern "C" __declspec(dllexport) HRESULT __cdecl LumeRecordOpen(const wchar_t* path, UINT width, UINT height, UINT fps, void** result) {
    if (!path || !result || width < 2 || height < 2 || width > 4096 || height > 2160 || (width & 1) || (height & 1) || fps < 1 || fps > 1000) return E_INVALIDARG; *result = nullptr;
    try { auto record = std::make_unique<SessionMedia::Recorder>(); record->Open(path, width, height, fps); *result = record.release(); return S_OK; }
    catch (SessionMedia::Error e) { return e.hr; } catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl LumeRecordFrame(void* handle, const BYTE* pixels, int stride, LONGLONG time) {
    if (!handle || !pixels) return E_INVALIDARG;
    try { static_cast<SessionMedia::Recorder*>(handle)->Frame(pixels, stride, time); return S_OK; }
    catch (SessionMedia::Error e) { return e.hr; } catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl LumeRecordClose(void* handle) {
    if (!handle) return E_INVALIDARG; std::unique_ptr<SessionMedia::Recorder> record(static_cast<SessionMedia::Recorder*>(handle));
    return record->sink->Finalize();
}
extern "C" __declspec(dllexport) HRESULT __cdecl LumeRecordOpenAudio(const wchar_t* path, UINT width, UINT height, UINT fps, void** result) {
    if (!path || !result || width < 2 || height < 2 || width > 4096 || height > 2160 || (width & 1) || (height & 1) || fps < 1 || fps > 1000) return E_INVALIDARG; *result = nullptr;
    try { auto record = std::make_unique<SessionMedia::Recorder>(); record->Open(path, width, height, fps, true); *result = record.release(); return S_OK; }
    catch (SessionMedia::Error e) { return e.hr; } catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl LumeRecordAudio(void* handle, const BYTE* pcm, UINT count, LONGLONG time) {
    if (!handle) return E_INVALIDARG;
    try { static_cast<SessionMedia::Recorder*>(handle)->AudioFrame(pcm, count, time); return S_OK; }
    catch (SessionMedia::Error e) { return e.hr; } catch (...) { return E_FAIL; }
}
