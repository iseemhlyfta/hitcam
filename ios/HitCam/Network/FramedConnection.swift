import Foundation
import Network

/// Why a connection ended before or while securing it.
enum ConnectionSecurityError: Error {
    /// The PC's certificate is not the one pinned for it: another PC, or someone in between.
    case fingerprintMismatch
    /// No TLS with the PC (most likely HitCam 0.3.0 or older there).
    case tlsFailed
}

/// A TLS connection speaking HitCam framing, the PC's self-signed certificate pinned by fingerprint (no certificate
/// authority). All callbacks run on `queue`.
final class FramedConnection {
    enum Event {
        case ready
        case message(MessageHeader, Data)
        case closed(Error?)
    }

    private let connection: NWConnection
    private let queue: DispatchQueue
    private let handler: (Event) -> Void
    private var closed = false
    private var isReady = false
    private let encoder = JSONEncoder()

    /// What the TLS verify block saw; on `queue`, like everything else.
    private final class CertificateCheck {
        var seen: Data?
        var mismatch = false
    }
    private let check: CertificateCheck

    /// The PC's certificate fingerprint, known once `.ready` arrives.
    var seenFingerprint: Data? { check.seen }

    /// Video frames handed to the socket but not yet written out. Used for low-latency frame dropping.
    private(set) var framesInFlight = 0

    /// `expected`: the certificate to insist on (from the QR code or an earlier pairing); nil while pairing, when
    /// any certificate is accepted and the PIN commitments check it.
    init(address: ServerAddress, expected: Data?, queue: DispatchQueue, handler: @escaping (Event) -> Void) {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.connectionTimeout = 5
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 2
        let check = CertificateCheck()
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
            let chain = SecTrustCopyCertificateChain(sec_trust_copy_ref(trust).takeRetainedValue()) as? [SecCertificate]
            guard let leaf = chain?.first else { return complete(false) }
            let fingerprint = PinProof.fingerprint(of: leaf)
            check.seen = fingerprint
            if let expected, !PinProof.same(expected, fingerprint) {
                check.mismatch = true
                return complete(false)
            }
            complete(true)
        }, queue)
        let parameters = NWParameters(tls: tls, tcp: tcp)
        parameters.prohibitedInterfaceTypes = [.cellular]   // local network only
        connection = NWConnection(
            host: NWEndpoint.Host(address.host),
            port: NWEndpoint.Port(rawValue: address.port) ?? 47800,
            using: parameters)
        self.check = check
        self.queue = queue
        self.handler = handler
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.isReady = true
                self.handler(.ready)
                self.receiveHeader()
            case .failed(let error):
                self.finish(self.securityError(error).map { $0 as Error } ?? error)
            case .waiting(let error) where self.securityError(error) != nil:
                // TLS will not get better by waiting.
                self.finish(self.securityError(error))
            case .waiting(let error):
                // Either no route (wrong IP, Wi-Fi off) or iOS is showing the Local Network permission
                // prompt. Give the user time to answer, then report the error instead of waiting forever.
                self.queue.asyncAfter(deadline: .now() + 15) { [weak self] in
                    guard let self, !self.isReady else { return }
                    self.finish(error)
                }
            case .cancelled:
                self.finish(nil)
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    private func securityError(_ error: NWError) -> ConnectionSecurityError? {
        if check.mismatch { return .fingerprintMismatch }
        if case .tls = error { return .tlsFailed }
        return nil
    }

    func cancel() {
        connection.cancel()
    }

    /// Sends a last message and closes once it has been written, or after `timeout` if the socket is stuck.
    /// No events are delivered afterwards.
    func close<T: Encodable>(sending type: MessageType, json value: T, timeout: TimeInterval = 0.3) {
        // Capture the NWConnection itself: the owner usually drops this object right away.
        let connection = self.connection
        guard !closed, isReady, let payload = try? encoder.encode(value) else {
            closed = true
            connection.cancel()
            return
        }
        closed = true
        var packet = MessageHeader(type: type, length: UInt32(payload.count), timestamp: MonotonicClock.nowMicros()).encoded()
        packet.append(payload)
        connection.send(content: packet, completion: .contentProcessed { _ in connection.cancel() })
        queue.asyncAfter(deadline: .now() + timeout) { connection.cancel() }
    }

    func send(_ type: MessageType, flags: MessageFlags = [], timestamp: UInt64 = MonotonicClock.nowMicros(),
              payload: Data = Data(), isVideo: Bool = false) {
        guard !closed else { return }
        var packet = MessageHeader(type: type, flags: flags, length: UInt32(payload.count), timestamp: timestamp).encoded()
        packet.append(payload)
        if isVideo { framesInFlight += 1 }
        connection.send(content: packet, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            if isVideo { self.framesInFlight -= 1 }
            if let error { self.finish(error) }
        })
    }

    func send<T: Encodable>(_ type: MessageType, json value: T) {
        guard let payload = try? encoder.encode(value) else { return }
        send(type, payload: payload)
    }

    private func receiveHeader() {
        connection.receive(minimumIncompleteLength: MessageHeader.size, maximumLength: MessageHeader.size) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let error { return self.finish(error) }
            guard let data, data.count == MessageHeader.size else {
                return self.finish(isComplete ? nil : ProtocolError.shortHeader)
            }
            do {
                let header = try MessageHeader.decode(data)
                if header.length == 0 {
                    self.deliver(header, Data())
                } else {
                    self.receivePayload(header)
                }
            } catch {
                self.finish(error)
            }
        }
    }

    private func receivePayload(_ header: MessageHeader) {
        let length = Int(header.length)
        connection.receive(minimumIncompleteLength: length, maximumLength: length) { [weak self] data, _, _, error in
            guard let self else { return }
            if let error { return self.finish(error) }
            guard let data, data.count == length else { return self.finish(ProtocolError.shortHeader) }
            self.deliver(header, data)
        }
    }

    private func deliver(_ header: MessageHeader, _ payload: Data) {
        guard !closed else { return }
        handler(.message(header, payload))
        receiveHeader()
    }

    private func finish(_ error: Error?) {
        guard !closed else { return }
        closed = true
        connection.cancel()
        handler(.closed(error))
    }
}
