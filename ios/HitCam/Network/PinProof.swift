import CryptoKit
import Foundation
import Security

/// Pairing over TLS without letting a man in the middle learn the PIN; see PinProof.cs on the PC for the whole scheme.
/// The PC commits to (its certificate, PIN) first; the phone commits to (the certificate it sees, the typed PIN)
/// before the PC opens; the phone opens only if the PC's commitment matches what it sees.
enum PinProof {
    static let pcLabel = "hitcam-pc-v2"
    static let phoneLabel = "hitcam-phone-v2"
    static let nonceSize = 32

    static func newNonce() -> Data {
        var bytes = [UInt8](repeating: 0, count: nonceSize)
        let status = SecRandomCopyBytes(kSecRandomDefault, nonceSize, &bytes)
        precondition(status == errSecSuccess, "no random numbers")
        return Data(bytes)
    }

    /// SHA-256 of label, 0, fingerprint (32), PIN (6 ASCII digits), nonce (32).
    static func commit(label: String, fingerprint: Data, pin: String, nonce: Data) -> Data {
        precondition(fingerprint.count == 32 && nonce.count == nonceSize && pin.utf8.count == 6)
        var data = Data(label.utf8)
        data.append(0)
        data.append(fingerprint)
        data.append(contentsOf: Array(pin.utf8))
        data.append(nonce)
        return Data(SHA256.hash(data: data))
    }

    /// SHA-256 of a certificate's DER bytes: what phones pin.
    static func fingerprint(of certificate: SecCertificate) -> Data {
        Data(SHA256.hash(data: SecCertificateCopyData(certificate) as Data))
    }

    /// Equal without an early exit.
    static func same(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        return zip(a, b).reduce(0) { $0 | ($1.0 ^ $1.1) } == 0
    }
}

extension Data {
    var hex: String { map { String(format: "%02x", $0) }.joined() }

    /// Hex of exactly `size` bytes (either case), or nil.
    init?(hex: String, size: Int) {
        let digits = Array(hex.utf8)
        guard digits.count == size * 2 else { return nil }
        var bytes = [UInt8]()
        bytes.reserveCapacity(size)
        func value(_ c: UInt8) -> UInt8? {
            switch c {
            case 48...57: return c - 48
            case 97...102: return c - 87
            case 65...70: return c - 55
            default: return nil
            }
        }
        for i in 0..<size {
            guard let high = value(digits[2 * i]), let low = value(digits[2 * i + 1]) else { return nil }
            bytes.append(high << 4 | low)
        }
        self.init(bytes)
    }
}
