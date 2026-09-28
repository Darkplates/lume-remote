import Foundation
import CoreGraphics
import Combine
import AVFoundation
import UIKit

struct RemoteState: Decodable {
    var media = RemoteMedia()
    var recording = RemoteRecording()
    var reconnecting = false
    var reconnect_attempts: UInt32 = 0
    var connected = false
    var status = "Connecting…"
    var peer = ""
    var control = false
    var input_ready = false
    var monitors: [RemoteMonitor] = []
    var monitor_pending = false
    var monitor_status = ""
    var capabilities: UInt64 = 0
    var width: UInt32 = 0
    var height: UInt32 = 0
    var refresh: UInt32 = 0
    var epoch: Int32 = 0
    var sequence: Int32 = 0
    var reply: String?
    var clipboard: String?
    var pair_ready = false
    var files_allowed = false
    var files = RemoteFiles()
}
struct RemoteFileEntry: Decodable { var name: String; var directory: Bool; var length: UInt64 }
struct RemoteMonitor: Decodable, Identifiable {
    var id: String; var name: String; var x: Int32; var y: Int32
    var width: UInt32; var height: UInt32; var refresh: UInt32; var selected: Bool
}
struct RemoteFiles: Decodable {
    var resumable = false; var resumed_bytes: UInt64 = 0
    var operation: UInt64 = 0
    var path = ""; var page = 0; var more = false; var entries: [RemoteFileEntry] = []; var listing = false
    var active = false; var name = ""; var direction = ""; var bytes: UInt64 = 0; var total: UInt64 = 0
    var status = ""; var completed: String?
    var completed_directory = false; var folder_job = false; var items_done: UInt64 = 0; var bytes_done: UInt64 = 0
}
enum BridgeFailure: Error, LocalizedError {
    case message(String)
    var errorDescription: String? { if case .message(let text) = self { return text }; return nil }
}
enum Bridge {
    static func pairing(_ handle: UInt64) throws -> SavedComputer {
        var bytes = [UInt8](repeating: 0, count: 8192)
        defer { bytes.withUnsafeMutableBytes { $0.initializeMemory(as: UInt8.self, repeating: 0) } }
        let count = bytes.withUnsafeMutableBufferPointer { lume_pairing(handle, $0.baseAddress, $0.count) }
        guard count > 0, count <= bytes.count else { throw BridgeFailure.message(error()) }
        return try JSONDecoder().decode(SavedComputer.self, from: Data(bytes.prefix(count)))
    }
    static func error() -> String {
        var bytes = [UInt8](repeating: 0, count: 8192)
        let count = bytes.withUnsafeMutableBufferPointer { lume_error($0.baseAddress, $0.count) }
        return String(decoding: bytes.prefix(min(count, bytes.count)), as: UTF8.self)
    }
    static func open(_ invitation: String) throws -> UInt64 {
        let text = Array(invitation.utf8)
        let path = Array(((Bundle.main.privateFrameworksPath ?? "") + "/LumePeer.framework/LumePeer").utf8)
        let handle = text.withUnsafeBufferPointer { a in path.withUnsafeBufferPointer { b in lume_open(a.baseAddress, a.count, b.baseAddress, b.count) } }
        guard handle != 0 else { throw BridgeFailure.message(error()) }
        return handle
    }
    static func action(_ handle: UInt64, _ action: [String: Any]) -> Bool {
        guard let data = try? JSONSerialization.data(withJSONObject: action) else { return false }
        return data.withUnsafeBytes { lume_action(handle, $0.bindMemory(to: UInt8.self).baseAddress, $0.count) }
    }
}
struct PresentedFrame { let image: CGImage; let epoch: Int32 }
private final class DisplayLinkTarget: NSObject {
    weak var owner: RemoteSession?
    @objc func frame(_ link: CADisplayLink) { owner?.requestPoll(display: true) }
}
final class RemoteSession: ObservableObject, Identifiable {
    private var audio: SessionAudio?
    let id: UInt64
    @Published private(set) var state = RemoteState()
    @Published private(set) var frame: PresentedFrame?
    @Published var error = ""
    @Published private(set) var preparingFiles = false
    private let queue = DispatchQueue(label: "com.lume.frame", qos: .userInitiated)
    private let fileQueue = DispatchQueue(label: "com.lume.files", qos: .utility)
    private let transferDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("lume-transfer-" + UUID().uuidString, isDirectory: true)
    private let gate = NSLock()
    private var stopped = false
    private var fileCancelled = false
    private var stagedUpload: URL?
    private var stagedOperation: UInt64 = 0
    private var pendingPresentation = false
    private var viewing = false
    private var timer: DispatchSourceTimer?
    private var displayLink: CADisplayLink?
    private var sequence: Int32 = 0
    private var lastState: TimeInterval = 0
    private var stateBytes = [UInt8](repeating: 0, count: 4096)
    init(invitation: String) throws {
        id = try Bridge.open(invitation)
        audio = SessionAudio(id)
        let source = DispatchSource.makeTimerSource(queue: queue)
        source.schedule(deadline: .now(), repeating: .milliseconds(200))
        source.setEventHandler { [weak self] in self?.requestPoll(display: false) }
        timer = source; source.resume()
        let target = DisplayLinkTarget(); target.owner = self
        let link = CADisplayLink(target: target, selector: #selector(DisplayLinkTarget.frame(_:)))
        let maximum = Float(UIScreen.main.maximumFramesPerSecond)
        link.preferredFrameRateRange = CAFrameRateRange(minimum: min(30, maximum), maximum: maximum, preferred: maximum)
        link.isPaused = true; link.add(to: .main, forMode: .common); displayLink = link
    }
    fileprivate func requestPoll(display: Bool) {
        gate.lock()
        if stopped || pendingPresentation || (display && !viewing) { gate.unlock(); return }
        pendingPresentation = true; gate.unlock()
        queue.async { [weak self] in self?.poll(display: display) }
    }
    private func poll(display: Bool) {
        gate.lock()
        if stopped { pendingPresentation = false; gate.unlock(); return }
        let display = display && viewing; gate.unlock()
        var state: RemoteState?
        let now = ProcessInfo.processInfo.systemUptime
        if now - lastState >= 0.2 {
            lastState = now
            var count = stateBytes.withUnsafeMutableBufferPointer { lume_state(id, $0.baseAddress, $0.count) }
            if count > stateBytes.count && count <= 2 * 1024 * 1024 {
                stateBytes = [UInt8](repeating: 0, count: 2 * 1024 * 1024)
                count = stateBytes.withUnsafeMutableBufferPointer { lume_state(id, $0.baseAddress, $0.count) }
            }
            if count > 0 && count <= stateBytes.count { state = try? JSONDecoder().decode(RemoteState.self, from: Data(stateBytes.prefix(count))) }
        }
        var info = LumeFrameInfo(width: 0, height: 0, epoch: 0, sequence: 0)
        var picture: CGImage?
        let required = display ? lume_frame(id, sequence, &info, nil, 0) : 0
        var failure = ""
        if required > 0 && required <= 128 * 1024 * 1024 {
            var pixels = Data(count: required)
            let copied = pixels.withUnsafeMutableBytes { lume_frame(id, sequence, &info, $0.bindMemory(to: UInt8.self).baseAddress, $0.count) }
            if copied > 0 && copied <= pixels.count && copied == Int(info.width) * Int(info.height) * 4 {
                let data = pixels.prefix(copied) as CFData
                if let provider = CGDataProvider(data: data) {
                    picture = CGImage(width: Int(info.width), height: Int(info.height), bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: Int(info.width) * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue).union(.byteOrder32Big), provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
                    if picture != nil { sequence = info.sequence }
                }
            }
        } else if required > 0 { failure = "Choose a lower resolution for this device." }
        let presentedEpoch = info.epoch
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.gate.lock(); let closed = self.stopped; self.gate.unlock()
            if !closed {
                if let state {
                    self.state = state
                    self.gate.lock()
                    let completedUpload = state.files.operation > self.stagedOperation && !state.files.active ? self.stagedUpload : nil
                    if completedUpload != nil { self.stagedUpload = nil }
                    self.gate.unlock()
                    if let completedUpload { self.preparingFiles = false; self.fileQueue.async { try? FileManager.default.removeItem(at: completedUpload) } }
                    if !state.connected || (self.frame?.epoch ?? 0) < state.epoch { self.frame = nil }
                }
                if let picture { self.frame = PresentedFrame(image: picture, epoch: presentedEpoch) }
                if !failure.isEmpty { self.error = failure }
            }
            self.gate.lock(); self.pendingPresentation = false; self.gate.unlock()
        }
    }
    func action(_ value: [String: Any]) {
        if value["type"] as? String == "cancel_file" { gate.lock(); fileCancelled = true; gate.unlock() }
        if !Bridge.action(id, value) { error = Bridge.error() } else { error = "" }
    }
    func setViewing(_ value: Bool) { gate.lock(); viewing = value; gate.unlock(); displayLink?.isPaused = !value }
    func input(_ kind: UInt8, _ a: Int32, _ b: Int32 = 0, epoch: Int32? = nil) {
        guard state.input_ready, let frame, frame.epoch == state.epoch,
              epoch == nil || epoch == frame.epoch else { return }
        action(["type": "input", "action": kind, "a": a, "b": b, "epoch": frame.epoch])
    }
    func release() { action(["type": "release"]) }
    func pauseMedia() { audio?.pause(); action(["type": "stop_recording"]) }
    func requestVoice() {
        AVAudioSession.sharedInstance().requestRecordPermission { [weak self] allowed in DispatchQueue.main.async {
            guard let self else { return }; self.gate.lock(); let active = !self.stopped && self.viewing; self.gate.unlock()
            guard allowed && active else { self.error = "Microphone permission is required while Lume is visible."; return }
            self.audio?.allow(previous: self.state.media.voice_generation); self.action(["type": "voice", "enabled": true])
        } }
    }
    static func recordingsFolder() throws -> URL { let folder = try FileManager.default.url(for: .documentDirectory, in: .userDomainMask, appropriateFor: nil, create: true).appendingPathComponent("Recordings", isDirectory: true); try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true); return folder }
    func record(fps: Int = 0) { if state.recording.active { action(["type": "stop_recording"]) } else { do { let url = try Self.recordingsFolder().appendingPathComponent("Lume-" + UUID().uuidString + ".mkv"); action(["type": "record", "path": url.path, "fps": fps]) } catch { self.error = "Unable to create a recording. Check free space." } } }
    func quality(height: Int, fps: Int, lossless: Bool) {
        action(["type": "quality", "height": height, "fps": fps, "jpeg": lossless ? 100 : 80, "lossless": lossless])
    }
    func downloadFolder() throws -> URL {
        if !FileManager.default.fileExists(atPath: transferDirectory.path) { try FileManager.default.createDirectory(at: transferDirectory, withIntermediateDirectories: false) }
        return transferDirectory.resolvingSymlinksInPath()
    }
    func upload(_ url: URL, folder: String) {
        guard !preparingFiles && !state.files.active else { error = "Wait for the current file operation."; return }
        preparingFiles = true; gate.lock(); fileCancelled = false; gate.unlock()
        fileQueue.async { [weak self] in
            guard let self else { return }
            var queued = false
            defer { if !queued { DispatchQueue.main.async { self.preparingFiles = false } } }
            self.gate.lock(); let closed = self.stopped; self.gate.unlock(); if closed { return }
            let access = url.startAccessingSecurityScopedResource()
            defer { if access { url.stopAccessingSecurityScopedResource() } }
            do {
                let local = try self.downloadFolder().appendingPathComponent("upload-" + UUID().uuidString)
                let directory = try url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory == true
                do { try FolderTransfer.copy(url, to: local, cancelled: { self.gate.lock(); defer { self.gate.unlock() }; return self.stopped || self.fileCancelled }) }
                catch { try? FileManager.default.removeItem(at: local); throw error }
                self.gate.lock(); let cancelled = self.stopped || self.fileCancelled; self.gate.unlock()
                if cancelled { try? FileManager.default.removeItem(at: local); return }
                let value: [String: Any] = ["type": directory ? "upload_folder" : "upload_file", "local": local.path, "folder": folder, "name": url.lastPathComponent]
                var bytes = [UInt8](repeating: 0, count: 2 * 1024 * 1024)
                let count = bytes.withUnsafeMutableBufferPointer { lume_state(self.id, $0.baseAddress, $0.count) }
                guard count > 0 && count <= bytes.count else { try? FileManager.default.removeItem(at: local); throw BridgeFailure.message("Session state unavailable.") }
                let current = try JSONDecoder().decode(RemoteState.self, from: Data(bytes.prefix(count)))
                self.gate.lock(); self.stagedOperation = current.files.operation; self.gate.unlock()
                if !Bridge.action(self.id, value) { try? FileManager.default.removeItem(at: local); throw BridgeFailure.message(Bridge.error()) }
                self.gate.lock(); self.stagedUpload = local; self.gate.unlock(); queued = true
            } catch { DispatchQueue.main.async { self.error = "Unable to upload the selected file: " + error.localizedDescription } }
        }
    }
    func verifiedDownload(_ path: String) -> URL? {
        let url = URL(fileURLWithPath: path).resolvingSymlinksInPath()
        return url.deletingLastPathComponent() == transferDirectory.resolvingSymlinksInPath() ? url : nil
    }
    func close() {
        audio?.close(); audio = nil
        gate.lock(); if stopped { gate.unlock(); return }; stopped = true; gate.unlock()
        timer?.cancel(); timer = nil
        displayLink?.invalidate(); displayLink = nil
        let handle = id; _ = lume_cancel(handle)
        let folder = transferDirectory; let files = fileQueue
        queue.async { _ = lume_close(handle); files.async { try? FileManager.default.removeItem(at: folder) } }
    }
    deinit { timer?.cancel(); displayLink?.invalidate(); let handle = id; let folder = transferDirectory; let files = fileQueue; _ = lume_cancel(handle); DispatchQueue.global(qos: .utility).async { _ = lume_close(handle); files.async { try? FileManager.default.removeItem(at: folder) } } }
}
