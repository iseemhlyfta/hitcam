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
    private var reconnectWork: DispatchWorkItem?
    private var lastReceived = Date()
    private var userStopped = true
    private var isStreaming = false
    private var needKeyframe = true
    private var sentToken: String?

    // Owned by `queue`: the session's copy of the camera state and the matching encoder size.
    private var state: CameraState
    private var outputSize: OutputSize

    // Statistics, owned by `queue`.
    private var droppedFrames = 0
    private var framesThisSecond = 0
    private var bytesThisSecond = 0
    private var lastFps: Double = 0
    private var lastKbps = 0
    private var batteryLevel: Double = 0
    private var isCharging = false

    init() {
        let stored = LocalStore.cameraState
        let initial = CameraRules.sanitized(stored, cameraIds: CameraController.availableCameraIds)
        if stored != nil, stored != initial {
            // Saved by an older version or set to something this phone can't do.
            LocalStore.cameraState = initial
        }
        state = initial
        outputSize = CameraRules.outputSize(for: initial)
        cameraState = initial
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
            guard case .pairing = self.currentPhase else { return }
            self.connection?.send(.pairRequest, json: PairRequest(pin: pin))
        }
    }

    func disconnect() {
        queue.async {
            self.userStopped = true
            // Keep the socket out of teardown so Bye is written before it closes.
            let old = self.connection
            self.connection = nil
            self.teardown()
            old?.close(sending: .bye, json: Bye(reason: "user"))
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
            guard !userStopped, address != nil else { return }
            if wasStreaming, let address {
                // Wi-Fi hiccup or PC restarted: keep trying while the app is open.
                setPhase(.reconnecting(address))
                scheduleReconnect()
            } else if case .reconnecting = currentPhase {
                scheduleReconnect()
            } else if case .failed = currentPhase {
                // Keep the more specific message from the handshake.
            } else {
                fail(error.map { L10n.connectionFailed($0.localizedDescription) } ?? L10n.connectionClosed)
            }
        }
    }

    /// Cancelled by `teardown`, so a new connection (another PC, a PIN being typed) is never replaced by a stale retry.
    private func scheduleReconnect() {
        reconnectWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, !self.userStopped else { return }
            self.reconnectWork = nil
            self.openConnection(reconnecting: true)
        }
        reconnectWork = work
        queue.asyncAfter(deadline: .now() + 2, execute: work)
    }

    private var currentPhase: Phase = .idle

    private func tokenKeys() -> [String] {
        guard let address else { return [] }
        return [address.serverId, "\(address.host):\(address.port)"].compactMap { $0 }
    }

    private func sendHello() {
        let token = tokenKeys().lazy.compactMap { TokenStore.token(for: $0) }.first
        sentToken = token
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
            if let expected = address?.serverId, expected != ack.serverId {
                // The QR code (or an earlier session) named another PC: stop here and send nothing more.
                return fail(L10n.otherPc)
            }
            if address?.serverId == nil { address?.serverId = ack.serverId }
            if address?.name == nil { address?.name = ack.serverName }
            switch ack.status {
            case HelloStatus.accepted:
                startStreaming(serverName: ack.serverName)
            case HelloStatus.pairingRequired:
                forgetRejectedToken()
                if let address { setPhase(.pairing(address, attemptsLeft: 5, wrongPin: false)) }
            case HelloStatus.busy:
                fail(L10n.busy)
            case HelloStatus.pairingLocked:
                fail(L10n.pairingLocked)
            default:
                fail(L10n.versionMismatch)
            }
        case .pairResult:
            guard case .pairing = currentPhase,
                  let result = try? decoder.decode(PairResult.self, from: payload), let address else { return }
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
            // Only a paired PC may control the camera.
            guard isStreaming, let control = try? decoder.decode(Control.self, from: payload) else { return }
            handleControl(control)
        case .requestKeyframe:
            guard isStreaming else { return }
            encoder.requestKeyframe()
        case .bye:
            fail(L10n.closedByPc)
        default:
            break
        }
    }

    /// The PC asked for a PIN although a token was sent: it was revoked there, so drop it here too.
    private func forgetRejectedToken() {
        guard let sent = sentToken else { return }
        sentToken = nil
        for key in tokenKeys() where TokenStore.token(for: key) == sent {
            TokenStore.remove(for: key)
        }
    }

    private func pongPayload(echo timestamp: UInt64) -> Data {
        var data = Data()
        data.appendLittleEndian(timestamp)
        return data
    }

    // MARK: Streaming (queue)

    private func startStreaming(serverName: String) {
        guard let address, let id = connectionId else { return }
        LocalStore.remember(address)
        refreshBattery()
        startCamera(with: state, connection: id, address: address, serverName: serverName)
    }

    private func startCamera(with initial: CameraState, connection id: UUID, address: ServerAddress, serverName: String) {
        camera.start(initial: initial) { [weak self] result in
            guard let self else { return }
            self.queue.async {
                // The connection dropped or the user left while the camera was starting.
                // Nothing to undo: teardown queued camera.stop() after this start.
                guard self.connectionId == id, !self.userStopped else { return }
                switch result {
                case .failure(let error):
                    if initial != CameraRules.defaultState {
                        // The saved settings don't work on this phone: start over from the defaults.
                        LocalStore.cameraState = nil
                        self.startCamera(with: CameraRules.defaultState, connection: id, address: address, serverName: serverName)
                    } else {
                        self.fail(L10n.cameraFailed(error.localizedDescription))
                    }
                case .success(let snapshot):
                    self.state = snapshot.state
                    self.outputSize = snapshot.outputSize
                    self.publish(snapshot.state)
                    do {
                        try self.configureEncoder()
                    } catch {
                        return self.fail(L10n.cameraFailed(error.localizedDescription))
                    }
                    self.connection?.send(.capabilities, json: self.capabilities)
                    self.connection?.send(.cameraState, json: snapshot.state)
                    self.isStreamingFlag = true
                    self.startTimers()
                    self.setPhase(.streaming(address, serverName: serverName))
                }
            }
        }
    }

    private func configureEncoder() throws {
        try encoder.configure(width: outputSize.width, height: outputSize.height, fps: state.fps, bitrateKbps: state.bitrateKbps)
        needKeyframe = true
        connection?.send(.streamConfig, json: StreamConfig(
            codec: "h264", width: Int(outputSize.width), height: Int(outputSize.height), fps: state.fps, bitrateKbps: state.bitrateKbps))
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
        camera.apply(control) { [weak self] update in
            guard let self else { return }
            self.queue.async {
                let state = update.snapshot.state
                self.state = state
                self.outputSize = update.snapshot.outputSize
                self.publish(state)
                // A rejected format was rolled back; only a state the camera accepted is remembered.
                if !update.failed, CameraRules.isValid(state, cameraIds: CameraController.availableCameraIds) {
                    LocalStore.cameraState = state
                }
                guard self.isStreaming else { return }
                if update.formatChanged {
                    do {
                        try self.configureEncoder()
                    } catch {
                        return self.fail(L10n.cameraFailed(error.localizedDescription))
                    }
                } else if update.bitrateChanged {
                    // The PC reads the new bitrate from the camera state below; no new StreamConfig needed.
                    self.encoder.setBitrate(kbps: state.bitrateKbps)
                }
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
            self.lastFps = fps
            self.lastKbps = kbps
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

    /// UIDevice is read on the main thread; the values reach `queue` a moment later.
    private func refreshBattery() {
        DispatchQueue.main.async { [weak self] in
            let device = UIDevice.current
            let level = Double(max(device.batteryLevel, 0))
            let charging = device.batteryState == .charging || device.batteryState == .full
            guard let self else { return }
            self.queue.async {
                self.batteryLevel = level
                self.isCharging = charging
            }
        }
    }

    private func sendStatus() {
        let thermal: String
        switch ProcessInfo.processInfo.thermalState {
        case .nominal: thermal = "nominal"
        case .fair: thermal = "fair"
        case .serious: thermal = "serious"
        case .critical: thermal = "critical"
        @unknown default: thermal = "nominal"
        }
        let status = DeviceStatus(
            battery: batteryLevel, charging: isCharging,
            thermal: thermal, fps: lastFps, bitrateKbps: lastKbps, droppedFrames: droppedFrames)
        connection?.send(.status, json: status)
        refreshBattery()
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
        reconnectWork?.cancel()
        reconnectWork = nil
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
