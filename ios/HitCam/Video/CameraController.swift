import AVFoundation
import Foundation

/// Owns the AVCaptureSession. All configuration happens on `sessionQueue`; frames are delivered on `videoQueue`.
final class CameraController: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    static let presets = CameraRules.presets
    /// Ids of the cameras this phone has; controls naming other ids are ignored.
    static let availableCameraIds = CameraController.availableCameras().map(\.id)

    let session = AVCaptureSession()
    private let sessionQueue = DispatchQueue(label: "hitcam.camera.session")
    private let videoQueue = DispatchQueue(label: "hitcam.camera.video", qos: .userInteractive)
    private let output = AVCaptureVideoDataOutput()
    private var input: AVCaptureDeviceInput?
    private var device: AVCaptureDevice?

    /// Keeps the stream level with the horizon; only landscape angles (0/180) are used so the frame size never changes.
    private var rotationCoordinator: AVCaptureDevice.RotationCoordinator?
    private var rotationObservation: NSKeyValueObservation?
    private var landscapeAngle = 0
    /// Set on the main thread by the preview view; its rotation follows `landscapeAngle`.
    private weak var previewLayer: AVCaptureVideoPreviewLayer?

    /// Called on the video queue for every captured frame.
    var onFrame: ((CMSampleBuffer) -> Void)?

    /// Owned by `sessionQueue`; other threads get copies through completions.
    private var state = CameraRules.defaultState

    /// A consistent copy of the camera state and the matching encoder size.
    struct Snapshot {
        var state: CameraState
        var outputSize: OutputSize
    }

    /// Result of a control request.
    struct Update {
        var snapshot: Snapshot
        /// Frame size or rate changed: the encoder must be recreated.
        var formatChanged: Bool
        var bitrateChanged: Bool
        /// The requested format could not be applied and the previous one was restored.
        var failed: Bool
    }

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
                supportsFocus: device.isFocusModeSupported(.locked) && device.isLockingFocusWithCustomLensPositionSupported,
                supportsWhiteBalance: device.isWhiteBalanceModeSupported(.locked) && device.isLockingWhiteBalanceWithCustomDeviceGainsSupported,
                supportsExposureLock: device.isExposureModeSupported(.locked))
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

    func start(initial: CameraState, completion: @escaping (Result<Snapshot, Error>) -> Void) {
        sessionQueue.async {
            do {
                self.state = initial
                try self.configureSession()
                if !self.session.isRunning { self.session.startRunning() }
                completion(.success(self.snapshot))
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

    /// Applies a control request; `completion` gets the resulting state and what changed.
    func apply(_ control: Control, completion: @escaping (Update) -> Void) {
        sessionQueue.async {
            let before = self.state
            let next = CameraRules.applying(control, to: before, cameraIds: Self.availableCameraIds)

            let needsReconfigure = next.cameraId != before.cameraId || next.width != before.width
                || next.height != before.height || next.fps != before.fps
            self.state = next
            var failed = false
            if needsReconfigure {
                do {
                    try self.configureSession()
                } catch {
                    // Not supported by this device after all: go back to the configuration that worked.
                    failed = true
                    self.state = before
                    try? self.configureSession()
                }
            } else if next.mirror != before.mirror || next.rotation != before.rotation || next.stabilization != before.stabilization {
                self.configureConnection()
            }

            if control.whiteBalanceMode != nil || control.whiteBalanceTemperature != nil || control.whiteBalanceTint != nil {
                // A temperature or tint alone means "lock at this value".
                self.setWhiteBalance(mode: control.whiteBalanceMode ?? "locked",
                                     temperature: control.whiteBalanceTemperature, tint: control.whiteBalanceTint)
            }
            if let mode = control.exposureMode { self.setExposureMode(mode) }

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

            // Mirroring and 180° turns change the picture, not the frame format, so the encoder keeps running.
            let snapshot = self.snapshot
            let formatChanged = snapshot.outputSize != CameraRules.outputSize(for: before) || snapshot.state.fps != before.fps
            completion(Update(snapshot: snapshot, formatChanged: formatChanged,
                              bitrateChanged: snapshot.state.bitrateKbps != before.bitrateKbps, failed: failed))
        }
    }

    /// Session queue only.
    private var snapshot: Snapshot {
        Snapshot(state: state, outputSize: CameraRules.outputSize(for: state))
    }

    /// Attaches the on-screen preview (main thread).
    func attachPreview(_ layer: AVCaptureVideoPreviewLayer) {
        previewLayer = layer
        sessionQueue.async { self.applyPreviewAngle() }
    }

    // MARK: Configuration (session queue)

    private func configureSession() throws {
        guard let device = Self.device(for: state.cameraId) else {
            throw NSError(domain: "HitCam", code: 1, userInfo: [NSLocalizedDescriptionKey: "Camera not available"])
        }

        session.beginConfiguration()
        defer { session.commitConfiguration() }
        session.sessionPreset = .inputPriority

        if let input {
            session.removeInput(input)
            self.input = nil
        }
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
        observeRotation(of: device)
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
        if device.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) { device.whiteBalanceMode = .continuousAutoWhiteBalance }
        state.fps = fps
        state.focusMode = "continuous"
        state.exposureBias = 0
        state.exposureMode = "auto"
        state.whiteBalanceMode = "auto"
        let current = device.temperatureAndTintValues(for: device.deviceWhiteBalanceGains)
        state.whiteBalanceTemperature = Double(current.temperature).rounded()
        state.whiteBalanceTint = Double(current.tint).rounded()

        // Stabilization depends on the format; keep the requested mode only if this one supports it.
        let modes = Self.stabilizationModes.filter { $0.id == "off" || format.isVideoStabilizationModeSupported($0.mode) }
        state.stabilizationModes = modes.map(\.id)
        if !(state.stabilizationModes ?? []).contains(state.stabilization ?? "off") { state.stabilization = "off" }
        if state.stabilization == nil { state.stabilization = "off" }
    }

    // Standard adds little latency; cinematic smooths more but delays frames noticeably.
    private static let stabilizationModes: [(id: String, mode: AVCaptureVideoStabilizationMode)] = [
        ("off", .off),
        ("standard", .standard),
        ("cinematic", .cinematic),
    ]

    private func observeRotation(of device: AVCaptureDevice) {
        let coordinator = AVCaptureDevice.RotationCoordinator(device: device, previewLayer: nil)
        rotationCoordinator = coordinator
        updateLandscapeAngle(coordinator.videoRotationAngleForHorizonLevelCapture)
        rotationObservation = coordinator.observe(\.videoRotationAngleForHorizonLevelCapture, options: [.new]) { [weak self] coordinator, _ in
            let angle = coordinator.videoRotationAngleForHorizonLevelCapture
            self?.sessionQueue.async {
                guard let self, self.rotationCoordinator === coordinator, self.updateLandscapeAngle(angle) else { return }
                self.configureConnection()
            }
        }
    }

    /// Accepts only landscape angles: in portrait or lying flat the last landscape orientation is kept.
    @discardableResult
    private func updateLandscapeAngle(_ angle: CGFloat) -> Bool {
        let rounded = (Int(angle.rounded()) % 360 + 360) % 360
        guard rounded == 0 || rounded == 180, rounded != landscapeAngle else { return false }
        landscapeAngle = rounded
        return true
    }

    private func configureConnection() {
        applyPreviewAngle()
        guard let connection = output.connection(with: .video) else { return }
        let angle = CGFloat((landscapeAngle + state.rotation) % 360)
        if connection.isVideoRotationAngleSupported(angle) { connection.videoRotationAngle = angle }
        if connection.isVideoMirroringSupported {
            connection.automaticallyAdjustsVideoMirroring = false
            connection.isVideoMirrored = state.mirror
        }
        if connection.isVideoStabilizationSupported {
            let mode = Self.stabilizationModes.first { $0.id == state.stabilization }?.mode ?? .off
            connection.preferredVideoStabilizationMode = mode
        }
    }

    /// The preview is shown on a landscape-only screen, so it uses the same horizon-level angle as the stream.
    private func applyPreviewAngle() {
        let angle = CGFloat(landscapeAngle)
        DispatchQueue.main.async { [weak self] in
            guard let connection = self?.previewLayer?.connection, connection.isVideoRotationAngleSupported(angle) else { return }
            connection.videoRotationAngle = angle
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

    /// "locked": fixed gains for the given temperature/tint (missing values keep the current ones); otherwise continuous auto.
    private func setWhiteBalance(mode: String, temperature: Double?, tint: Double?) {
        withDevice { device in
            if mode == "locked" {
                guard device.isWhiteBalanceModeSupported(.locked), device.isLockingWhiteBalanceWithCustomDeviceGainsSupported else { return }
                let current = device.temperatureAndTintValues(for: device.deviceWhiteBalanceGains)
                let values = AVCaptureDevice.WhiteBalanceTemperatureAndTintValues(
                    temperature: Float(max(2000, min(temperature ?? Double(current.temperature), 10_000))),
                    tint: Float(max(-150, min(tint ?? Double(current.tint), 150))))
                var gains = device.deviceWhiteBalanceGains(for: values)
                // Gains outside [1, max] throw; clamping keeps the closest achievable color.
                let maxGain = device.maxWhiteBalanceGain
                gains.redGain = max(1, min(gains.redGain, maxGain))
                gains.greenGain = max(1, min(gains.greenGain, maxGain))
                gains.blueGain = max(1, min(gains.blueGain, maxGain))
                device.setWhiteBalanceModeLocked(with: gains, completionHandler: nil)
                state.whiteBalanceMode = "locked"
                state.whiteBalanceTemperature = Double(values.temperature).rounded()
                state.whiteBalanceTint = Double(values.tint).rounded()
            } else {
                guard device.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) else { return }
                device.whiteBalanceMode = .continuousAutoWhiteBalance
                state.whiteBalanceMode = "auto"
            }
        }
    }

    /// "locked" freezes the current exposure (bias no longer applies); otherwise continuous auto.
    private func setExposureMode(_ mode: String) {
        withDevice { device in
            if mode == "locked" {
                guard device.isExposureModeSupported(.locked) else { return }
                device.exposureMode = .locked
                state.exposureMode = "locked"
            } else {
                guard device.isExposureModeSupported(.continuousAutoExposure) else { return }
                device.exposureMode = .continuousAutoExposure
                state.exposureMode = "auto"
            }
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
                state.exposureMode = "auto"
            }
        }
    }

    // MARK: AVCaptureVideoDataOutputSampleBufferDelegate

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        onFrame?(sampleBuffer)
    }
}
