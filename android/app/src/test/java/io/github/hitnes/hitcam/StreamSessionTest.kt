package io.github.hitnes.hitcam

import io.github.hitnes.hitcam.camera.CameraRules
import io.github.hitnes.hitcam.camera.CameraSnapshot
import io.github.hitnes.hitcam.camera.CameraUpdate
import io.github.hitnes.hitcam.camera.OutputSize
import io.github.hitnes.hitcam.protocol.CameraInfo
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.Capabilities
import io.github.hitnes.hitcam.protocol.Control
import io.github.hitnes.hitcam.protocol.Hello
import io.github.hitnes.hitcam.protocol.HelloAck
import io.github.hitnes.hitcam.protocol.MessageHeader
import io.github.hitnes.hitcam.protocol.MessageType
import io.github.hitnes.hitcam.protocol.PairRequest
import io.github.hitnes.hitcam.protocol.PairResult
import io.github.hitnes.hitcam.protocol.ProtocolJson
import io.github.hitnes.hitcam.protocol.ServerAddress
import io.github.hitnes.hitcam.protocol.StreamConfig
import io.github.hitnes.hitcam.session.Phase
import io.github.hitnes.hitcam.session.SessionEnvironment
import io.github.hitnes.hitcam.session.SessionError
import io.github.hitnes.hitcam.session.StreamSession
import io.github.hitnes.hitcam.session.VideoSource
import io.github.hitnes.hitcam.video.EncodedFrame
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import java.io.DataInputStream
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.concurrent.thread

class StreamSessionTest {
    private lateinit var server: ServerSocket
    private lateinit var video: FakeVideo
    private lateinit var environment: FakeEnvironment
    private lateinit var session: StreamSession
    private val address get() = ServerAddress("127.0.0.1", server.localPort)

    @Before
    fun setUp() {
        server = ServerSocket(0)
        server.soTimeout = 5_000
        video = FakeVideo()
        environment = FakeEnvironment()
        session = StreamSession(video, environment, reconnectDelayMs = 100)
    }

    @After
    fun tearDown() {
        session.close()
        server.close()
    }

    @Test
    fun pairedPhoneStreamsAfterHelloWithItsToken() {
        environment.tokens["pc-1"] = "secret"
        session.connect(address.copy(serverId = "pc-1"))
        val pc = FakePc(server.accept())

        val hello = pc.expectJson<Hello>(MessageType.Hello)
        assertEquals("secret", hello.token)
        assertEquals("Test Phone", hello.deviceName)
        pc.sendJson(MessageType.HelloAck, HelloAck(1, "accepted", "Test PC", "pc-1"))

        // Startup order as on the iPhone: StreamConfig, Capabilities, CameraState, then video from a keyframe.
        val config = pc.expectJson<StreamConfig>(MessageType.StreamConfig)
        assertEquals(StreamConfig("h264", 1920, 1080, 30, 8000), config)
        pc.expectJson<Capabilities>(MessageType.Capabilities)
        pc.expectJson<CameraState>(MessageType.CameraState)
        val (header, payload) = pc.expect(MessageType.VideoFrame)
        assertTrue(header.isKeyframe)
        assertEquals(1234L, header.timestamp)
        assertEquals(3, payload.size)

        waitFor { session.phase.value is Phase.Streaming }
        assertEquals(address.copy(serverId = "pc-1", name = "Test PC"), environment.remembered.last())
        pc.close()
    }

