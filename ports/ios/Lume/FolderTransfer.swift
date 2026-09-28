import Foundation

/// Copies a picker-granted item into private staging without following links.
enum FolderTransfer {
    static func copy(_ source: URL, to destination: URL, cancelled: () -> Bool, depth: Int = 0) throws {
        guard !cancelled(), depth < 64 else { throw BridgeFailure.message("Folder copy cancelled or nesting is too deep.") }
        let values = try source.resourceValues(forKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey])
        guard values.isSymbolicLink != true else { throw BridgeFailure.message("Linked files are unsupported.") }
        if values.isDirectory == true {
            guard !FileManager.default.fileExists(atPath: destination.path) else { throw BridgeFailure.message("Duplicate destination folder.") }
            try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: false)
            var enumerationError: Error?
            guard let iterator = FileManager.default.enumerator(at: source, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsSubdirectoryDescendants], errorHandler: { _, error in enumerationError = error; return false }) else {
                throw BridgeFailure.message("Unable to enumerate the selected folder.")
            }
            while let child = iterator.nextObject() as? URL {
                try copy(child, to: destination.appendingPathComponent(child.lastPathComponent), cancelled: cancelled, depth: depth + 1)
            }
            if let enumerationError { throw enumerationError }
        } else {
            guard values.isRegularFile == true, !FileManager.default.fileExists(atPath: destination.path), FileManager.default.createFile(atPath: destination.path, contents: nil) else {
                throw BridgeFailure.message("Unsupported or duplicate file.")
            }
            let input = try FileHandle(forReadingFrom: source)
            defer { try? input.close() }
            let output = try FileHandle(forWritingTo: destination)
            defer { try? output.close() }
            while let bytes = try input.read(upToCount: 65536), !bytes.isEmpty {
                guard !cancelled() else { throw BridgeFailure.message("Folder copy cancelled.") }
                try output.write(contentsOf: bytes)
            }
        }
    }
}
