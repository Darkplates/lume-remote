import AVFoundation
import Foundation

struct RemoteMedia: Decodable {
    var audio = false; var voice = false; var audio_pending = false; var voice_pending = false
    var audio_generation: Int32 = 0; var voice_generation: Int32 = 0; var status = ""
}
struct RemoteRecording: Decodable { var active = false; var path = ""; var frames: UInt64 = 0; var message = "" }
private struct MediaSnapshot: Decodable { let connected: Bool; let media: RemoteMedia }

private enum AudioEnvironment {
    static let lock = NSLock()
    static var users = 0
    static func acquire() throws {
        lock.lock(); defer { lock.unlock() }
        if users == 0 { let audio = AVAudioSession.sharedInstance(); try audio.setCategory(.playAndRecord, mode: .voiceChat, options: [.defaultToSpeaker, .allowBluetooth, .mixWithOthers]); try audio.setPreferredSampleRate(48000); try audio.setActive(true) }
        users += 1
    }
    static func release() { lock.lock(); defer { lock.unlock() }; users = max(0, users - 1); if users == 0 { try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation) } }
}
/// A separate audio timer keeps device queues independent of image presentation.
final class SessionAudio {
    private let id: UInt64
    private let queue = DispatchQueue(label: "com.lume.audio", qos: .userInitiated)
    private var timer: DispatchSourceTimer?
    private var engine: AVAudioEngine?
    private var players: [UInt8: AVAudioPlayerNode] = [:]
    private var budget: [UInt8: DispatchSemaphore] = [:]
    private var tap = false
    private var consent = false
    private var priorGeneration: Int32 = 0
    private var audioGeneration: Int32 = 0
    private var voiceGeneration: Int32 = 0
    private var bytes = [UInt8](repeating: 0, count: 19200)
    private var stateBytes = [UInt8](repeating: 0, count: 16384)
    private let format = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
    private var observer: NSObjectProtocol?
    private var nextPoll: TimeInterval = 0
    init(_ id: UInt64) {
        self.id = id
        let t = DispatchSource.makeTimerSource(queue: queue); t.schedule(deadline: .now(), repeating: .milliseconds(20)); t.setEventHandler { [weak self] in self?.poll() }; timer = t; t.resume()
        observer = NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: nil) { [weak self] _ in self?.pause() }
    }
    func allow(previous: Int32) { queue.async { self.priorGeneration = previous; self.consent = true } }
    func pause() { queue.async { self.consent = false; self.stop(); _ = Bridge.action(self.id, ["type": "voice", "enabled": false]); _ = Bridge.action(self.id, ["type": "audio", "enabled": false]) } }
    func close() { timer?.cancel(); timer = nil; queue.async { self.consent = false; self.stop() } }
    private func stop() { if let e = engine { if tap { e.inputNode.removeTap(onBus: 0) }; tap = false; players.values.forEach { $0.stop() }; e.stop(); engine = nil; players.removeAll(); budget.removeAll(); AudioEnvironment.release() } }
    private func poll() {
        let now = ProcessInfo.processInfo.systemUptime; if now < nextPoll { return }
        let count = stateBytes.withUnsafeMutableBufferPointer { lume_media(id, $0.baseAddress, $0.count) }
        guard count > 0, count <= stateBytes.count, let state = try? JSONDecoder().decode(MediaSnapshot.self, from: Data(stateBytes.prefix(count))) else { stop(); nextPoll = now + 0.25; return }
        let m = state.media
        let audio = state.connected && m.audio
        let voice = state.connected && m.voice && consent && m.voice_generation != priorGeneration && AVAudioSession.sharedInstance().recordPermission == .granted
        if !audio && !voice { stop(); nextPoll = now + 0.25; return }; nextPoll = 0
        if audioGeneration != m.audio_generation || voiceGeneration != m.voice_generation || (tap != voice) || ((players[20] != nil) != audio) { stop() }
        audioGeneration = m.audio_generation; voiceGeneration = m.voice_generation
        do {
            if engine == nil {
                try AudioEnvironment.acquire(); let e = AVAudioEngine(); engine = e
                for kind: UInt8 in [20, 21] where kind == 20 ? audio : voice { let p = AVAudioPlayerNode(); e.attach(p); e.connect(p, to: e.mainMixerNode, format: format); players[kind] = p; budget[kind] = DispatchSemaphore(value: 4) }
                if voice {
                    let input = e.inputNode; let inputFormat = input.outputFormat(forBus: 0)
                    guard inputFormat.sampleRate > 0, let converter = AVAudioConverter(from: inputFormat, to: format) else { throw BridgeFailure.message("Microphone format unavailable") }
                    let handle = id, generation = m.voice_generation, target = format
                    input.installTap(onBus: 0, bufferSize: 960, format: inputFormat) { buffer, _ in
                        let size = AVAudioFrameCount(ceil(Double(buffer.frameLength) * 48000 / inputFormat.sampleRate)) + 16
                        guard size <= 4800, let converted = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: size) else { return }
                        var consumed = false; var error: NSError?
                        let result = converter.convert(to: converted, error: &error) { _, status in if consumed { status.pointee = .noDataNow; return nil }; consumed = true; status.pointee = .haveData; return buffer }
                        guard result != .error, error == nil, let samples = converted.floatChannelData else { return }
                        var pcm = [UInt8](); pcm.reserveCapacity(Int(converted.frameLength) * 4)
                        for i in 0..<Int(converted.frameLength) { for channel in 0..<2 { let f = samples[channel][i]; let n = Int16(max(-32768, min(32767, (f.isFinite ? f : 0) * 32767))); let bits = UInt16(bitPattern: n); pcm.append(UInt8(bits & 255)); pcm.append(UInt8(bits >> 8)) } }
                        _ = pcm.withUnsafeBufferPointer { lume_microphone(handle, generation, $0.baseAddress, $0.count) }
                    }; tap = true
                }
                try e.start(); players.values.forEach { $0.play() }
            }
            for (kind, player) in players { for _ in 0..<4 {
                var generation: Int32 = 0
                let n = bytes.withUnsafeMutableBufferPointer { lume_audio(id, kind, &generation, $0.baseAddress, $0.count) }
                if n == 0 { break }; guard n <= 19200, n % 4 == 0, generation == (kind == 20 ? audioGeneration : voiceGeneration), let limit = budget[kind], limit.wait(timeout: .now()) == .success else { continue }
                guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(n / 4)), let channels = buffer.floatChannelData else { limit.signal(); continue }; buffer.frameLength = AVAudioFrameCount(n / 4)
                for frame in 0..<(n / 4) { for channel in 0..<2 { let i = frame * 4 + channel * 2; channels[channel][frame] = Float(Int16(bitPattern: UInt16(bytes[i]) | UInt16(bytes[i + 1]) << 8)) / 32768 } }
                player.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack) { _ in limit.signal() }
            } }
        } catch { consent = false; stop(); _ = Bridge.action(id, ["type": "voice", "enabled": false]); _ = Bridge.action(id, ["type": "audio", "enabled": false]) }
    }
    deinit { timer?.cancel(); if let observer { NotificationCenter.default.removeObserver(observer) }; stop() }
}