    @Test
    fun pairingWithAWrongThenARightPin() {
        environment.tokens["127.0.0.1:${server.localPort}"] = "revoked"
        session.connect(address)
        val pc = FakePc(server.accept())
        assertEquals("revoked", pc.expectJson<Hello>(MessageType.Hello).token)
        pc.sendJson(MessageType.HelloAck, HelloAck(1, "pairingRequired", "Test PC", "pc-2"))

        waitFor { session.phase.value is Phase.Pairing }
        // The PC no longer knows that token: it is forgotten.
        assertNull(environment.tokens["127.0.0.1:${server.localPort}"])

        session.submitPin("111111")
        assertEquals("111111", pc.expectJson<PairRequest>(MessageType.PairRequest).pin)
        pc.sendJson(MessageType.PairResult, PairResult(ok = false, attemptsLeft = 4))
        waitFor { (session.phase.value as? Phase.Pairing)?.wrongPin == true }
        assertEquals(4, (session.phase.value as Phase.Pairing).attemptsLeft)

        session.submitPin("222222")
        assertEquals("222222", pc.expectJson<PairRequest>(MessageType.PairRequest).pin)
        pc.sendJson(MessageType.PairResult, PairResult(ok = true, token = "fresh", attemptsLeft = 5))
        pc.expect(MessageType.StreamConfig)
        waitFor { session.phase.value is Phase.Streaming }
        assertEquals("fresh", environment.tokens["pc-2"])
        assertEquals("fresh", environment.tokens["127.0.0.1:${server.localPort}"])
        pc.close()
    }

    @Test
    fun aServerIdClaimedByATypedAddressIsNotTrusted() {
        // The real PC "pc-1" is paired; someone else at a typed address claims to be it.
        environment.tokens["pc-1"] = "secret"
        session.connect(address)
        val pc = FakePc(server.accept())
        assertNull(pc.expectJson<Hello>(MessageType.Hello).token)
        pc.sendJson(MessageType.HelloAck, HelloAck(1, "accepted", "Impostor", "pc-1"))
        pc.expect(MessageType.StreamConfig)
        waitFor { session.phase.value is Phase.Streaming }
        pc.close()

        waitFor { session.phase.value is Phase.Reconnecting }
        val again = FakePc(server.accept())
        assertNull(again.expectJson<Hello>(MessageType.Hello).token)
        again.close()
        assertTrue(environment.remembered.isNotEmpty())
        assertTrue(environment.remembered.none { it.serverId == "pc-1" })
        assertEquals("Impostor", environment.remembered.last().name)
    }

    @Test
    fun pairingThroughATypedAddressTrustsThePairedServer() {
        session.connect(address)
        val pc = FakePc(server.accept())
        assertNull(pc.expectJson<Hello>(MessageType.Hello).token)
        pc.sendJson(MessageType.HelloAck, HelloAck(1, "pairingRequired", "Test PC", "pc-3"))
        waitFor { session.phase.value is Phase.Pairing }
        session.submitPin("123456")
        pc.expect(MessageType.PairRequest)
        pc.sendJson(MessageType.PairResult, PairResult(ok = true, token = "fresh", attemptsLeft = 5))
        pc.expect(MessageType.StreamConfig)
        waitFor { session.phase.value is Phase.Streaming }
        assertEquals("fresh", environment.tokens["pc-3"])
        assertEquals("fresh", environment.tokens["127.0.0.1:${server.localPort}"])
        assertEquals(address.copy(serverId = "pc-3", name = "Test PC"), environment.remembered.last())
        pc.close()

        waitFor { session.phase.value is Phase.Reconnecting }
        val again = FakePc(server.accept())
        assertEquals("fresh", again.expectJson<Hello>(MessageType.Hello).token)
        // The paired id is now expected: another PC at this address is refused.
        again.sendJson(MessageType.HelloAck, HelloAck(1, "accepted", "Other", "someone-else"))
        waitFor { session.phase.value == Phase.Failed(SessionError.OtherPc) }
        again.close()
    }

    @Test
    fun silentPcFailsTheConnectInsteadOfHangingForever() {
        useReplyTimeout(300)
        session.connect(address)
        val pc = FakePc(server.accept())
        pc.expect(MessageType.Hello)
        // Accepted TCP, never answers.
        waitFor { session.phase.value is Phase.Failed }
        assertTrue((session.phase.value as Phase.Failed).error is SessionError.ConnectionFailed)
        pc.close()
    }

