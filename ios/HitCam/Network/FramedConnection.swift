import Foundation
import Network

/// A TCP connection speaking HitCam framing. All callbacks run on `queue`.
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

    /// Video frames handed to the socket but not yet written out. Used for low-latency frame dropping.
    private(set) var framesInFlight = 0

    init(address: ServerAddress, queue: DispatchQueue, handler: @escaping (Event) -> Void) {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.connectionTimeout = 5
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 2
        let parameters = NWParameters(tls: nil, tcp: tcp)
        parameters.prohibitedInterfaceTypes = [.cellular]   // local network only
        connection = NWConnection(
            host: NWEndpoint.Host(address.host),
            port: NWEndpoint.Port(rawValue: address.port) ?? 47800,
            using: parameters)
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
                self.finish(error)
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

    func cancel() {
        connection.cancel()
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
