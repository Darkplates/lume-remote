import Foundation
import UIKit
import SwiftUI

final class PrintablePDF: Identifiable {
    let id = UUID()
    let url: URL
    init(url: URL) { self.url = url }
    deinit { try? FileManager.default.removeItem(at: url) }
    static func prepare(_ source: URL, done: @escaping (Result<PrintablePDF, Error>) -> Void) {
        DispatchQueue.global(qos: .utility).async {
            do {
                let values = try source.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
                guard source.pathExtension.lowercased() == "pdf", values.isRegularFile == true,
                      values.isSymbolicLink != true, let size = values.fileSize, (8...128 * 1024 * 1024).contains(size)
                else { throw BridgeFailure.message("Choose a downloaded PDF of at most 128 MiB.") }
                let target = FileManager.default.temporaryDirectory.appendingPathComponent("lume-print-" + UUID().uuidString + ".pdf")
                let copy = PrintablePDF(url: target)
                let input = try FileHandle(forReadingFrom: source)
                defer { try? input.close() }
                guard try input.read(upToCount: 5) == Data("%PDF-".utf8) else { throw BridgeFailure.message("This is not a PDF.") }
                try input.seek(toOffset: 0)
                guard FileManager.default.createFile(atPath: target.path, contents: nil, attributes: [.posixPermissions: 0o600, .protectionKey: FileProtectionType.complete])
                else { throw BridgeFailure.message("Unable to prepare printing.") }
                let output = try FileHandle(forWritingTo: target)
                defer { try? output.close() }
                var copied = 0
                while let bytes = try input.read(upToCount: 65536), !bytes.isEmpty {
                    copied += bytes.count
                    guard copied <= size else { throw BridgeFailure.message("The PDF changed while preparing.") }
                    try output.write(contentsOf: bytes)
                }
                guard copied == size else { throw BridgeFailure.message("The PDF changed while preparing.") }
                try output.synchronize()
                DispatchQueue.main.async { done(.success(copy)) }
            } catch { DispatchQueue.main.async { done(.failure(error)) } }
        }
    }
}
struct PDFPrintPanel: UIViewControllerRepresentable {
    let document: PrintablePDF
    let done: () -> Void
    func makeUIViewController(context: Context) -> Controller { Controller(document: document, done: done) }
    func updateUIViewController(_ uiViewController: Controller, context: Context) {}
    final class Controller: UIViewController {
        let document: PrintablePDF
        let done: () -> Void
        var presented = false
        init(document: PrintablePDF, done: @escaping () -> Void) { self.document = document; self.done = done; super.init(nibName: nil, bundle: nil) }
        required init?(coder: NSCoder) { fatalError("Not supported") }
        override func viewDidAppear(_ animated: Bool) {
            super.viewDidAppear(animated)
            guard !presented else { return }; presented = true
            guard UIPrintInteractionController.canPrint(document.url) else { done(); return }
            let panel = UIPrintInteractionController.shared
            let info = UIPrintInfo(dictionary: nil); info.jobName = "Lume downloaded PDF"; info.outputType = .general
            panel.printInfo = info; panel.printingItem = document.url; panel.showsNumberOfCopies = true
            // iPad requires a popover anchor. Retain the snapshot until the system finishes.
            panel.present(from: CGRect(x: view.bounds.midX, y: view.bounds.midY, width: 1, height: 1), in: view, animated: true) { [document, done] _, _, _ in _ = document; done() }
        }
    }
}
