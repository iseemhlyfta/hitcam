import Foundation

/// Output frame size after rotation.
struct OutputSize: Equatable {
    var width: Int32
    var height: Int32
}

/// Pure rules for camera states requested by the PC or restored from storage (no AVFoundation, unit-tested).
enum CameraRules {
    static let presets = [
        VideoPreset(width: 1280, height: 720, fps: [30, 60]),
        VideoPreset(width: 1920, height: 1080, fps: [30, 60]),
    ]
    static let bitrateRange = 500...50_000
    static let rotations = [0, 90, 180, 270]

    static let defaultState = CameraState(
        cameraId: "back-wide", zoom: 1, torch: false, focusMode: "continuous", lensPosition: 0.5,
        exposureBias: 0, mirror: false, rotation: 0, width: 1920, height: 1080, fps: 30, bitrateKbps: 8000)

    /// Applies the stream-related fields of `control`. Values the phone does not offer are ignored, never stored.
    static func applying(_ control: Control, to state: CameraState, cameraIds: [String],
                         presets: [VideoPreset] = presets) -> CameraState {
        var next = state
        if let id = control.cameraId, cameraIds.contains(id) { next.cameraId = id }
        if let width = control.width, let height = control.height,
           presets.contains(where: { $0.width == width && $0.height == height }) {
            next.width = width
            next.height = height
        }
        let preset = presets.first { $0.width == next.width && $0.height == next.height }
        if let fps = control.fps, let preset, preset.fps.contains(fps) { next.fps = fps }
        // A new size may not support the current frame rate.
        if let preset, !preset.fps.contains(next.fps), let first = preset.fps.first { next.fps = first }
        if let bitrate = control.bitrateKbps {
            next.bitrateKbps = max(bitrateRange.lowerBound, min(bitrate, bitrateRange.upperBound))
        }
        if let mirror = control.mirror { next.mirror = mirror }
        if let rotation = control.rotation, rotations.contains(rotation) { next.rotation = rotation }
        if let stabilization = control.stabilization, (state.stabilizationModes ?? ["off"]).contains(stabilization) {
            next.stabilization = stabilization
        }
        return next
    }

    /// Whether a stored state can be used to start the camera.
    static func isValid(_ state: CameraState, cameraIds: [String], presets: [VideoPreset] = presets) -> Bool {
        cameraIds.contains(state.cameraId)
            && presets.contains { $0.width == state.width && $0.height == state.height && $0.fps.contains(state.fps) }
            && bitrateRange.contains(state.bitrateKbps)
            && rotations.contains(state.rotation)
    }

    /// The stored state if it is usable, otherwise the defaults.
    static func sanitized(_ stored: CameraState?, cameraIds: [String], presets: [VideoPreset] = presets) -> CameraState {
        guard let stored, isValid(stored, cameraIds: cameraIds, presets: presets) else { return defaultState }
        return stored
    }

    /// Portrait rotations swap width and height.
    static func outputSize(for state: CameraState) -> OutputSize {
        let width = Int32(clamping: state.width)
        let height = Int32(clamping: state.height)
        let rotated = state.rotation == 90 || state.rotation == 270
        return rotated ? OutputSize(width: height, height: width) : OutputSize(width: width, height: height)
    }
}
