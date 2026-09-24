import Foundation

// HitCam protocol v1. Keep in sync with docs/protocol.md and windows/HitCam.Core/Protocol.

enum ProtocolInfo {
    static let version = 1
    static let defaultPort: UInt16 = 47800
    static let bonjourType = "_hitcam._tcp"
    static let uriScheme = "hitcam"
}

enum MessageType: UInt8 {
    case hello = 0x01
    case helloAck = 0x02
    case pairRequest = 0x03
    case pairResult = 0x04
    case streamConfig = 0x10
    case videoFrame = 0x11
    case requestKeyframe = 0x12
    case capabilities = 0x20
    case cameraState = 0x21
    case control = 0x22
    case status = 0x30
    case ping = 0x31
    case pong = 0x32
    case bye = 0x3F
}

struct MessageFlags: OptionSet, Equatable {
    let rawValue: UInt8
    static let keyframe = MessageFlags(rawValue: 1 << 0)
}

enum ProtocolError: Error, Equatable {
    case reservedNotZero
    case payloadTooLarge(UInt32)
    case shortHeader
}

/// Fixed 16-byte header, little-endian: type, flags, reserved(2), length(4), timestamp(8).
struct MessageHeader: Equatable {
    static let size = 16
    static let maxPayloadLength: UInt32 = 8 * 1024 * 1024

    var rawType: UInt8
    var flags: MessageFlags
    var length: UInt32
    var timestamp: UInt64

    var type: MessageType? { MessageType(rawValue: rawType) }

    init(type: MessageType, flags: MessageFlags = [], length: UInt32, timestamp: UInt64) {
        self.rawType = type.rawValue
        self.flags = flags
        self.length = length
        self.timestamp = timestamp
    }

    init(rawType: UInt8, flags: MessageFlags, length: UInt32, timestamp: UInt64) {
        self.rawType = rawType
        self.flags = flags
        self.length = length
        self.timestamp = timestamp
    }

    func encoded() -> Data {
        var data = Data(capacity: MessageHeader.size)
        data.append(rawType)
        data.append(flags.rawValue)
        data.append(contentsOf: [0, 0])
        data.appendLittleEndian(length)
        data.appendLittleEndian(timestamp)
        return data
    }

    static func decode(_ data: Data) throws -> MessageHeader {
        guard data.count >= size else { throw ProtocolError.shortHeader }
        return try data.withUnsafeBytes { raw -> MessageHeader in
            let reserved = UInt16(littleEndian: raw.loadUnaligned(fromByteOffset: 2, as: UInt16.self))
            guard reserved == 0 else { throw ProtocolError.reservedNotZero }
            let length = UInt32(littleEndian: raw.loadUnaligned(fromByteOffset: 4, as: UInt32.self))
            guard length <= maxPayloadLength else { throw ProtocolError.payloadTooLarge(length) }
            let timestamp = UInt64(littleEndian: raw.loadUnaligned(fromByteOffset: 8, as: UInt64.self))
            return MessageHeader(rawType: raw[0], flags: MessageFlags(rawValue: raw[1]), length: length, timestamp: timestamp)
        }
    }
}

extension Data {
    mutating func appendLittleEndian<T: FixedWidthInteger>(_ value: T) {
        Swift.withUnsafeBytes(of: value.littleEndian) { append(contentsOf: $0) }
    }
}

/// Monotonic clock in microseconds (mach absolute time, the same base as AVFoundation capture timestamps).
enum MonotonicClock {
    static func nowMicros() -> UInt64 { DispatchTime.now().uptimeNanoseconds / 1_000 }
}

// MARK: - JSON payloads

enum HelloStatus {
    static let accepted = "accepted"
    static let pairingRequired = "pairingRequired"
    static let pairingLocked = "pairingLocked"
    static let busy = "busy"
    static let versionMismatch = "versionMismatch"
}

struct Hello: Codable {
    var protocolVersion: Int
    var deviceId: String
    var deviceName: String
    var model: String?
    var appVersion: String?
    var token: String?
}

