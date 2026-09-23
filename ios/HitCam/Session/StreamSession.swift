import AVFoundation
import Combine
import UIKit

/// Drives one connection to a PC: handshake, pairing, streaming, remote control and reconnects.
/// Published state is updated on the main thread; networking runs on `queue`.
final class StreamSession: ObservableObject {
    enum Phase: Equatable {
        case idle
        case connecting(ServerAddress)
        case pairing(ServerAddress, attemptsLeft: Int, wrongPin: Bool)
        case streaming(ServerAddress, serverName: String)
        case reconnecting(ServerAddress)
        case failed(String)
    }

    @Published private(set) var phase: Phase = .idle
    @Published private(set) var cameraState: CameraState
    @Published private(set) var sentFps: Double = 0
    @Published private(set) var sentKbps: Int = 0

    let camera = CameraController()
    let capabilities = CameraController.capabilities()

    private let queue = DispatchQueue(label: "hitcam.session", qos: .userInitiated)
    private var connection: FramedConnection?
    private var connectionId: UUID?
    private var address: ServerAddress?
    private var encoder: H264Encoder!
    private var timers: [DispatchSourceTimer] = []
    private var lastReceived = Date()
    private var userStopped = true
    private var isStreaming = false
    private var needKeyframe = true
    private var droppedFrames = 0
    private var framesThisSecond = 0
    private var bytesThisSecond = 0

    init() {
        cameraState = LocalStore.cameraState ?? CameraState(
            cameraId: "back-wide", zoom: 1, torch: false, focusMode: "continuous", lensPosition: 0.5,
            exposureBias: 0, mirror: false, rotation: 0, width: 1920, height: 1080, fps: 30, bitrateKbps: 8000)
        encoder = H264Encoder { [weak self] frame in
            self?.queue.async { self?.send(frame) }
        }
        camera.onFrame = { [weak self] sample in
            guard let self, self.isStreamingFlag else { return }
            self.encoder.encode(sample)
        }
        UIDevice.current.isBatteryMonitoringEnabled = true
    }

    // Read from the capture thread; written only on `queue`.
    private var isStreamingFlag: Bool {
        get { streamingLock.withLock { isStreaming } }
        set { streamingLock.withLock { isStreaming = newValue } }
    }
    private let streamingLock = NSLock()

    // MARK: Public API (main thread)

    func connect(to address: ServerAddress) {
        queue.async {
            self.userStopped = false
            self.address = address
            self.openConnection(reconnecting: false)
        }
    }

    func submitPin(_ pin: String) {
        queue.async {
            self.connection?.send(.pairRequest, json: PairRequest(pin: pin))
        }
    }

    func disconnect() {
        queue.async {
            self.userStopped = true
            self.connection?.send(.bye, json: Bye(reason: "user"))
            self.teardown()
            self.setPhase(.idle)
        }
    }

    /// Local controls from the phone UI go through the same path as controls from the PC.
    func apply(_ control: Control) {
        queue.async { self.handleControl(control) }
    }

    // MARK: Connection lifecycle (queue)

    private func openConnection(reconnecting: Bool) {
        guard let address else { return }
        teardown()
        setPhase(reconnecting ? .reconnecting(address) : .connecting(address))

        // Events from an older, cancelled connection must not tear down this one.
        let id = UUID()
        connectionId = id
        let connection = FramedConnection(address: address, queue: queue) { [weak self] event in
            guard let self, self.connectionId == id else { return }
            self.handle(event)
        }
        self.connection = connection
        connection.start()
    }

