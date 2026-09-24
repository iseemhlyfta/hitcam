import XCTest
@testable import HitCam

final class ProtocolTests: XCTestCase {
    func testHeaderMatchesTheWindowsEncoding() throws {
        // Same bytes as windows/HitCam.Core.Tests ProtocolTests.Header_round_trips.
        let header = MessageHeader(type: .videoFrame, flags: .keyframe, length: 1234, timestamp: 0x0102030405060708)
        let bytes = header.encoded()

        XCTAssertEqual([UInt8](bytes), [0x11, 0x01, 0, 0, 0xD2, 0x04, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1])
        XCTAssertEqual(try MessageHeader.decode(bytes), header)
    }

    func testHeaderDecodesFromADataSlice() throws {
        let header = MessageHeader(type: .ping, length: 0, timestamp: 42)
        let padded = Data([9, 9, 9]) + header.encoded()

        XCTAssertEqual(try MessageHeader.decode(padded.dropFirst(3)), header)
    }

    func testHeaderRejectsBadInput() {
        var bytes = MessageHeader(type: .ping, length: 0, timestamp: 0).encoded()
        bytes[2] = 1
        XCTAssertThrowsError(try MessageHeader.decode(bytes))

        var big = MessageHeader(type: .ping, length: 0, timestamp: 0).encoded()
        big.replaceSubrange(4..<8, with: [0xFF, 0xFF, 0xFF, 0x7F])
        XCTAssertThrowsError(try MessageHeader.decode(big))
    }

    func testControlJsonOmitsMissingFields() throws {
        let json = try JSONEncoder().encode(Control(zoom: 2, torch: true))
        let object = try JSONSerialization.jsonObject(with: json) as? [String: Any]

        XCTAssertEqual(object?.count, 2)
        XCTAssertEqual(object?["zoom"] as? Double, 2)
        XCTAssertEqual(object?["torch"] as? Bool, true)
    }

    func testStateSavedByAnOlderVersionStillDecodes() throws {
        // Stored by 0.1 in UserDefaults, before white balance, exposure lock and stabilization existed.
        let json = #"{"cameraId":"back-wide","zoom":1,"torch":false,"focusMode":"continuous","lensPosition":0.5,"exposureBias":0,"mirror":false,"rotation":0,"width":1920,"height":1080,"fps":30,"bitrateKbps":8000}"#
        let state = try JSONDecoder().decode(CameraState.self, from: Data(json.utf8))

        XCTAssertEqual(state.cameraId, "back-wide")
        XCTAssertNil(state.whiteBalanceMode)
        XCTAssertNil(state.stabilization)
    }

    func testPcCanLockWhiteBalanceExposureAndSetStabilization() throws {
        let json = #"{"whiteBalanceMode":"locked","whiteBalanceTemperature":4200,"whiteBalanceTint":-10,"exposureMode":"locked","stabilization":"standard"}"#
        let control = try JSONDecoder().decode(Control.self, from: Data(json.utf8))

        XCTAssertEqual(control.whiteBalanceMode, "locked")
        XCTAssertEqual(control.whiteBalanceTemperature, 4200)
        XCTAssertEqual(control.whiteBalanceTint, -10)
        XCTAssertEqual(control.exposureMode, "locked")
        XCTAssertEqual(control.stabilization, "standard")
        XCTAssertNil(control.zoom)
    }

    func testHelloAckFromThePcDecodes() throws {
        let json = #"{"protocolVersion":1,"status":"pairingRequired","serverName":"PC","serverId":"abc"}"#
        let ack = try JSONDecoder().decode(HelloAck.self, from: Data(json.utf8))

        XCTAssertEqual(ack.status, HelloStatus.pairingRequired)
    }

    func testAvccToAnnexB() {
        let avcc = Data([0, 0, 0, 3, 0x67, 0xAA, 0xBB, 0, 0, 0, 2, 0x65, 0x11])
        let annexB = AnnexB.fromAVCC(avcc)

        XCTAssertEqual(annexB.map { [UInt8]($0) }, [0, 0, 0, 1, 0x67, 0xAA, 0xBB, 0, 0, 0, 1, 0x65, 0x11])
        XCTAssertNil(AnnexB.fromAVCC(Data([0, 0, 0, 9, 1])), "truncated NAL must be rejected")
    }

    func testServerAddressParsing() {
        XCTAssertEqual(
            ServerAddress.parse("hitcam://192.168.1.5:47800?id=abc&name=My%20PC"),
            ServerAddress(host: "192.168.1.5", port: 47800, serverId: "abc", name: "My PC"))
        XCTAssertEqual(ServerAddress.parse(" 10.0.0.2 "), ServerAddress(host: "10.0.0.2", port: 47800, serverId: nil, name: nil))
        XCTAssertEqual(ServerAddress.parse("10.0.0.2:5000")?.port, 5000)
        XCTAssertNil(ServerAddress.parse("10.0.0.2:abc"))
        XCTAssertNil(ServerAddress.parse(""))
    }
}
