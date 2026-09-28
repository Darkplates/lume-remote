// Explicit local PDFKit print panel. Source prepared; no Apple device was available.
import AppKit
import PDFKit
import Foundation

guard CommandLine.arguments.count == 2 else { exit(2) }
let url = URL(fileURLWithPath: CommandLine.arguments[1])
guard url.pathExtension.lowercased() == "pdf",
      let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey]),
      values.isRegularFile == true, values.isSymbolicLink != true,
      let size = values.fileSize, size >= 8, size <= 128 * 1024 * 1024,
      let pdf = PDFDocument(url: url), !pdf.isLocked, pdf.pageCount > 0,
      let operation = pdf.printOperation(for: NSPrintInfo.shared, scalingMode: .pageScaleDownToFit, autoRotate: true)
else { exit(2) }
let application = NSApplication.shared
application.setActivationPolicy(.regular)
application.activate(ignoringOtherApps: true)
operation.jobTitle = "Lume downloaded PDF"
operation.showsPrintPanel = true
operation.showsProgressPanel = true
exit(operation.run() ? 0 : 1)
