import SwiftUI
import UIKit

final class Computers: ObservableObject {
    @Published var saved: [SavedComputer] = []
    @Published var sessions: [RemoteSession] = []
    @Published var selected: UInt64?
    @Published var error = ""
    init() { do { saved = try SavedStore.read() } catch { self.error = error.localizedDescription } }
    func savePairing(_ session: RemoteSession) {
        do { let computer = try Bridge.pairing(session.id); var next = saved.filter { $0.id != computer.id }; next.append(computer); try SavedStore.write(next); saved = next; session.action(["type": "clear_pairing"]); close(session); error = "" }
        catch { self.error = error.localizedDescription }
    }
    func forget(_ computer: SavedComputer) { do { let next = saved.filter { $0.id != computer.id }; try SavedStore.write(next); saved = next; error = "" } catch { self.error = error.localizedDescription } }
    func connect(_ invitation: String) {
        do { let session = try RemoteSession(invitation: invitation); sessions.append(session); selected = session.id; error = "" }
        catch { self.error = error.localizedDescription }
    }
    func close(_ session: RemoteSession) { session.close(); sessions.removeAll { $0.id == session.id }; selected = sessions.first?.id }
    deinit { for session in sessions { session.close() } }
}
@main struct LumeApp: App {
    @StateObject private var computers = Computers()
    @Environment(\.scenePhase) private var phase
    var body: some Scene {
        WindowGroup {
            Dashboard().environmentObject(computers).preferredColorScheme(.dark)
                .onChange(of: phase) { phase in if phase != .active { computers.sessions.forEach { $0.release(); $0.pauseMedia() } } }
        }
    }
}
struct Dashboard: View {
    @EnvironmentObject var computers: Computers
    @State private var invitation = ""
    @State private var showComputers = true
    @State private var showRecordings = false
    var body: some View {
        VStack(spacing: 12) {
            if showComputers || computers.sessions.isEmpty {
                VStack(alignment: .leading, spacing: 14) {
                    Text("Lume").font(.largeTitle.bold())
                    Button("Recordings") { showRecordings = true }
                    TextField("Paste an invitation or one-time pairing code", text: $invitation).textInputAutocapitalization(.never).autocorrectionDisabled().privacySensitive().textFieldStyle(.roundedBorder)
                    Button("Connect") { computers.connect(invitation.trimmingCharacters(in: .whitespacesAndNewlines)); if computers.error.isEmpty { invitation = ""; showComputers = false } }.buttonStyle(.borderedProminent)
                    if !computers.error.isEmpty { Text(computers.error).foregroundStyle(.red) }
                    ForEach(computers.saved) { computer in HStack { Button(computer.name) { computers.connect(computer.connection); showComputers = false }; Spacer(); Menu("Manage") { Button("Forget on this device", role: .destructive) { computers.forget(computer) }; Text("Revoke access on the host to invalidate the key.") } } }
                    if !computers.sessions.isEmpty {
                        ScrollView(.horizontal) { HStack { ForEach(computers.sessions) { session in Button(session.state.peer.isEmpty ? "Computer \(session.id)" : session.state.peer) { computers.selected = session.id; showComputers = false }.buttonStyle(.bordered) } } }
                    }
                }.padding()
            }
            if let session = computers.sessions.first(where: { $0.id == computers.selected }) {
                SessionView(session: session, showComputers: $showComputers).id(session.id)
            } else { Spacer(); Text("Connect to a computer you own or have permission to use.").foregroundStyle(.secondary).padding(); Spacer() }
        }.tint(Color(red: 0.3, green: 0.85, blue: 0.7)).sheet(isPresented: $showRecordings) { RecordingsView().environmentObject(computers) }
    }
}
struct SessionView: View {
    @ObservedObject var session: RemoteSession
    @EnvironmentObject var computers: Computers
    @Binding var showComputers: Bool
    @Environment(\.scenePhase) private var phase
    @State private var showQuality = false
    @State private var showFiles = false
    @State private var showDisplays = false
    @State private var awaitingClipboard = false
    @State private var pressed = false
    @State private var drawing = false
    @State private var stroke: [[Int32]] = []
    @State private var strokeEpoch: Int32 = 0
    @State private var voiceConsent = false
    @State private var recordingOptions = false
    @State private var recordingFPS = "0"
    var body: some View {
        VStack(spacing: 6) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack {
                    Button("Computers") { showComputers.toggle() }
                    Button("Quality") { showQuality = true }
                    Button("Files") { session.release(); showFiles = true }.disabled(!session.state.connected || !session.state.files_allowed)
                    Menu("Actions") {
                        Button(drawing ? "Finish drawing" : "Draw") { session.release(); pressed = false; stroke.removeAll(); drawing.toggle() }.disabled(!session.state.input_ready || session.state.capabilities & 64 == 0)
                        Button("Clear marks") { stroke.removeAll(); session.action(["type": "annotation", "epoch": session.state.epoch, "points": [[Int32]]()]) }.disabled(!session.state.input_ready || session.state.capabilities & 64 == 0)
                        Button("Displays") { session.release(); showDisplays = true }.disabled(!session.state.connected || session.state.capabilities & 2 == 0)
                        Button(session.state.media.audio || session.state.media.audio_pending ? "Sound off" : "Sound") { session.action(["type": "audio", "enabled": !(session.state.media.audio || session.state.media.audio_pending)]) }.disabled(session.state.capabilities & 32 == 0)
                        Button(session.state.media.voice || session.state.media.voice_pending ? "End voice call" : "Voice call") { if session.state.media.voice || session.state.media.voice_pending { session.action(["type": "voice", "enabled": false]) } else { voiceConsent = true } }.disabled(session.state.capabilities & 1024 == 0)
                        Button(session.state.recording.active ? "Stop recording" : "Record to MKV") { if session.state.recording.active { session.record() } else { recordingOptions = true } }
                        Button("Send clipboard text") { if let text = UIPasteboard.general.string { session.action(["type": "clipboard", "text": text]) } }.disabled(!session.state.control)
                        Button("Get clipboard text") { awaitingClipboard = true; session.action(["type": "read_clipboard"]) }.disabled(!session.state.control || session.state.capabilities & 1 == 0)
                        Button("Right click") { session.input(1, 1); session.input(2, 1) }.disabled(!session.state.control || drawing)
                        Button("Scroll up") { session.input(3, 120) }.disabled(!session.state.control || drawing)
                        Button("Scroll down") { session.input(3, -120) }.disabled(!session.state.control || drawing)
                        Button("Enter") { session.input(4, 13); session.input(5, 13) }.disabled(!session.state.control || drawing)
                        Button("Escape") { session.input(4, 27); session.input(5, 27) }.disabled(!session.state.control || drawing)
                    }
                    Button("Disconnect", role: .destructive) { computers.close(session) }
                }.buttonStyle(.bordered).padding(.horizontal)
            }
            if let reply = session.state.reply {
                Button("Copy reply to the sharing computer") { UIPasteboard.general.setItems([[UIPasteboard.typeAutomatic: reply]], options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(300)]) }.padding(.horizontal)
            }
            if session.state.pair_ready { Button("Save paired computer") { computers.savePairing(session); showComputers = true } }
            Text(session.error.isEmpty ? session.state.status : session.error).font(.caption).foregroundStyle(.secondary).padding(.horizontal)
            if !session.state.media.status.isEmpty { Text(session.state.media.status).font(.caption) }
            if session.state.recording.active { Text("Recording · image and enabled system audio").font(.caption).foregroundStyle(.red) }
            GeometryReader { geometry in
                ZStack {
                    Color.black
                    if let frame = session.frame {
                        let image = frame.image
                        let scale = min(geometry.size.width / CGFloat(image.width), geometry.size.height / CGFloat(image.height))
                        let size = CGSize(width: CGFloat(image.width) * scale, height: CGFloat(image.height) * scale)
                        Image(decorative: image, scale: 1).resizable().frame(width: size.width, height: size.height)
                            .contentShape(Rectangle()).gesture(DragGesture(minimumDistance: 0).onChanged { value in
                                let x = Int32(max(0, min(65535, value.location.x / size.width * 65535)))
                                let y = Int32(max(0, min(65535, value.location.y / size.height * 65535)))
                                if drawing {
                                    guard session.state.input_ready else { stroke.removeAll(); return }
                                    if strokeEpoch != frame.epoch { stroke.removeAll(); strokeEpoch = frame.epoch }
                                    if stroke.count == 128 { stroke.remove(at: 126) }
                                    stroke.append([x, y])
                                } else { session.input(0, x, y, epoch: frame.epoch); if !pressed { session.input(1, 0, epoch: frame.epoch); pressed = true } }
                            }.onEnded { _ in
                                session.release(); pressed = false
                                if drawing && stroke.count >= 2 && strokeEpoch == frame.epoch { session.action(["type": "annotation", "epoch": strokeEpoch, "points": stroke]) }
                                stroke.removeAll()
                            })
                            .overlay {
                                if drawing && strokeEpoch == frame.epoch {
                                    Path { path in for (index, point) in stroke.enumerated() { let p = CGPoint(x: CGFloat(point[0]) / 65535 * size.width, y: CGFloat(point[1]) / 65535 * size.height); if index == 0 { path.move(to: p) } else { path.addLine(to: p) } } }.stroke(Color(red: 80/255, green: 245/255, blue: 181/255), lineWidth: 3).allowsHitTesting(false)
                                }
                            }
                    }
                }
            }.privacySensitive()
        }.sheet(isPresented: $showQuality) { QualityView(session: session) }
         .sheet(isPresented: $showFiles) { FilesView(session: session) }
         .sheet(isPresented: $showDisplays) { DisplaysView(session: session) }
         .alert("Recording FPS", isPresented: $recordingOptions) {
             TextField("0 = source", text: $recordingFPS).keyboardType(.numberPad)
             Button("Record") { if let fps = Int(recordingFPS), (0...1000).contains(fps) { session.record(fps: fps) } else { session.error = "Choose recording FPS from 0 to 1000." } }
             Button("Cancel", role: .cancel) {}
         } message: { Text("0 follows the source refresh rate. Actual recording depends on received frames and encoding speed.") }
         .alert("Allow your microphone for this call?", isPresented: $voiceConsent) { Button("Cancel", role: .cancel) {}; Button("Allow and request call") { computers.sessions.filter { $0.id != session.id }.forEach { $0.action(["type": "voice", "enabled": false]) }; session.requestVoice() } } message: { Text("The host must also accept. The microphone stops when you leave Lume. Use headphones to reduce echo.") }
         .onAppear { session.setViewing(phase == .active) }
         .onDisappear { session.setViewing(false); session.release(); pressed = false; stroke.removeAll() }
         .onChange(of: session.state.epoch) { _ in pressed = false }
         .onChange(of: phase) { phase in session.setViewing(phase == .active); if phase != .active { pressed = false } }
         .onChange(of: session.state.clipboard) { text in
             if awaitingClipboard, let text { UIPasteboard.general.setItems([[UIPasteboard.typeAutomatic: text]], options: [.localOnly: true]); session.action(["type": "clear_clipboard", "text": text]); awaitingClipboard = false }
         }
    }
}
struct DisplaysView: View {
    @ObservedObject var session: RemoteSession
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            List {
                if session.state.monitor_pending { ProgressView("Changing display…") }
                if !session.state.monitor_status.isEmpty { Text(session.state.monitor_status) }
                if !session.error.isEmpty { Text(session.error).foregroundStyle(.red) }
                ForEach(session.state.monitors) { display in
                    Button {
                        session.action(["type": "select_monitor", "id": display.id])
                    } label: {
                        VStack(alignment: .leading) {
                            Text((display.selected ? "✓ " : "") + display.name)
                            Text("\(display.width) × \(display.height) · \(display.refresh) Hz").font(.caption)
                        }
                    }.disabled(!session.state.connected || session.state.monitor_pending)
                }
            }.navigationTitle("Displays").toolbar {
                Button("Refresh") { session.action(["type": "list_monitors"]) }.disabled(!session.state.connected || session.state.monitor_pending)
                Button("Close") { dismiss() }
            }.onAppear { session.action(["type": "list_monitors"]) }
        }
    }
}
struct RecordingsView: View {
    @EnvironmentObject var computers: Computers
    @Environment(\.dismiss) private var dismiss
    @State private var files: [URL] = []
    var body: some View { NavigationStack { List(files, id: \.self) { url in
        if computers.sessions.contains(where: { $0.state.recording.active && $0.state.recording.path == url.path }) { Text("Recording: " + url.lastPathComponent) }
        else { ShareLink(item: url) { Text(url.lastPathComponent) } }
    }.overlay { if files.isEmpty { Text("No recordings saved yet.") } }.navigationTitle("Recordings").toolbar { Button("Close") { dismiss() } }.onAppear { if let folder = try? RemoteSession.recordingsFolder() { files = ((try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []).filter { $0.pathExtension == "mkv" }.sorted { $0.lastPathComponent > $1.lastPathComponent } } } } }
}
struct QualityView: View {
    @ObservedObject var session: RemoteSession
    @Environment(\.dismiss) var dismiss
    @State private var height = "1080"
    @State private var fps = "60"
    @State private var lossless = false
    var body: some View {
        NavigationStack {
            Form {
                Section { Button("Source") { session.quality(height: 0, fps: 0, lossless: true); dismiss() }; Button("Save data — 360p, 10 FPS") { session.quality(height: 360, fps: 10, lossless: false); dismiss() } }
                Section("Custom") {
                    TextField("Height — 0 keeps source", text: $height).keyboardType(.numberPad)
                    TextField("FPS — 0 follows source", text: $fps).keyboardType(.numberPad)
                    Toggle("Lossless pixels", isOn: $lossless).disabled(session.state.capabilities & 2048 == 0)
                    Button("Apply") { if let height = Int(height), let fps = Int(fps) { session.quality(height: height, fps: fps, lossless: lossless); dismiss() } }
                }
            }.navigationTitle("Quality").toolbar { Button("Close") { dismiss() } }
        }
    }
}