struct HelloAck: Codable {
    var protocolVersion: Int
    var status: String
    var serverName: String
    var serverId: String
}

struct PairRequest: Codable { var pin: String }

struct PairResult: Codable {
    var ok: Bool
    var token: String?
    var attemptsLeft: Int
}

struct StreamConfig: Codable, Equatable {
    var codec: String
    var width: Int
    var height: Int
    var fps: Int
    var bitrateKbps: Int
}

struct CameraInfo: Codable, Equatable {
    var id: String
    var name: String
    var position: String
    var minZoom: Double
    var maxZoom: Double
    var hasTorch: Bool
    var supportsFocus: Bool
    // Added in 0.2; optional so older PCs and stored states still decode.
    var supportsWhiteBalance: Bool? = nil
    var supportsExposureLock: Bool? = nil
}

struct VideoPreset: Codable, Equatable {
    var width: Int
    var height: Int
    var fps: [Int]
}

struct Capabilities: Codable {
    var cameras: [CameraInfo]
    var presets: [VideoPreset]
}

struct NormalizedPoint: Codable, Equatable {
    var x: Double
    var y: Double
}

struct CameraState: Codable, Equatable {
    var cameraId: String
    var zoom: Double
    var torch: Bool
    var focusMode: String
    var lensPosition: Double
    var exposureBias: Double
    var mirror: Bool
    var rotation: Int
    var width: Int
    var height: Int
    var fps: Int
    var bitrateKbps: Int
    // Added in 0.2: "auto" | "locked"; temperature in kelvin, tint in device units.
    var whiteBalanceMode: String? = nil
    var whiteBalanceTemperature: Double? = nil
    var whiteBalanceTint: Double? = nil
    // "auto" | "locked"
    var exposureMode: String? = nil
    // "off" | "standard" | "cinematic"; `stabilizationModes` are those the active format supports.
    var stabilization: String? = nil
    var stabilizationModes: [String]? = nil
}

/// Only non-nil fields are applied.
struct Control: Codable, Equatable {
    var cameraId: String?
    var zoom: Double?
    var torch: Bool?
    var focusMode: String?
    var lensPosition: Double?
    var focusPoint: NormalizedPoint?
    var exposureBias: Double?
    var mirror: Bool?
    var rotation: Int?
    var width: Int?
    var height: Int?
    var fps: Int?
    var bitrateKbps: Int?
    var whiteBalanceMode: String?
    var whiteBalanceTemperature: Double?
    var whiteBalanceTint: Double?
    var exposureMode: String?
    var stabilization: String?
}

struct DeviceStatus: Codable {
    var battery: Double
    var charging: Bool
    var thermal: String
    var fps: Double
    var bitrateKbps: Int
    var droppedFrames: Int
}

struct Bye: Codable { var reason: String? }

/// Parsed `hitcam://host:port?id=…&name=…` from the QR code on the PC.
struct ServerAddress: Equatable, Codable, Hashable {
    var host: String
    var port: UInt16
    var serverId: String?
    var name: String?

    static func parse(_ text: String) -> ServerAddress? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let url = URL(string: trimmed), url.scheme == ProtocolInfo.uriScheme, let host = url.host, !host.isEmpty {
            let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
            var port = ProtocolInfo.defaultPort
            if let raw = url.port {
                guard let exact = UInt16(exactly: raw), exact > 0 else { return nil }
                port = exact
            }
            return ServerAddress(
                host: host,
                port: port,
                serverId: items.first { $0.name == "id" }?.value,
                name: items.first { $0.name == "name" }?.value)
        }
        // Manual entry: "192.168.1.5" or "192.168.1.5:47800".
        let parts = trimmed.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count <= 2, let host = parts.first, !host.isEmpty, !host.contains(" ") else { return nil }
        var port = ProtocolInfo.defaultPort
        if parts.count == 2 {
            guard let parsed = UInt16(parts[1]), parsed > 0 else { return nil }
            port = parsed
        }
        return ServerAddress(host: String(host), port: port, serverId: nil, name: nil)
    }

    var display: String { port == ProtocolInfo.defaultPort ? host : "\(host):\(port)" }
}
