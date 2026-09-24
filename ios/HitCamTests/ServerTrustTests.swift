import XCTest
@testable import HitCam

final class ServerTrustTests: XCTestCase {
    func testTypedAddressLooksUpOnlyHostAndPort() {
        let typed = ServerAddress(host: "192.168.1.5", port: 47800, serverId: nil, name: nil)

        XCTAssertEqual(ServerTrust.tokenKeys(for: typed), ["192.168.1.5:47800"])
    }

    func testQrAddressLooksUpItsServerIdFirst() {
        let scanned = ServerAddress(host: "192.168.1.5", port: 47800, serverId: "pc-1", name: "Desk")

        XCTAssertEqual(ServerTrust.tokenKeys(for: scanned), ["pc-1", "192.168.1.5:47800"])
    }

    func testClaimedIdIsTrustedOnlyAfterPairing() {
        let typed = ServerAddress(host: "192.168.1.5", port: 47800, serverId: nil, name: nil)
        let paired = ServerTrust.afterPairing(typed, claimedServerId: "pc-1")

        XCTAssertEqual(paired.serverId, "pc-1")
        XCTAssertEqual(ServerTrust.tokenKeys(for: paired), ["pc-1", "192.168.1.5:47800"])
    }

    func testPairingKeepsTheIdTheUserChose() {
        let scanned = ServerAddress(host: "192.168.1.5", port: 47800, serverId: "pc-1", name: nil)

        XCTAssertEqual(ServerTrust.afterPairing(scanned, claimedServerId: "pc-2").serverId, "pc-1")
        XCTAssertEqual(ServerTrust.afterPairing(scanned, claimedServerId: nil).serverId, "pc-1")
    }
}