    private func handle(_ event: FramedConnection.Event) {
        switch event {
        case .ready:
            lastReceived = Date()
            sendHello()
        case .message(let header, let payload):
            lastReceived = Date()
            handleMessage(header, payload)
        case .closed(let error):
            let wasStreaming = isStreaming
            connectionId = nil
            teardown()
            guard !userStopped, let address else { return }
            if wasStreaming {
                // Wi-Fi hiccup or PC restarted: keep trying while the app is open.
                setPhase(.reconnecting(address))
                queue.asyncAfter(deadline: .now() + 2) { [weak self] in
                    guard let self, !self.userStopped else { return }
                    self.openConnection(reconnecting: true)
                }
            } else if case .reconnecting = currentPhase {
                queue.asyncAfter(deadline: .now() + 2) { [weak self] in
                    guard let self, !self.userStopped else { return }
                    self.openConnection(reconnecting: true)
                }
            } else if case .failed = currentPhase {
                // Keep the more specific message from the handshake.
            } else {
                fail(error.map { L10n.connectionFailed($0.localizedDescription) } ?? L10n.connectionClosed)
            }
        }
    }

    private var currentPhase: Phase = .idle

    private func tokenKeys() -> [String] {
        guard let address else { return [] }
        return [address.serverId, "\(address.host):\(address.port)"].compactMap { $0 }
    }

    private func sendHello() {
        let token = tokenKeys().lazy.compactMap { TokenStore.token(for: $0) }.first
        let hello = Hello(
            protocolVersion: ProtocolInfo.version,
            deviceId: LocalStore.deviceId,
            deviceName: UIDevice.current.name,
            model: LocalStore.modelIdentifier,
            appVersion: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String,
            token: token)
        connection?.send(.hello, json: hello)
    }

    private func handleMessage(_ header: MessageHeader, _ payload: Data) {
        let decoder = JSONDecoder()
        switch header.type {
        case .helloAck:
            guard let ack = try? decoder.decode(HelloAck.self, from: payload) else { return fail(L10n.protocolError) }
            if address?.serverId == nil { address?.serverId = ack.serverId }
            if address?.name == nil { address?.name = ack.serverName }
            switch ack.status {
            case HelloStatus.accepted:
                startStreaming(serverName: ack.serverName)
            case HelloStatus.pairingRequired:
                if let address { setPhase(.pairing(address, attemptsLeft: 5, wrongPin: false)) }
            case HelloStatus.busy:
                fail(L10n.busy)
            case HelloStatus.pairingLocked:
                fail(L10n.pairingLocked)
            default:
                fail(L10n.versionMismatch)
            }
        case .pairResult:
            guard let result = try? decoder.decode(PairResult.self, from: payload), let address else { return }
            if result.ok, let token = result.token {
                tokenKeys().forEach { TokenStore.save(token, for: $0) }
                startStreaming(serverName: address.name ?? address.host)
            } else if result.attemptsLeft > 0 {
                setPhase(.pairing(address, attemptsLeft: result.attemptsLeft, wrongPin: true))
            } else {
                fail(L10n.pairingLocked)
            }
        case .ping:
            connection?.send(.pong, payload: pongPayload(echo: header.timestamp))
        case .control:
            if let control = try? decoder.decode(Control.self, from: payload) { handleControl(control) }
        case .requestKeyframe:
            encoder.requestKeyframe()
        case .bye:
            fail(L10n.closedByPc)
        default:
            break
        }
    }

    private func pongPayload(echo timestamp: UInt64) -> Data {
        var data = Data()
        data.appendLittleEndian(timestamp)
        return data
    }

    // MARK: Streaming (queue)

    private func startStreaming(serverName: String) {
        guard let address else { return }
        LocalStore.remember(address)
        camera.start(initial: cameraState) { [weak self] result in
            guard let self else { return }
            self.queue.async {
                switch result {
                case .failure(let error):
                    self.fail(L10n.cameraFailed(error.localizedDescription))
                case .success(let state):
                    self.publish(state)
                    do {
                        try self.configureEncoder()
                    } catch {
                        return self.fail(L10n.cameraFailed(error.localizedDescription))
                    }
                    self.connection?.send(.capabilities, json: self.capabilities)
                    self.connection?.send(.cameraState, json: state)
                    self.isStreamingFlag = true
                    self.startTimers()
                    self.setPhase(.streaming(address, serverName: serverName))
                }
            }
        }
    }

