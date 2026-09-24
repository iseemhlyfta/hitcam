package io.github.hitnes.hitcam.session

import io.github.hitnes.hitcam.camera.CameraRules
import io.github.hitnes.hitcam.camera.OutputSize
import io.github.hitnes.hitcam.net.FramedConnection
import io.github.hitnes.hitcam.protocol.Bye
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.Capabilities
import io.github.hitnes.hitcam.protocol.Control
import io.github.hitnes.hitcam.protocol.DeviceStatus
import io.github.hitnes.hitcam.protocol.Hello
import io.github.hitnes.hitcam.protocol.HelloAck
import io.github.hitnes.hitcam.protocol.HelloStatus
import io.github.hitnes.hitcam.protocol.MessageFlags
import io.github.hitnes.hitcam.protocol.MessageHeader
import io.github.hitnes.hitcam.protocol.MessageType
import io.github.hitnes.hitcam.protocol.PairRequest
import io.github.hitnes.hitcam.protocol.PairResult
import io.github.hitnes.hitcam.protocol.ProtocolInfo
import io.github.hitnes.hitcam.protocol.ProtocolJson
import io.github.hitnes.hitcam.protocol.ServerAddress
import io.github.hitnes.hitcam.protocol.StreamConfig
import io.github.hitnes.hitcam.protocol.pongPayload
import io.github.hitnes.hitcam.video.EncodedFrame
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

/** Why a session ended; the UI turns it into text. */
sealed interface SessionError {
    data class ConnectionFailed(val reason: String) : SessionError
    data object ConnectionClosed : SessionError
    data object ProtocolError : SessionError
    data object OtherPc : SessionError
    data object Busy : SessionError
    data object PairingLocked : SessionError
    data object VersionMismatch : SessionError
    data object ClosedByPc : SessionError
    data class CameraFailed(val reason: String) : SessionError
}

sealed interface Phase {
    data object Idle : Phase
    data class Connecting(val address: ServerAddress) : Phase
    data class Pairing(val address: ServerAddress, val attemptsLeft: Int, val wrongPin: Boolean) : Phase
    data class Streaming(val address: ServerAddress, val serverName: String) : Phase
    data class Reconnecting(val address: ServerAddress) : Phase
    data class Failed(val error: SessionError) : Phase
}

/**
 * Drives one connection to a PC: handshake, pairing, streaming, remote control and reconnects.
 * All state lives on one thread ([queue]); the UI observes [phase], [cameraState] and the send statistics.
 */
