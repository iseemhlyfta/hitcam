import XCTest
@testable import HitCam

final class PinProofTests: XCTestCase {
    func testCommitmentsMatchThePcAndAndroid() {
        // The same vectors are in TlsServerTests.cs (PC) and PinProofTest.kt (Android).
        let fingerprint = Data(repeating: 1, count: 32)
        let nonce = Data(repeating: 2, count: 32)
        XCTAssertEqual(
            PinProof.commit(label: PinProof.pcLabel, fingerprint: fingerprint, pin: "123456", nonce: nonce).hex,
            "cec2d2dd935ef81984f804ae036b0070aebe85dbbb5553d8904ef361c277e7e1")
        XCTAssertEqual(
            PinProof.commit(label: PinProof.phoneLabel, fingerprint: fingerprint, pin: "123456", nonce: nonce).hex,
            "09e06dfbc17294fa4b4c4e1789a1c306ff506ac5020569463c2191e782dba7f3")
    }

    func testHexRoundTripsAndRejectsGarbage() {
        let bytes = Data((0..<32).map { UInt8($0) })
        XCTAssertEqual(Data(hex: bytes.hex, size: 32), bytes)
        XCTAssertEqual(Data(hex: bytes.hex.uppercased(), size: 32), bytes)
        XCTAssertNil(Data(hex: String(repeating: "zz", count: 32), size: 32))
        XCTAssertNil(Data(hex: "00", size: 32))
    }

    func testNoncesAreFreshAndSized() {
        XCTAssertEqual(PinProof.newNonce().count, 32)
        XCTAssertNotEqual(PinProof.newNonce(), PinProof.newNonce())
    }

    func testStoredPairingsKeepTheCertificateTheTokenBelongsTo() {
        let fp = String(repeating: "ab", count: 32)
        let stored = StoredPairing(fingerprint: fp, token: "token").encoded
        XCTAssertEqual(StoredPairing(stored: stored), StoredPairing(fingerprint: fp, token: "token"))
        // A bare token from app 0.3.0 has no certificate: not a pairing to send it with.
        XCTAssertNil(StoredPairing(stored: "token"))
        XCTAssertNil(StoredPairing(stored: "v2:short:token"))
    }

    func testTheQrCodeCarriesTheFingerprint() {
        let fp = String(repeating: "0f", count: 32)
        XCTAssertEqual(ServerAddress.parse("hitcam://10.0.0.2:47800?id=pc&name=PC&fp=\(fp)")?.fingerprint, fp)
        XCTAssertEqual(ServerAddress.parse("hitcam://10.0.0.2:47800?id=pc&fp=\(fp.uppercased())")?.fingerprint, fp)
        // A damaged fingerprint rejects the whole code rather than connecting without it.
        XCTAssertNil(ServerAddress.parse("hitcam://10.0.0.2:47800?id=pc&fp=1234"))
        XCTAssertNil(ServerAddress.parse("hitcam://10.0.0.2:47800?id=pc")?.fingerprint)
    }

    func testPairingPinsTheCertificateAndKeepsTheChosenId() {
        let scanned = ServerAddress(host: "192.168.1.5", port: 47800, serverId: "pc-1", name: nil)
        let paired = ServerTrust.afterPairing(scanned, claimedServerId: "pc-2", fingerprint: "ff")
        XCTAssertEqual(paired.serverId, "pc-1")
        XCTAssertEqual(paired.fingerprint, "ff")
    }
}
