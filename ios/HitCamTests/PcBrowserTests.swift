import XCTest
@testable import HitCam

final class PcBrowserTests: XCTestCase {
    func testAnAnnouncementGivesTheAddressButNeverATrustedId() {
        let pc = FoundPc.from(serviceName: "DESKTOP-1", txt: ["id": "abc", "name": "My PC", "addr": "192.168.1.5,10.0.0.2", "port": "47800"])

        XCTAssertEqual(pc?.name, "My PC")
        XCTAssertEqual(pc?.claimedId, "abc")
        XCTAssertEqual(pc?.host, "192.168.1.5")
        // The id is only claimed: the address to connect to carries none, so no token goes to it before pairing.
        XCTAssertNil(pc?.address.serverId)
        XCTAssertEqual(pc?.address.port, 47800)
    }

    func testMissingFieldsFallBack() {
        let pc = FoundPc.from(serviceName: "DESKTOP-1", txt: ["addr": "10.0.0.2", "port": "oops"])
        XCTAssertEqual(pc?.name, "DESKTOP-1")
        XCTAssertNil(pc?.claimedId)
        XCTAssertEqual(pc?.port, ProtocolInfo.defaultPort)
    }

    func testAnAnnouncementWithoutAnAddressIsDropped() {
        XCTAssertNil(FoundPc.from(serviceName: "x", txt: ["name": "PC"]))
        XCTAssertNil(FoundPc.from(serviceName: "x", txt: ["addr": " , "]))
    }
}