class StreamSession(
    private val video: VideoSource,
    private val environment: SessionEnvironment,
    private val reconnectDelayMs: Long = 2_000,
) {
    private val queue = Executors.newSingleThreadScheduledExecutor { Thread(it, "hitcam-session") }

    private val _phase = MutableStateFlow<Phase>(Phase.Idle)
    val phase: StateFlow<Phase> = _phase.asStateFlow()
    private val _cameraState: MutableStateFlow<CameraState>
    val cameraState: StateFlow<CameraState>
    private val _sentFps = MutableStateFlow(0.0)
    val sentFps: StateFlow<Double> = _sentFps.asStateFlow()
    private val _sentKbps = MutableStateFlow(0)
    val sentKbps: StateFlow<Int> = _sentKbps.asStateFlow()

    val capabilities: Capabilities get() = video.capabilities

    // Owned by `queue`.
    private var connection: FramedConnection? = null
    private var connectionId = 0
    private var address: ServerAddress? = null
    private var timers = mutableListOf<ScheduledFuture<*>>()
    private var reconnect: ScheduledFuture<*>? = null
    private var lastReceived = 0L
    private var userStopped = true
    private var cameraPaused = false
    private var needKeyframe = true
    private var sentToken: String? = null
    private var serverName = ""
    private var state: CameraState
    private var outputSize: OutputSize

    // Read from the encoder thread; written only on `queue`.
    @Volatile private var isStreaming = false

    // Statistics, owned by `queue`.
    private var droppedFrames = 0
    private var framesThisSecond = 0
    private var bytesThisSecond = 0
    private var lastFps = 0.0
    private var lastKbps = 0

    init {
        val stored = environment.cameraState
        val initial = CameraRules.sanitized(stored, video.cameraIds)
        // Saved by an older version or set to something this phone can't do.
        if (stored != null && stored != initial) environment.cameraState = initial
        state = initial
        outputSize = CameraRules.outputSize(initial)
        _cameraState = MutableStateFlow(initial)
        cameraState = _cameraState.asStateFlow()
        video.onFrame = { frame -> if (isStreaming) queue.execute { send(frame) } }
        video.onError = { message -> queue.execute { if (isStreaming) fail(SessionError.CameraFailed(message)) } }
    }

    // MARK: Public API (any thread)

    fun connect(address: ServerAddress) = queue.execute {
        userStopped = false
        this.address = address
        openConnection(reconnecting = false)
    }

    fun submitPin(pin: String) = queue.execute {
        if (_phase.value is Phase.Pairing) connection?.send(MessageType.PairRequest, PairRequest.serializer(), PairRequest(pin))
    }

    fun disconnect() = queue.execute {
        userStopped = true
        // Keep the socket out of teardown so Bye is written before it closes.
        val old = connection
        connection = null
        teardown()
        old?.close(MessageType.Bye, Bye.serializer(), Bye("user"))
        setPhase(Phase.Idle)
    }

    /** Local controls from the phone UI go through the same path as controls from the PC. */
    fun apply(control: Control) = queue.execute { handleControl(control) }

    /** The app went to the background: Android takes the camera away, so release it but keep the connection. */
    fun pauseCamera() = queue.execute {
        if (cameraPaused) return@execute
        cameraPaused = true
        if (isStreaming) {
            isStreaming = false
            video.stop()
        }
    }

    fun resumeCamera() = queue.execute {
        if (!cameraPaused) return@execute
        cameraPaused = false
        val phase = _phase.value
        if (phase is Phase.Streaming && connection != null) startCamera(state, connectionId, phase.address, phase.serverName, resumed = true)
    }

    fun close() {
        disconnect()
        queue.shutdown()
    }

    // MARK: Connection lifecycle (queue)

    private fun openConnection(reconnecting: Boolean) {
        val address = address ?: return
        teardown()
        setPhase(if (reconnecting) Phase.Reconnecting(address) else Phase.Connecting(address))

        // Events from an older, cancelled connection must not tear down this one.
        val id = ++connectionId
        val connection = FramedConnection(address.host, address.port, queue) { event ->
            if (connectionId == id) handle(event)
        }
        this.connection = connection
        connection.start()
    }

    private fun handle(event: FramedConnection.Event) {
        when (event) {
            is FramedConnection.Event.Ready -> {
                lastReceived = System.nanoTime()
                sendHello()
            }
            is FramedConnection.Event.Message -> {
                lastReceived = System.nanoTime()
                handleMessage(event.header, event.payload)
            }
            is FramedConnection.Event.Closed -> {
                val wasStreaming = _phase.value is Phase.Streaming
                connectionId++
                teardown()
                val address = address
                if (userStopped || address == null) return
                val phase = _phase.value
                when {
                    wasStreaming -> {
                        // Wi-Fi hiccup or PC restarted: keep trying while the app is open.
                        setPhase(Phase.Reconnecting(address))
                        scheduleReconnect()
                    }
                    phase is Phase.Reconnecting -> scheduleReconnect()
                    phase is Phase.Failed -> Unit // Keep the more specific message from the handshake.
                    else -> fail(event.error?.let { SessionError.ConnectionFailed(it.message ?: it.javaClass.simpleName) } ?: SessionError.ConnectionClosed)
                }
            }
        }
    }

    /** Cancelled by [teardown], so a new connection is never replaced by a stale retry. */
    private fun scheduleReconnect() {
        reconnect?.cancel(false)
        reconnect = queue.schedule({
            reconnect = null
            if (!userStopped) openConnection(reconnecting = true)
        }, reconnectDelayMs, TimeUnit.MILLISECONDS)
    }

    private fun tokenKeys(): List<String> {
        val address = address ?: return emptyList()
        return listOfNotNull(address.serverId, "${address.host}:${address.port}")
    }

    private fun sendHello() {
        val token = tokenKeys().firstNotNullOfOrNull { environment.token(it) }
        sentToken = token
        val hello = Hello(
            protocolVersion = ProtocolInfo.VERSION,
            deviceId = environment.deviceId,
            deviceName = environment.deviceName,
            model = environment.model,
            appVersion = environment.appVersion,
            token = token,
        )
        connection?.send(MessageType.Hello, Hello.serializer(), hello)
    }

    private fun handleMessage(header: MessageHeader, payload: ByteArray) {
        when (header.type) {
            MessageType.HelloAck -> {
                val ack = decode<HelloAck>(payload) ?: return fail(SessionError.ProtocolError)
                val expected = address?.serverId
                // The QR code (or an earlier session) named another PC: stop here and send nothing more.
                if (expected != null && expected != ack.serverId) return fail(SessionError.OtherPc)
                address = address?.let {
                    it.copy(serverId = it.serverId ?: ack.serverId, name = it.name ?: ack.serverName)
                }
                when (ack.status) {
                    HelloStatus.ACCEPTED -> startStreaming(ack.serverName)
                    HelloStatus.PAIRING_REQUIRED -> {
                        forgetRejectedToken()
                        address?.let { setPhase(Phase.Pairing(it, attemptsLeft = 5, wrongPin = false)) }
                    }
                    HelloStatus.BUSY -> fail(SessionError.Busy)
                    HelloStatus.PAIRING_LOCKED -> fail(SessionError.PairingLocked)
                    else -> fail(SessionError.VersionMismatch)
                }
            }
            MessageType.PairResult -> {
                if (_phase.value !is Phase.Pairing) return
                val result = decode<PairResult>(payload) ?: return
                val address = address ?: return
                val token = result.token
                when {
                    result.ok && token != null -> {
                        tokenKeys().forEach { environment.saveToken(token, it) }
                        startStreaming(address.name ?: address.host)
                    }
                    result.attemptsLeft > 0 -> setPhase(Phase.Pairing(address, result.attemptsLeft, wrongPin = true))
                    else -> fail(SessionError.PairingLocked)
                }
            }
            MessageType.Ping -> connection?.send(MessageType.Pong, pongPayload(header.timestamp))
            MessageType.Control -> {
                // Only a paired PC may control the camera.
                if (_phase.value !is Phase.Streaming) return
                decode<Control>(payload)?.let { handleControl(it) }
            }
            MessageType.RequestKeyframe -> if (isStreaming) video.requestKeyframe()
            MessageType.Bye -> fail(SessionError.ClosedByPc)
            else -> Unit
        }
    }

    private inline fun <reified T> decode(payload: ByteArray): T? =
        runCatching { ProtocolJson.decodeFromString<T>(payload.decodeToString()) }.getOrNull()

    /** The PC asked for a PIN although a token was sent: it was revoked there, so drop it here too. */
    private fun forgetRejectedToken() {
        val sent = sentToken ?: return
        sentToken = null
        tokenKeys().filter { environment.token(it) == sent }.forEach { environment.removeToken(it) }
    }

    // MARK: Streaming (queue)

    private fun startStreaming(serverName: String) {
        val address = address ?: return
        this.serverName = serverName
        environment.remember(address)
        setPhase(Phase.Streaming(address, serverName))
        startTimers()
        if (!cameraPaused) startCamera(state, connectionId, address, serverName, resumed = false)
    }

    private fun startCamera(initial: CameraState, id: Int, address: ServerAddress, serverName: String, resumed: Boolean) {
        video.start(initial) { result ->
            queue.execute {
                // The connection dropped, the user left or the app went to the background while the camera was starting.
                if (connectionId != id || userStopped || cameraPaused) {
                    if (connectionId == id && cameraPaused) video.stop()
                    return@execute
                }
                result.onFailure { error ->
                    if (initial != CameraRules.defaultState) {
                        // The saved settings don't work on this phone: start over from the defaults.
                        environment.cameraState = null
                        startCamera(CameraRules.defaultState, id, address, serverName, resumed)
                    } else {
                        fail(SessionError.CameraFailed(error.message ?: error.javaClass.simpleName))
                    }
                }
                result.onSuccess { snapshot ->
                    state = snapshot.state
                    outputSize = snapshot.outputSize
                    _cameraState.value = snapshot.state
                    try {
                        configureEncoder()
                    } catch (e: Exception) {
                        return@execute fail(SessionError.CameraFailed(e.message ?: e.javaClass.simpleName))
                    }
                    if (!resumed) connection?.send(MessageType.Capabilities, Capabilities.serializer(), video.capabilities)
                    connection?.send(MessageType.CameraState, CameraState.serializer(), snapshot.state)
                    isStreaming = true
                }
            }
        }
    }

    private fun configureEncoder() {
        video.configureEncoder(outputSize, state.fps, state.bitrateKbps)
        needKeyframe = true
        connection?.send(
            MessageType.StreamConfig, StreamConfig.serializer(),
            StreamConfig("h264", outputSize.width, outputSize.height, state.fps, state.bitrateKbps),
        )
    }

    private fun send(frame: EncodedFrame) {
        val connection = connection
        if (!isStreaming || connection == null) return

        // Low-latency rule: never let frames queue up. When the socket is congested, drop until a keyframe.
        if (connection.framesInFlight >= 2 || (needKeyframe && !frame.isKeyframe)) {
            droppedFrames++
            if (!needKeyframe) {
                needKeyframe = true
                video.requestKeyframe()
            } else if (!frame.isKeyframe) {
                video.requestKeyframe()
            }
            return
        }
        needKeyframe = false
        connection.send(
            MessageType.VideoFrame, frame.data, flags = if (frame.isKeyframe) MessageFlags.KEYFRAME else 0,
            timestamp = frame.timestampMicros, isVideo = true,
        )
        framesThisSecond++
        bytesThisSecond += frame.data.size
    }

    private fun handleControl(control: Control) {
        video.apply(control) { update ->
            queue.execute {
                val state = update.snapshot.state
                this.state = state
                outputSize = update.snapshot.outputSize
                _cameraState.value = state
                // A rejected format was rolled back; only a state the camera accepted is remembered.
                if (!update.failed && CameraRules.isValid(state, video.cameraIds)) environment.cameraState = state
                if (!isStreaming) return@execute
                if (update.formatChanged) {
                    try {
                        configureEncoder()
                    } catch (e: Exception) {
                        return@execute fail(SessionError.CameraFailed(e.message ?: e.javaClass.simpleName))
                    }
                } else if (update.bitrateChanged) {
                    // The PC reads the new bitrate from the camera state below; no new StreamConfig needed.
                    video.setBitrate(state.bitrateKbps)
                }
                connection?.send(MessageType.CameraState, CameraState.serializer(), state)
            }
        }
    }

    private fun startTimers() {
        stopTimers()
        timers += queue.scheduleWithFixedDelay({ everySecond() }, 1, 1, TimeUnit.SECONDS)
        timers += queue.scheduleWithFixedDelay({ sendStatus() }, 2, 2, TimeUnit.SECONDS)
    }

    private fun everySecond() {
        connection?.send(MessageType.Ping)
        lastFps = framesThisSecond.toDouble()
        lastKbps = bytesThisSecond * 8 / 1000
        framesThisSecond = 0
        bytesThisSecond = 0
        _sentFps.value = lastFps
        _sentKbps.value = lastKbps
        if (System.nanoTime() - lastReceived > TimeUnit.SECONDS.toNanos(5)) {
            // Liveness timeout: handled like a dropped connection (reconnect while streaming).
            val connection = connection ?: return
            this.connection = null
            connection.cancel()
            handle(FramedConnection.Event.Closed(java.io.IOException("timeout")))
        }
    }

    private fun sendStatus() {
        if (_phase.value !is Phase.Streaming) return
        val (battery, charging) = environment.battery()
        val status = DeviceStatus(battery, charging, environment.thermal(), lastFps, lastKbps, droppedFrames)
        connection?.send(MessageType.Status, DeviceStatus.serializer(), status)
    }

    private fun stopTimers() {
        timers.forEach { it.cancel(false) }
        timers.clear()
    }

    private fun teardown() {
        isStreaming = false
        reconnect?.cancel(false)
        reconnect = null
        stopTimers()
        video.stop()
        val old = connection
        connection = null
        old?.cancel()
        _sentFps.value = 0.0
        _sentKbps.value = 0
    }

    private fun fail(error: SessionError) {
        userStopped = true
        teardown()
        setPhase(Phase.Failed(error))
    }

    private fun setPhase(phase: Phase) {
        _phase.value = phase
    }
}