    @Test
    fun silentPcWhileReconnectingIsRetried() {
        useReplyTimeout(300)
        val pc = streamingPc()
        pc.close()
        waitFor { session.phase.value is Phase.Reconnecting }
        val silent = FakePc(server.accept())
        silent.expect(MessageType.Hello)
        // No answer: the phone gives up on this socket and tries again.
        val again = FakePc(server.accept())
        again.expect(MessageType.Hello)
        assertTrue(session.phase.value is Phase.Reconnecting)
        silent.close()
        again.close()
    }

    @Test
    fun unansweredPinClosesButTypingThePinHasNoDeadline() {
        useReplyTimeout(300)
        session.connect(address)
        val pc = FakePc(server.accept())
        pc.expect(MessageType.Hello)
        pc.sendJson(MessageType.HelloAck, HelloAck(1, "pairingRequired", "Test PC", "pc-4"))
        waitFor { session.phase.value is Phase.Pairing }
        // The user takes longer than the reply timeout to type the PIN.
        Thread.sleep(900)
        assertTrue(session.phase.value is Phase.Pairing)

        session.submitPin("123456")
        pc.expect(MessageType.PairRequest)
        waitFor { session.phase.value == Phase.Failed(SessionError.ConnectionClosed) }
        pc.close()
    }

