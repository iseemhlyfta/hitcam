package io.github.hitnes.hitcam.net

import io.github.hitnes.hitcam.protocol.MessageHeader
import io.github.hitnes.hitcam.protocol.MessageType
import io.github.hitnes.hitcam.protocol.MonotonicClock
import io.github.hitnes.hitcam.protocol.ProtocolJson
import kotlinx.serialization.KSerializer
import java.io.DataInputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executor
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * A TCP connection speaking HitCam framing. A reader and a writer thread do the blocking I/O;
 * every event is delivered on [events] (the session's single thread), in order.
 */
class FramedConnection(
    private val host: String,
    private val port: Int,
    private val events: Executor,
    private val handler: (Event) -> Unit,
) {
    sealed interface Event {
        data object Ready : Event
        class Message(val header: MessageHeader, val payload: ByteArray) : Event
        class Closed(val error: Exception?) : Event
    }

    private class Packet(val bytes: ByteArray, val isVideo: Boolean, val closeAfter: Boolean = false)

    private val socket = Socket()
    private val outgoing = LinkedBlockingQueue<Packet>()
    private val closed = AtomicBoolean(false)
    private val closing = AtomicBoolean(false)
    @Volatile private var ready = false

    /** Video frames handed to the socket but not yet written out. Used for low-latency frame dropping. */
    private val inFlight = AtomicInteger()
    val framesInFlight: Int get() = inFlight.get()

    fun start() {
        Thread({ run() }, "hitcam-net-read").apply { isDaemon = true }.start()
    }

    private fun run() {
        try {
            socket.tcpNoDelay = true
            socket.keepAlive = true
            // A small kernel buffer makes congestion visible as frames in flight, so they are dropped instead of
            // queueing seconds of video in the socket (the default buffer grows to megabytes).
            socket.sendBufferSize = SEND_BUFFER_BYTES
            socket.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
        } catch (e: Exception) {
            finish(e)
            return
        }
        if (closed.get()) {
            closeSocket()
            return
        }
        ready = true
        Thread({ writeLoop() }, "hitcam-net-write").apply { isDaemon = true }.start()
        events.execute { if (!closed.get()) handler(Event.Ready) }
        readLoop()
    }

    private fun readLoop() {
        try {
            val input = DataInputStream(socket.getInputStream().buffered())
            val headerBytes = ByteArray(MessageHeader.SIZE)
            while (!closed.get()) {
                input.readFully(headerBytes)
                val header = MessageHeader.decode(headerBytes)
                val payload = ByteArray(header.length)
                input.readFully(payload)
                events.execute { if (!closed.get()) handler(Event.Message(header, payload)) }
            }
        } catch (e: java.io.EOFException) {
            finish(null)
        } catch (e: Exception) {
            finish(e)
        }
    }

    private fun writeLoop() {
        try {
            val output = socket.getOutputStream()
            while (true) {
                val packet = outgoing.take()
                if (packet.bytes.isNotEmpty()) output.write(packet.bytes)
                if (packet.isVideo) inFlight.decrementAndGet()
                if (packet.closeAfter) {
                    output.flush()
                    closeSocket()
                    return
                }
            }
        } catch (e: InterruptedException) {
            // Closed.
        } catch (e: Exception) {
            finish(e)
        }
    }

    /** Closes without delivering any further events. */
    fun cancel() {
        if (closed.getAndSet(true)) return
        closeSocket()
    }

    /** Sends a last message and closes once it is written, or after [timeoutMs] if the socket is stuck. No events follow. */
    fun <T> close(type: MessageType, serializer: KSerializer<T>, value: T, timeoutMs: Long = 300) {
        if (closed.getAndSet(true)) return
        if (!ready || closing.getAndSet(true)) {
            closeSocket()
            return
        }
        val payload = ProtocolJson.encodeToString(serializer, value).toByteArray()
        outgoing.clear()
        outgoing.put(Packet(packet(type, 0, MonotonicClock.nowMicros(), payload), isVideo = false, closeAfter = true))
        Thread({
            try {
                TimeUnit.MILLISECONDS.sleep(timeoutMs)
            } catch (_: InterruptedException) {
            }
            closeSocket()
        }, "hitcam-net-close").apply { isDaemon = true }.start()
    }

    fun send(type: MessageType, payload: ByteArray = EMPTY, flags: Int = 0, timestamp: Long = MonotonicClock.nowMicros(), isVideo: Boolean = false) {
        if (closed.get() || closing.get()) return
        if (isVideo) inFlight.incrementAndGet()
        outgoing.put(Packet(packet(type, flags, timestamp, payload), isVideo))
    }

    fun <T> send(type: MessageType, serializer: KSerializer<T>, value: T) {
        send(type, ProtocolJson.encodeToString(serializer, value).toByteArray())
    }

    private fun packet(type: MessageType, flags: Int, timestamp: Long, payload: ByteArray): ByteArray =
        MessageHeader(type, payload.size, timestamp, flags).encode() + payload

    private fun finish(error: Exception?) {
        if (closed.getAndSet(true)) return
        closeSocket()
        events.execute { handler(Event.Closed(error)) }
    }

    private fun closeSocket() {
        try {
            socket.close()
        } catch (_: IOException) {
        }
        // Wakes the writer so it exits; its packets are dropped.
        outgoing.offer(Packet(EMPTY, isVideo = false, closeAfter = true))
    }

    private companion object {
        const val CONNECT_TIMEOUT_MS = 5_000
        const val SEND_BUFFER_BYTES = 128 * 1024
        val EMPTY = ByteArray(0)
    }
}
