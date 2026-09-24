import Foundation
import Security
import UIKit

/// Pairing tokens live in the Keychain, keyed by the PC's server id (or host:port for manual entries).
enum TokenStore {
    private static let service = "io.github.hitnes.hitcam.token"

    static func token(for key: String) -> String? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: key,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func save(_ token: String, for key: String) {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: key,
        ]
        SecItemDelete(query as CFDictionary)
        var item = query
        item[kSecValueData] = Data(token.utf8)
        item[kSecAttrAccessible] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }

    static func remove(for key: String) {
        let query: [CFString: Any] = [kSecClass: kSecClassGenericPassword, kSecAttrService: service, kSecAttrAccount: key]
        SecItemDelete(query as CFDictionary)
    }
}

enum LocalStore {
    private static let defaults = UserDefaults.standard

    /// Random per-install identity presented to the PC (not tied to any Apple identifier).
    static var deviceId: String {
        if let id = defaults.string(forKey: "deviceId") { return id }
        let id = UUID().uuidString
        defaults.set(id, forKey: "deviceId")
        return id
    }

    /// Recently used PCs. A stored serverId is trusted (its token may be sent to that address), so the session only
    /// remembers an id that came from a QR code or was confirmed by pairing, never one a PC merely claimed.
    static var recentServers: [ServerAddress] {
        get {
            guard let data = defaults.data(forKey: "recentServers") else { return [] }
            let stored = (try? JSONDecoder().decode([ServerAddress].self, from: data)) ?? []
            if defaults.bool(forKey: "recentServersTrusted") { return stored }
            // Older versions also stored the id a typed address's PC claimed about itself; it can't be told apart
            // from a scanned one, so drop them all once. Tokens are still found by host:port; a QR scan restores the id.
            let cleaned = stored.map { server -> ServerAddress in
                var copy = server
                copy.serverId = nil
                return copy
            }
            recentServers = cleaned
            return cleaned
        }
        set {
            defaults.set(try? JSONEncoder().encode(Array(newValue.prefix(5))), forKey: "recentServers")
            defaults.set(true, forKey: "recentServersTrusted")
        }
    }

    static func remember(_ server: ServerAddress) {
        var list = recentServers.filter { $0.host != server.host || $0.port != server.port }
        list.insert(server, at: 0)
        recentServers = list
    }

    static var cameraState: CameraState? {
        get {
            guard let data = defaults.data(forKey: "cameraState") else { return nil }
            return try? JSONDecoder().decode(CameraState.self, from: data)
        }
        set { defaults.set(try? JSONEncoder().encode(newValue), forKey: "cameraState") }
    }

    static var modelIdentifier: String {
        var info = utsname()
        uname(&info)
        return withUnsafeBytes(of: &info.machine) { raw in
            String(decoding: raw.prefix { $0 != 0 }, as: UTF8.self)
        }
    }
}