    @Test
    fun pingIsAnsweredWithTheEchoedTimestamp() {
        session.connect(address)
        val pc = FakePc(server.accept())
        pc.expect(MessageType.Hello)
        pc.send(MessageType.Ping, ByteArray(0), timestamp = 0x0102030405060708)
        val (_, payload) = pc.expect(MessageType.Pong)
        assertEquals(0x0102030405060708, ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN).long)
        pc.close()
    }

    @Test
    fun anotherPcIsRefused() {
        session.connect(address.copy(serverId = "expected"))
        val pc = FakePc(server.accept())
        pc.expect(MessageType.Hello)
        pc.sendJson(MessageType.HelloAck, HelloAck(1, "accepted", "Other", "someone-else"))
        waitFor { session.phase.value == Phase.Failed(SessionError.OtherPc) }
        assertTrue(video.starts == 0)
        pc.close()
    }

    @Test
    fun busyPcIsReported() {
        session.connect(address)
        val pc = FakePc(server.accept())
        pc.expect(MessageType.Hello)
        pc.sendJson(MessageType.HelloAck, HelloAck(1, "busy", "PC", "pc"))
        waitFor { session.phase.value == Phase.Failed(SessionError.Busy) }
        pc.close()
    }

    @Test
    fun controlFromThePcIsAppliedAndReported() {
        val pc = streamingPc()
        pc.sendJson(MessageType.Control, Control(width = 1280, height = 720, mirror = true))
        val config = pc.expectJson<StreamConfig>(MessageType.StreamConfig)
        assertEquals(1280 to 720, config.width to config.height)
        val state = pc.expectJson<CameraState>(MessageType.CameraState)
        assertTrue(state.mirror)
        waitFor { environment.cameraState?.width == 1280 }
        pc.close()
    }

    @Test
    fun droppedConnectionReconnects() {
        val pc = streamingPc()
        pc.close()
        waitFor { session.phase.value is Phase.Reconnecting }
        val again = FakePc(server.accept())
        again.expect(MessageType.Hello)
        again.close()
    }

    @Test
    fun disconnectSaysBye() {
        val pc = streamingPc()
        session.disconnect()
        pc.expect(MessageType.Bye)
        waitFor { session.phase.value == Phase.Idle }
        pc.close()
    }

    private fun useReplyTimeout(ms: Long) {
        session.close()
        session = StreamSession(video, environment, reconnectDelayMs = 100, replyTimeoutMs = ms)
    }

    private fun streamingPc(): FakePc {
        session.connect(address)
        val pc = FakePc(server.accept())
        pc.expect(MessageType.Hello)
        pc.sendJson(MessageType.HelloAck, HelloAck(1, "accepted", "PC", "pc"))
        pc.expect(MessageType.StreamConfig)
        pc.expect(MessageType.Capabilities)
        pc.expect(MessageType.CameraState)
        pc.expect(MessageType.VideoFrame)
        waitFor { session.phase.value is Phase.Streaming }
        return pc
    }

    private fun waitFor(condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + 5_000
        while (!condition()) {
            if (System.currentTimeMillis() > deadline) fail("condition not met; phase = ${session.phase.value}")
            Thread.sleep(10)
        }
    }

    /** The PC side of one connection. Status/Ping messages from the phone are skipped while waiting. */
    private class FakePc(private val socket: Socket) {
        private val input = DataInputStream(socket.getInputStream())
        private val output: OutputStream = socket.getOutputStream()

        init {
            socket.soTimeout = 5_000
        }

        fun expect(type: MessageType): Pair<MessageHeader, ByteArray> {
            while (true) {
                val headerBytes = ByteArray(MessageHeader.SIZE)
                input.readFully(headerBytes)
                val header = MessageHeader.decode(headerBytes)
                val payload = ByteArray(header.length).also { input.readFully(it) }
                if (header.type == type) return header to payload
                if (header.type != MessageType.Ping && header.type != MessageType.Status && header.type != MessageType.VideoFrame) {
                    fail("expected $type, got ${header.type}")
                }
            }
        }

        inline fun <reified T> expectJson(type: MessageType): T = ProtocolJson.decodeFromString(expect(type).second.decodeToString())

        fun send(type: MessageType, payload: ByteArray, timestamp: Long = 0) {
            output.write(MessageHeader(type, payload.size, timestamp).encode() + payload)
            output.flush()
        }

        inline fun <reified T> sendJson(type: MessageType, value: T) = send(type, ProtocolJson.encodeToString(value).toByteArray())

        fun close() = socket.close()
    }

    private class FakeVideo : VideoSource {
        override val capabilities = Capabilities(
            listOf(CameraInfo("back-wide", "Wide", "back", 1.0, 10.0, hasTorch = true, supportsFocus = true)),
            CameraRules.presets,
        )
        override val cameraIds = listOf("back-wide")
        override var onFrame: ((EncodedFrame) -> Unit)? = null
        override var onError: ((String) -> Unit)? = null
        @Volatile var starts = 0
        private var state = CameraRules.defaultState

        override fun start(initial: CameraState, completion: (Result<CameraSnapshot>) -> Unit) {
            starts++
            state = initial.copy(stabilization = "off", stabilizationModes = listOf("off"))
            thread { completion(Result.success(CameraSnapshot(state, CameraRules.outputSize(state)))) }
        }

        override fun stop() = Unit

        override fun apply(control: Control, completion: (CameraUpdate) -> Unit) {
            val before = state
            state = CameraRules.applying(control, state, cameraIds)
            val snapshot = CameraSnapshot(state, CameraRules.outputSize(state))
            thread {
                completion(CameraUpdate(snapshot, snapshot.outputSize != CameraRules.outputSize(before), state.bitrateKbps != before.bitrateKbps, false))
            }
        }

        override fun configureEncoder(size: OutputSize, fps: Int, bitrateKbps: Int) {
            // A real encoder starts producing frames shortly after, the first one a keyframe.
            thread {
                Thread.sleep(50)
                onFrame?.invoke(EncodedFrame(byteArrayOf(0, 0, 1), isKeyframe = true, timestampMicros = 1234))
            }
        }

        override fun setBitrate(kbps: Int) = Unit
        override fun requestKeyframe() = Unit
    }

    private class FakeEnvironment : SessionEnvironment {
        val tokens = java.util.concurrent.ConcurrentHashMap<String, String>()
        val remembered = java.util.concurrent.CopyOnWriteArrayList<ServerAddress>()
        override val deviceId = "device-1"
        override val deviceName = "Test Phone"
        override val model = "Test Model"
        override val appVersion = "0.2.0"
        override fun token(key: String) = tokens[key]
        override fun saveToken(token: String, key: String) {
            tokens[key] = token
        }
        override fun removeToken(key: String) {
            tokens.remove(key)
        }
        override fun remember(server: ServerAddress) {
            remembered += server
        }
        @Volatile override var cameraState: CameraState? = null
        override fun battery() = 0.5 to false
        override fun thermal() = "nominal"
    }
}