    private func configureEncoder() throws {
        let size = camera.outputSize
        try encoder.configure(width: size.width, height: size.height, fps: cameraState.fps, bitrateKbps: cameraState.bitrateKbps)
        needKeyframe = true
        connection?.send(.streamConfig, json: StreamConfig(
            codec: "h264", width: Int(size.width), height: Int(size.height), fps: cameraState.fps, bitrateKbps: cameraState.bitrateKbps))
    }

    private func send(_ frame: EncodedFrame) {
        guard isStreaming, let connection else { return }

        // Low-latency rule: never let frames queue up. When the socket is congested, drop until a keyframe.
        if connection.framesInFlight >= 2 || (needKeyframe && !frame.isKeyframe) {
            droppedFrames += 1
            if !needKeyframe {
                needKeyframe = true
                encoder.requestKeyframe()
            }
            return
        }
        needKeyframe = false
        connection.send(.videoFrame, flags: frame.isKeyframe ? .keyframe : [], timestamp: frame.timestampMicros,
                        payload: frame.data, isVideo: true)
        framesThisSecond += 1
        bytesThisSecond += frame.data.count
    }

    private func handleControl(_ control: Control) {
        camera.apply(control) { [weak self] state, formatChanged in
            guard let self else { return }
            self.queue.async {
                self.publish(state)
                LocalStore.cameraState = state
                guard self.isStreaming else { return }
                if formatChanged { try? self.configureEncoder() }
                self.connection?.send(.cameraState, json: state)
            }
        }
    }

    private func startTimers() {
        stopTimers()
        timers.append(repeating(1) { [weak self] in
            guard let self else { return }
            self.connection?.send(.ping)
            let fps = Double(self.framesThisSecond)
            let kbps = self.bytesThisSecond * 8 / 1000
            self.framesThisSecond = 0
            self.bytesThisSecond = 0
            DispatchQueue.main.async {
                self.sentFps = fps
                self.sentKbps = kbps
            }
            if Date().timeIntervalSince(self.lastReceived) > 5 {
                self.connection?.cancel()   // liveness timeout -> reconnect path
            }
        })
        timers.append(repeating(2) { [weak self] in
            self?.sendStatus()
        })
    }

    private func sendStatus() {
        let device = UIDevice.current
        let thermal: String
        switch ProcessInfo.processInfo.thermalState {
        case .nominal: thermal = "nominal"
        case .fair: thermal = "fair"
        case .serious: thermal = "serious"
        case .critical: thermal = "critical"
        @unknown default: thermal = "nominal"
        }
        let status = DispatchQueue.main.sync {
            DeviceStatus(
                battery: Double(max(device.batteryLevel, 0)),
                charging: device.batteryState == .charging || device.batteryState == .full,
                thermal: thermal, fps: sentFps, bitrateKbps: sentKbps, droppedFrames: droppedFrames)
        }
        connection?.send(.status, json: status)
    }

    private func repeating(_ seconds: Double, _ body: @escaping () -> Void) -> DispatchSourceTimer {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + seconds, repeating: seconds)
        timer.setEventHandler(handler: body)
        timer.resume()
        return timer
    }

    private func stopTimers() {
        timers.forEach { $0.cancel() }
        timers.removeAll()
    }

    private func teardown() {
        isStreamingFlag = false
        stopTimers()
        encoder.invalidate()
        camera.stop()
        let old = connection
        connection = nil
        connectionId = nil
        old?.cancel()
    }

    // MARK: State publishing

    private func fail(_ message: String) {
        userStopped = true
        teardown()
        setPhase(.failed(message))
    }

    private func setPhase(_ phase: Phase) {
        currentPhase = phase
        DispatchQueue.main.async {
            self.phase = phase
            UIApplication.shared.isIdleTimerDisabled = {
                if case .streaming = phase { return true }
                if case .reconnecting = phase { return true }
                return false
            }()
        }
    }

    private func publish(_ state: CameraState) {
        DispatchQueue.main.async { self.cameraState = state }
    }
}
