import XCTest
@testable import HitCam

final class CameraRulesTests: XCTestCase {
    private let cameras = ["back-wide", "front"]
    private let state = CameraRules.defaultState   // 1920×1080 @ 30, back-wide

    func testKnownPresetIsApplied() {
        let next = CameraRules.applying(Control(width: 1280, height: 720, fps: 60), to: state, cameraIds: cameras)

        XCTAssertEqual([next.width, next.height, next.fps], [1280, 720, 60])
    }

    func testUnknownResolutionFromThePcIsIgnored() {
        let next = CameraRules.applying(Control(width: 99_999_999_999, height: 3), to: state, cameraIds: cameras)

        XCTAssertEqual(next, state)
    }

    func testUnknownFrameRateIsIgnored() {
        XCTAssertEqual(CameraRules.applying(Control(fps: 240), to: state, cameraIds: cameras).fps, 30)
        XCTAssertEqual(CameraRules.applying(Control(fps: 0), to: state, cameraIds: cameras).fps, 30)
    }

    func testFrameRateFallsBackWhenTheNewSizeDoesNotSupportIt() {
        let presets = [VideoPreset(width: 1280, height: 720, fps: [30, 60]), VideoPreset(width: 3840, height: 2160, fps: [30])]
        var current = state
        current.width = 1280
        current.height = 720
        current.fps = 60

        let next = CameraRules.applying(Control(width: 3840, height: 2160), to: current, cameraIds: cameras, presets: presets)

        XCTAssertEqual([next.width, next.height, next.fps], [3840, 2160, 30])
    }

    func testUnknownCameraIsIgnored() {
        XCTAssertEqual(CameraRules.applying(Control(cameraId: "back-tele"), to: state, cameraIds: cameras).cameraId, "back-wide")
        XCTAssertEqual(CameraRules.applying(Control(cameraId: "front"), to: state, cameraIds: cameras).cameraId, "front")
    }

    func testBitrateIsClampedAndRotationValidated() {
        XCTAssertEqual(CameraRules.applying(Control(bitrateKbps: 1_000_000), to: state, cameraIds: cameras).bitrateKbps, 50_000)
        XCTAssertEqual(CameraRules.applying(Control(bitrateKbps: -5), to: state, cameraIds: cameras).bitrateKbps, 500)
        XCTAssertEqual(CameraRules.applying(Control(rotation: 45), to: state, cameraIds: cameras).rotation, 0)
        XCTAssertEqual(CameraRules.applying(Control(rotation: 90), to: state, cameraIds: cameras).rotation, 90)
    }

    func testInvalidStoredStateFallsBackToDefaults() {
        var broken = state
        broken.width = 4000
        broken.height = 3000
        XCTAssertFalse(CameraRules.isValid(broken, cameraIds: cameras))
        XCTAssertEqual(CameraRules.sanitized(broken, cameraIds: cameras), CameraRules.defaultState)

        var missingCamera = state
        missingCamera.cameraId = "back-tele"
        XCTAssertEqual(CameraRules.sanitized(missingCamera, cameraIds: cameras), CameraRules.defaultState)

        var good = state
        good.width = 1280
        good.height = 720
        good.fps = 60
        XCTAssertEqual(CameraRules.sanitized(good, cameraIds: cameras), good)
        XCTAssertEqual(CameraRules.sanitized(nil, cameraIds: cameras), CameraRules.defaultState)
    }

    func testOutputSizeSwapsForPortrait() {
        var portrait = state
        portrait.rotation = 90
        XCTAssertEqual(CameraRules.outputSize(for: state), OutputSize(width: 1920, height: 1080))
        XCTAssertEqual(CameraRules.outputSize(for: portrait), OutputSize(width: 1080, height: 1920))
    }
}
