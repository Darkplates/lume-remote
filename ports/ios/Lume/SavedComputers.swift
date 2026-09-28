import Foundation
import Security

struct SavedComputer: Codable, Identifiable {
    let id: String
    let name: String
    let connection: String
}
enum SavedStore {
    private static let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "com.lume.saved-computers", kSecAttrAccount as String: "v1"]
    static func read() throws -> [SavedComputer] {
        var request = query; request[kSecReturnData as String] = true; request[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &item)
        if status == errSecItemNotFound { return [] }
        guard status == errSecSuccess, let data = item as? Data, data.count <= 131072 else { throw BridgeFailure.message("Unable to unlock saved computers on this device.") }
        let values = try JSONDecoder().decode([SavedComputer].self, from: data)
        guard values.count <= 64 else { throw BridgeFailure.message("Too many saved computers.") }
        return values
    }
    static func write(_ values: [SavedComputer]) throws {
        guard values.count <= 64 else { throw BridgeFailure.message("Remove an unused saved computer first.") }
        let data = try JSONEncoder().encode(values)
        guard data.count <= 131072 else { throw BridgeFailure.message("Saved-computer data exceeds its limit.") }
        let attributes: [String: Any] = [kSecValueData as String: data, kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound { status = SecItemAdd(query.merging(attributes) { _, new in new } as CFDictionary, nil) }
        guard status == errSecSuccess else { throw BridgeFailure.message("Unable to protect saved computers on this device.") }
    }
}
