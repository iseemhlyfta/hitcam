import Foundation
import Network

/// A HitCam PC announced on the local network (DNS-SD `_hitcam._tcp`). `claimedId` is only what the announcement
/// says: anyone on the network can announce any id, so it is never used to send a token (see ServerTrust); the address
/// alone is connected to, and a PC not paired at that address asks for its PIN.
struct FoundPc: Hashable, Identifiable {
    var name: String
    var host: String
    var port: UInt16
    var claimedId: String?

    var id: String { "\(host):\(port)" }

    /// What to connect to: the address, with the name for display only.
    var address: ServerAddress { ServerAddress(host: host, port: port, serverId: nil, name: name) }

    /// From an announcement's name and TXT entries; nil without an IPv4 address in `addr`.
    static func from(serviceName: String, txt: [String: String]) -> FoundPc? {
        let host = (txt["addr"] ?? "").split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.first { !$0.isEmpty }
        guard let host else { return nil }
        let port = txt["port"].flatMap { UInt16($0) }.flatMap { $0 > 0 ? $0 : nil } ?? ProtocolInfo.defaultPort
        let name = txt["name"].flatMap { $0.isEmpty ? nil : $0 } ?? serviceName
        let id = txt["id"].flatMap { $0.isEmpty ? nil : $0 }
        return FoundPc(name: name, host: host, port: port, claimedId: id)
    }
}

/// Browses for HitCam PCs while started (Network.framework); `found` updates on the main queue.
@MainActor
final class PcBrowser: ObservableObject {
    @Published private(set) var found: [FoundPc] = []
    private var browser: NWBrowser?

    func start() {
        guard browser == nil else { return }
        let browser = NWBrowser(for: .bonjourWithTXTRecord(type: ProtocolInfo.bonjourType, domain: nil), using: .tcp)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            let found = results.compactMap { result -> FoundPc? in
                guard case let .service(name, _, _, _) = result.endpoint else { return nil }
                var txt: [String: String] = [:]
                if case let .bonjour(record) = result.metadata {
                    for (key, entry) in record {
                        if case let .string(value) = entry { txt[key] = value }
                    }
                }
                return FoundPc.from(serviceName: name, txt: txt)
            }
            Task { @MainActor in self?.found = Array(Set(found)).sorted { $0.name < $1.name } }
        }
        browser.start(queue: .main)
        self.browser = browser
    }

    func stop() {
        browser?.cancel()
        browser = nil
        found = []
    }
}
