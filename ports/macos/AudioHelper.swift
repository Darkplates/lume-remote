// Built into Lume.app. PCM16 stereo at 48 kHz over owned pipes; no network access.
import AVFoundation
import ScreenCaptureKit
import CoreMedia
import Foundation

let target = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
final class PcmWriter {
    private let queue = DispatchQueue(label: "com.lume.audio.output")
    private let budget = DispatchSemaphore(value: 4)
    private var converter: AVAudioConverter?
    private var source: AVAudioFormat?
    func write(_ input: AVAudioPCMBuffer) {
        if source != input.format { source = input.format; converter = AVAudioConverter(from: input.format, to: target) }
        guard let converter, input.format.sampleRate > 0 else { return }
        let capacity = AVAudioFrameCount(ceil(Double(input.frameLength) * 48000 / input.format.sampleRate)) + 16
        guard capacity <= 96000, let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return }
        var used = false; var error: NSError?
        let result = converter.convert(to: output, error: &error) { _, state in if used { state.pointee = .noDataNow; return nil }; used = true; state.pointee = .haveData; return input }
        guard result != .error, error == nil, let samples = output.floatChannelData else { return }
        for offset in stride(from: 0, to: Int(output.frameLength), by: 960) {
            var data = Data(); let count = min(960, Int(output.frameLength) - offset); data.reserveCapacity(count * 4)
            for i in offset..<(offset + count) { for channel in 0..<2 { let value = samples[channel][i]; let signed = Int16(max(-32768, min(32767, (value.isFinite ? value : 0) * 32767))); let v = UInt16(bitPattern: signed); data.append(UInt8(v & 255)); data.append(UInt8(v >> 8)) } }
            guard budget.wait(timeout: .now()) == .success else { continue }
            queue.async { [self] in defer { budget.signal() }; do { try FileHandle.standardOutput.write(contentsOf: data) } catch { exit(2) } }
        }
    }
}
final class SystemAudio: NSObject, SCStreamOutput, SCStreamDelegate {
    private var stream: SCStream?
    private let writer = PcmWriter()
    func start() async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { throw NSError(domain: "Lume", code: 1) }
        let config = SCStreamConfiguration(); config.capturesAudio = true; config.excludesCurrentProcessAudio = true; config.sampleRate = 48000; config.channelCount = 2
        config.width = 2; config.height = 2; config.minimumFrameInterval = CMTime(value: 1, timescale: 1); config.queueDepth = 3
        let s = SCStream(filter: SCContentFilter(display: display, excludingWindows: []), configuration: config, delegate: self)
        try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: DispatchQueue(label: "com.lume.system.audio")); stream = s; try await s.startCapture()
    }
    func stream(_ stream: SCStream, didStopWithError error: Error) { exit(2) }
    func stream(_ stream: SCStream, didOutputSampleBuffer sample: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sample.isValid, let description = sample.formatDescription else { return }
        let format = AVAudioFormat(cmAudioFormatDescription: description)
        let count = CMSampleBufferGetNumSamples(sample)
        guard count > 0, count <= 96000, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(count)) else { return }; buffer.frameLength = AVAudioFrameCount(count)
        guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sample, at: 0, frameCount: Int32(count), into: buffer.mutableAudioBufferList) == noErr else { return }; writer.write(buffer)
    }
}
let engine = AVAudioEngine()
var systemAudio: SystemAudio?
let writer = PcmWriter()
let mode = CommandLine.arguments.dropFirst().first ?? ""
if mode == "play" {
    let player = AVAudioPlayerNode(); engine.attach(player); engine.connect(player, to: engine.mainMixerNode, format: target)
    do { try engine.start(); player.play() } catch { exit(2) }
    DispatchQueue.global(qos: .userInitiated).async {
        let slots = DispatchSemaphore(value: 4)
        while true {
            var data = Data(); do { while data.count < 3840 { guard let part = try FileHandle.standardInput.read(upToCount: 3840 - data.count), !part.isEmpty else { break }; data.append(part) } } catch { exit(2) }
            if data.isEmpty { exit(0) }; guard data.count % 4 == 0 else { exit(2) }
            slots.wait(); let count = data.count / 4
            guard let buffer = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: AVAudioFrameCount(count)), let samples = buffer.floatChannelData else { exit(2) }; buffer.frameLength = AVAudioFrameCount(count)
            let bytes = [UInt8](data); for frame in 0..<count { for channel in 0..<2 { let i = frame * 4 + channel * 2; samples[channel][frame] = Float(Int16(bitPattern: UInt16(bytes[i]) | UInt16(bytes[i + 1]) << 8)) / 32768 } }
            player.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack) { _ in slots.signal() }
        }
    }
} else if mode == "microphone" {
    AVCaptureDevice.requestAccess(for: .audio) { allowed in
        guard allowed else { exit(3) }; let node = engine.inputNode; let format = node.outputFormat(forBus: 0)
        guard format.sampleRate > 0 else { exit(2) }; node.installTap(onBus: 0, bufferSize: 960, format: format) { buffer, _ in writer.write(buffer) }
        do { try engine.start() } catch { exit(2) }
    }
} else if mode == "system" {
    let capture = SystemAudio(); systemAudio = capture
    Task { do { try await capture.start() } catch { exit(3) } }
} else { exit(1) }
RunLoop.main.run()
