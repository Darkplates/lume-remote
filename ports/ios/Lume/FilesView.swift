import SwiftUI
import UIKit
import UniformTypeIdentifiers

private struct PickerRequest: Identifiable { let id = UUID(); var export: URL?; var folder = false }
struct FilesView: View {
    @ObservedObject var session: RemoteSession
    @Environment(\.dismiss) var dismiss
    @State private var picker: PickerRequest?
    @State private var uploadFolder = ""
    @State private var printable: PrintablePDF?
    @State private var preparingPrint = false
    var body: some View {
        NavigationStack {
            List {
                Section {
                    Button("Drives / shares") { list("", 0) }
                    Button("Up") { list(parent(session.state.files.path), 0) }.disabled(session.state.files.path.isEmpty)
                    Text(session.state.files.path.isEmpty ? "Choose a remote drive or share" : session.state.files.path).font(.caption)
                }
                Section("Remote files") {
                    ForEach(Array(session.state.files.entries.enumerated()), id: \.offset) { _, entry in
                        HStack {
                        Button(entry.name + (entry.directory ? " /" : " — Download")) {
                            let path = child(session.state.files.path, entry.name)
                            if entry.directory { list(path, 0) }
                            else { do { session.action(["type": "download_file", "remote": path, "folder": try session.downloadFolder().path, "name": entry.name]) } catch { session.error = "Unable to create a download folder." } }
                        }.disabled(!entry.directory && (session.state.files.active || session.preparingFiles))
                        if entry.directory && !session.state.files.path.isEmpty && session.state.capabilities & 16 != 0 {
                            Button("Download folder") { do { session.action(["type": "download_folder", "remote": child(session.state.files.path, entry.name), "folder": try session.downloadFolder().path, "name": entry.name]) } catch { session.error = "Unable to create a download folder." } }.disabled(session.state.files.active || session.preparingFiles)
                        }
                        }
                    }
                    HStack {
                        Button("Previous") { list(session.state.files.path, session.state.files.page - 1) }.disabled(session.state.files.page == 0 || session.state.files.listing)
                        Spacer()
                        Button("Next") { list(session.state.files.path, session.state.files.page + 1) }.disabled(!session.state.files.more || session.state.files.listing)
                    }
                }
                Section {
                    Button("Upload file…") { uploadFolder = session.state.files.path; picker = PickerRequest() }.disabled(session.state.files.path.isEmpty || session.state.files.active || session.preparingFiles)
                    Button("Upload folder…") { uploadFolder = session.state.files.path; picker = PickerRequest(folder: true) }.disabled(session.state.files.path.isEmpty || session.state.files.active || session.preparingFiles || session.state.capabilities & 16 == 0)
                    if let completed = session.state.files.completed, let url = session.verifiedDownload(completed) { Button("Save download…") { picker = PickerRequest(export: url) }.disabled(session.preparingFiles) }
                    if let completed = session.state.files.completed, !session.state.files.completed_directory, let url = session.verifiedDownload(completed), url.pathExtension.lowercased() == "pdf" {
                        Button("Print PDF…") { preparingPrint = true; PrintablePDF.prepare(url) { result in preparingPrint = false; switch result { case .success(let value): printable = value; case .failure(let error): session.error = error.localizedDescription } } }.disabled(preparingPrint)
                    }
                    if session.state.files.resumed_bytes > 0 { Text("\(session.state.files.resumed_bytes) bytes resumed after verification").font(.caption) }
                    if session.state.files.active || session.preparingFiles {
                        Text("\(session.state.files.direction) \(session.state.files.name)")
                        ProgressView(value: Double(session.state.files.bytes), total: Double(max(1, session.state.files.total)))
                        Button("Cancel transfer", role: .destructive) { session.action(["type": "cancel_file"]) }
                    }
                    if session.preparingFiles { Text("Preparing or sending the selected item…").font(.caption) }
                    if session.state.files.folder_job { Text("\(session.state.files.items_done) items · \(session.state.files.bytes_done) bytes verified").font(.caption) }
                    Text(session.error.isEmpty ? session.state.files.status : session.error).font(.caption)
                }
            }.navigationTitle("Files").toolbar { Button("Close") { dismiss() } }
        }.onAppear { list("", 0) }
         .sheet(item: $printable) { value in PDFPrintPanel(document: value) { printable = nil } }
         .sheet(item: $picker) { request in FilePicker(export: request.export, folder: request.folder) { urls in if request.export == nil, let url = urls.first { session.upload(url, folder: uploadFolder) }; picker = nil } }
    }
    private func list(_ path: String, _ page: Int) { session.action(["type": "list_files", "path": path, "page": page]) }
    private func child(_ path: String, _ name: String) -> String { if path.isEmpty { return name }; var base = path; while base.last == "\\" || base.last == "/" { base.removeLast() }; return base + (path.contains("\\") ? "\\" : "/") + name }
    private func parent(_ path: String) -> String {
        var value = path; while value.last == "\\" || value.last == "/" { value.removeLast() }
        guard let last = value.lastIndex(where: { $0 == "\\" || $0 == "/" }) else { return "" }
        if value.distance(from: value.startIndex, to: last) == 2 && value.dropFirst().first == ":" { return String(value[...last]) }
        return last == value.startIndex ? "/" : String(value[..<last])
    }
}
private struct FilePicker: UIViewControllerRepresentable {
    let export: URL?
    let folder: Bool
    let done: ([URL]) -> Void
    func makeCoordinator() -> Coordinator { Coordinator(done: done) }
    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        let picker = export.map { UIDocumentPickerViewController(forExporting: [$0], asCopy: true) } ?? UIDocumentPickerViewController(forOpeningContentTypes: folder ? [.folder] : [.item], asCopy: !folder)
        picker.delegate = context.coordinator; picker.allowsMultipleSelection = false; return picker
    }
    func updateUIViewController(_ uiViewController: UIDocumentPickerViewController, context: Context) {}
    final class Coordinator: NSObject, UIDocumentPickerDelegate {
        let done: ([URL]) -> Void
        init(done: @escaping ([URL]) -> Void) { self.done = done }
        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) { done(urls) }
        func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) { done([]) }
    }
}
