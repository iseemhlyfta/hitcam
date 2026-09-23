import AVFoundation
import Foundation

/// Owns the AVCaptureSession. All configuration happens on `sessionQueue`; frames are delivered on `videoQueue`.
final class CameraController: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    static let presets = [
        VideoPreset(width: 1280, height: 720, fps: [30, 60]),
        VideoPreset(width: 1920, height: 1080, fps: [30, 60]),
    ]

    let session = AVCaptureSession()
    private let sessionQueue = DispatchQueue(label: "hitcam.camera.session")
    private let videoQueue = DispatchQueue(label: "hitcam.camera.video", qos: .userInteractive)
    private let output = AVCaptureVideoDataOutput()
    private var input: AVCaptureDeviceInput?
    private var device: AVCaptureDevice?

    /// Called on the video queue for every captured frame.
    var onFrame: ((CMSampleBuffer) -> Void)?

    private(set) var state = CameraState(
        cameraId: "back-wide", zoom: 1, torch: false, focusMode: "continuous", lensPosition: 0.5,
        exposureBias: 0, mirror: false, rotation: 0, width: 1920, height: 1080, fps: 30, bitrateKbps: 8000)

    // MARK: Cameras

    private static let cameraMap: [(id: String, type: AVCaptureDevice.DeviceType, position: AVCaptureDevice.Position, name: String)] = [
        ("back-wide", .builtInWideAngleCamera, .back, "Wide"),
        ("back-ultrawide", .builtInUltraWideCamera, .back, "Ultra Wide"),
        ("back-tele", .builtInTelephotoCamera, .back, "Telephoto"),
        ("front", .builtInWideAngleCamera, .front, "Front"),
    ]

    static func availableCameras() -> [CameraInfo] {
        cameraMap.compactMap { entry in
            guard let device = AVCaptureDevice.default(entry.type, for: .video, position: entry.position) else { return nil }
            return CameraInfo(
                id: entry.id,
                name: entry.name,
                position: entry.position == .front ? "front" : "back",
                minZoom: Double(device.minAvailableVideoZoomFactor),
                maxZoom: Double(min(device.maxAvailableVideoZoomFactor, 10)),
                hasTorch: device.hasTorch,
                supportsFocus: device.isFocusModeSupported(.locked) && device.isLockingFocusWithCustomLensPositionSupported)
        }
    }

    static func capabilities() -> Capabilities {
        Capabilities(cameras: availableCameras(), presets: presets)
    }

    private static func device(for id: String) -> AVCaptureDevice? {
        let entry = cameraMap.first { $0.id == id } ?? cameraMap[0]
        return AVCaptureDevice.default(entry.type, for: .video, position: entry.position)
    }

    // MARK: Lifecycle

    func start(initial: CameraState, completion: @escaping (Result<CameraState, Error>) -> Void) {
        sessionQueue.async {
            do {
                self.state = initial
                try self.configureSession()
                if !self.session.isRunning { self.session.startRunning() }
                completion(.success(self.state))
            } catch {
                completion(.failure(error))
            }
        }
    }

    func stop() {
        sessionQueue.async {
            self.setTorch(false)
            if self.session.isRunning { self.session.stopRunning() }
        }
    }

    /// Applies a control request; `completion` gets the new state and whether the stream format changed.
    func apply(_ control: Control, completion: @escaping (CameraState, Bool) -> Void) {
        sessionQueue.async {
            let before = self.state
            var next = self.state
            if let id = control.cameraId { next.cameraId = id }
            if let width = control.width, let height = control.height { next.width = width; next.height = height }
            if let fps = control.fps { next.fps = fps }
            if let bitrate = control.bitrateKbps { next.bitrateKbps = max(500, min(bitrate, 50_000)) }
            if let mirror = control.mirror { next.mirror = mirror }
            if let rotation = control.rotation, [0, 90, 180, 270].contains(rotation) { next.rotation = rotation }

            let needsReconfigure = next.cameraId != before.cameraId || next.width != before.width
                || next.height != before.height || next.fps != before.fps
            self.state = next
            if needsReconfigure {
                try? self.configureSession()
            } else if next.mirror != before.mirror || next.rotation != before.rotation {
                self.configureConnection()
            }

            if let zoom = control.zoom { self.setZoom(zoom) }
            if let torch = control.torch { self.setTorch(torch) }
            if let exposure = control.exposureBias { self.setExposureBias(exposure) }
            if let point = control.focusPoint {
                self.focus(at: point)
            } else if let mode = control.focusMode {
                self.setFocus(mode: mode, lensPosition: control.lensPosition)
            } else if let lens = control.lensPosition {
                self.setFocus(mode: "locked", lensPosition: lens)
            }

            let formatChanged = needsReconfigure || next.mirror != before.mirror || next.rotation != before.rotation
                || next.bitrateKbps != before.bitrateKbps
            completion(self.state, formatChanged)
        }
    }

    /// Output dimensions after rotation (portrait rotations swap width and height).
    var outputSize: (width: Int32, height: Int32) {
        let rotated = state.rotation == 90 || state.rotation == 270
        return rotated ? (Int32(state.height), Int32(state.width)) : (Int32(state.width), Int32(state.height))
    }

    // MARK: Configuration (session queue)

    private func configureSession() throws {
        guard let device = Self.device(for: state.cameraId) else {
            throw NSError(domain: "HitCam", code: 1, userInfo: [NSLocalizedDescriptionKey: "Camera not available"])
        }

        session.beginConfiguration()
        defer { session.commitConfiguration() }
        session.sessionPreset = .inputPriority

        if let input { session.removeInput(input) }
        let newInput = try AVCaptureDeviceInput(device: device)
        guard session.canAddInput(newInput) else {
            throw NSError(domain: "HitCam", code: 2, userInfo: [NSLocalizedDescriptionKey: "Cannot use this camera"])
        }
        session.addInput(newInput)
        input = newInput
        self.device = device

        if !session.outputs.contains(output) {
            output.alwaysDiscardsLateVideoFrames = true
            output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange]
            output.setSampleBufferDelegate(self, queue: videoQueue)
            if session.canAddOutput(output) { session.addOutput(output) }
        }

        try selectFormat(device: device)
        configureConnection()
        state.zoom = Double(device.videoZoomFactor)
        state.torch = false
    }

    /// Picks a format with the requested size and frame rate, falling back to the requested size at 30 fps.
    private func selectFormat(device: AVCaptureDevice) throws {
        func matches(_ format: AVCaptureDevice.Format, fps: Int) -> Bool {
            let dims = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            let subtype = CMFormatDescriptionGetMediaSubType(format.formatDescription)
            return Int(dims.width) == state.width && Int(dims.height) == state.height
                && subtype == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
                && format.videoSupportedFrameRateRanges.contains { $0.maxFrameRate >= Double(fps) && $0.minFrameRate <= Double(fps) }
        }

        var fps = state.fps
        var format = device.formats.first { matches($0, fps: fps) }
        if format == nil, fps > 30 {
            fps = 30
            format = device.formats.first { matches($0, fps: fps) }
        }
        guard let format else {
            throw NSError(domain: "HitCam", code: 3, userInfo: [NSLocalizedDescriptionKey: "Unsupported resolution"])
        }

        try device.lockForConfiguration()
        defer { device.unlockForConfiguration() }
        device.activeFormat = format
        let duration = CMTime(value: 1, timescale: CMTimeScale(fps))
        device.activeVideoMinFrameDuration = duration
        device.activeVideoMaxFrameDuration = duration
        if device.isFocusModeSupported(.continuousAutoFocus) { device.focusMode = .continuousAutoFocus }
        if device.isExposureModeSupported(.continuousAutoExposure) { device.exposureMode = .continuousAutoExposure }
        state.fps = fps
        state.focusMode = "continuous"
        state.exposureBias = 0
    }

    private func configureConnection() {
        guard let connection = output.connection(with: .video) else { return }
        let angle = CGFloat(state.rotation)
        if connection.isVideoRotationAngleSupported(angle) { connection.videoRotationAngle = angle }
        if connection.isVideoMirroringSupported {
            connection.automaticallyAdjustsVideoMirroring = false
            connection.isVideoMirrored = state.mirror
        }
    }

    private func withDevice(_ body: (AVCaptureDevice) -> Void) {
        guard let device, (try? device.lockForConfiguration()) != nil else { return }
        body(device)
        device.unlockForConfiguration()
    }

    private func setZoom(_ zoom: Double) {
        withDevice { device in
            let limit = min(device.maxAvailableVideoZoomFactor, 10)
            let factor = max(device.minAvailableVideoZoomFactor, min(CGFloat(zoom), limit))
            device.videoZoomFactor = factor
            state.zoom = Double(factor)
        }
    }

    private func setTorch(_ on: Bool) {
        withDevice { device in
            guard device.hasTorch, device.isTorchModeSupported(on ? .on : .off) else { return }
            device.torchMode = on ? .on : .off
            state.torch = on
        }
    }

    private func setExposureBias(_ bias: Double) {
        withDevice { device in
            let value = max(device.minExposureTargetBias, min(Float(bias), device.maxExposureTargetBias))
            device.setExposureTargetBias(value, completionHandler: nil)
            state.exposureBias = Double(value)
        }
    }

    private func setFocus(mode: String, lensPosition: Double?) {
        withDevice { device in
            switch mode {
            case "locked":
                let lens = Float(max(0, min(lensPosition ?? state.lensPosition, 1)))
                guard device.isLockingFocusWithCustomLensPositionSupported else { return }
                device.setFocusModeLocked(lensPosition: lens, completionHandler: nil)
                state.lensPosition = Double(lens)
            case "auto":
                guard device.isFocusModeSupported(.autoFocus) else { return }
                device.focusMode = .autoFocus
            default:
                guard device.isFocusModeSupported(.continuousAutoFocus) else { return }
                device.focusMode = .continuousAutoFocus
            }
            state.focusMode = mode
        }
    }

    private func focus(at point: NormalizedPoint) {
        withDevice { device in
            let target = CGPoint(x: max(0, min(point.x, 1)), y: max(0, min(point.y, 1)))
            if device.isFocusPointOfInterestSupported, device.isFocusModeSupported(.autoFocus) {
                device.focusPointOfInterest = target
                device.focusMode = .autoFocus
                state.focusMode = "auto"
            }
            if device.isExposurePointOfInterestSupported, device.isExposureModeSupported(.autoExpose) {
                device.exposurePointOfInterest = target
                device.exposureMode = .autoExpose
            }
        }
    }

    // MARK: AVCaptureVideoDataOutputSampleBufferDelegate

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        onFrame?(sampleBuffer)
    }
}
