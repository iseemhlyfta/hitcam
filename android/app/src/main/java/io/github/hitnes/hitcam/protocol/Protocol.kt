package io.github.hitnes.hitcam.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.net.URI
import java.net.URLDecoder
import java.nio.ByteBuffer
import java.nio.ByteOrder

// HitCam protocol v1. Keep in sync with docs/protocol.md, ios/HitCam/Protocol and windows/HitCam.Core/Protocol.

object ProtocolInfo {
    const val VERSION = 1
    const val DEFAULT_PORT = 47800
    const val URI_SCHEME = "hitcam"
}

enum class MessageType(val raw: Int) {
    Hello(0x01),
    HelloAck(0x02),
    PairRequest(0x03),
    PairResult(0x04),
    StreamConfig(0x10),
    VideoFrame(0x11),
    RequestKeyframe(0x12),
    Capabilities(0x20),
    CameraState(0x21),
    Control(0x22),
    Status(0x30),
    Ping(0x31),
    Pong(0x32),
    Bye(0x3F);

    companion object {
        fun of(raw: Int): MessageType? = entries.firstOrNull { it.raw == raw }
    }
}

object MessageFlags {
    const val KEYFRAME = 1
}

class ProtocolException(message: String) : Exception(message)

/** Fixed 16-byte header, little-endian: type, flags, reserved(2), length(4), timestamp(8). */
data class MessageHeader(val rawType: Int, val flags: Int, val length: Int, val timestamp: Long) {
    constructor(type: MessageType, length: Int, timestamp: Long, flags: Int = 0) : this(type.raw, flags, length, timestamp)

    val type: MessageType? get() = MessageType.of(rawType)
    val isKeyframe: Boolean get() = flags and MessageFlags.KEYFRAME != 0

    fun encode(): ByteArray = ByteBuffer.allocate(SIZE).order(ByteOrder.LITTLE_ENDIAN)
        .put(rawType.toByte())
        .put(flags.toByte())
        .putShort(0)
        .putInt(length)
        .putLong(timestamp)
        .array()

    companion object {
        const val SIZE = 16
        const val MAX_PAYLOAD_LENGTH = 8 * 1024 * 1024

        fun decode(bytes: ByteArray, offset: Int = 0): MessageHeader {
            if (bytes.size - offset < SIZE) throw ProtocolException("short header")
            val buffer = ByteBuffer.wrap(bytes, offset, SIZE).order(ByteOrder.LITTLE_ENDIAN)
            val type = buffer.get().toInt() and 0xFF
            val flags = buffer.get().toInt() and 0xFF
            if (buffer.getShort().toInt() != 0) throw ProtocolException("reserved field is not zero")
            val length = buffer.getInt().toLong() and 0xFFFFFFFFL
            if (length > MAX_PAYLOAD_LENGTH) throw ProtocolException("payload too large: $length")
            return MessageHeader(type, flags, length.toInt(), buffer.getLong())
        }
    }
}

/** Pong payload: the ping's header timestamp, 8 bytes little-endian. */
fun pongPayload(pingTimestamp: Long): ByteArray =
    ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(pingTimestamp).array()

/** Monotonic clock in microseconds (System.nanoTime); camera timestamps are converted to the same base. */
object MonotonicClock {
    fun nowMicros(): Long = System.nanoTime() / 1_000
}

/** camelCase JSON; absent optional fields are omitted, unknown ones ignored. */
val ProtocolJson = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
}

// MARK: - JSON payloads

object HelloStatus {
    const val ACCEPTED = "accepted"
    const val PAIRING_REQUIRED = "pairingRequired"
    const val PAIRING_LOCKED = "pairingLocked"
    const val BUSY = "busy"
    const val VERSION_MISMATCH = "versionMismatch"
}

@Serializable
data class Hello(
    val protocolVersion: Int,
    val deviceId: String,
    val deviceName: String,
    val model: String? = null,
    val appVersion: String? = null,
    val token: String? = null,
)

@Serializable
data class HelloAck(val protocolVersion: Int, val status: String, val serverName: String, val serverId: String)

@Serializable
data class PairRequest(val pin: String)

@Serializable
data class PairResult(val ok: Boolean, val token: String? = null, val attemptsLeft: Int)

@Serializable
data class StreamConfig(val codec: String, val width: Int, val height: Int, val fps: Int, val bitrateKbps: Int)

@Serializable
data class CameraInfo(
    val id: String,
    val name: String,
    val position: String,
    val minZoom: Double,
    val maxZoom: Double,
    val hasTorch: Boolean,
    val supportsFocus: Boolean,
    val supportsWhiteBalance: Boolean? = null,
    val supportsExposureLock: Boolean? = null,
)

@Serializable
data class VideoPreset(val width: Int, val height: Int, val fps: List<Int>)

@Serializable
data class Capabilities(val cameras: List<CameraInfo>, val presets: List<VideoPreset>)

@Serializable
data class NormalizedPoint(val x: Double, val y: Double)

@Serializable
data class CameraState(
    val cameraId: String,
    val zoom: Double,
    val torch: Boolean,
    val focusMode: String,
    val lensPosition: Double,
    val exposureBias: Double,
    val mirror: Boolean,
    val rotation: Int,
    val width: Int,
    val height: Int,
    val fps: Int,
    val bitrateKbps: Int,
    // Added in 0.2: "auto" | "locked"; temperature in kelvin, tint −150…150 (positive = magenta).
    val whiteBalanceMode: String? = null,
    val whiteBalanceTemperature: Double? = null,
    val whiteBalanceTint: Double? = null,
    // "auto" | "locked"
    val exposureMode: String? = null,
    // "off" | "standard"; `stabilizationModes` are those the active camera supports.
    val stabilization: String? = null,
    val stabilizationModes: List<String>? = null,
    // Added in 0.3: camera noise reduction before encoding, "off" | "fast" | "high"; `noiseReductionModes` are those
    // the active camera supports. Both absent when the camera offers no choice.
    val noiseReduction: String? = null,
    val noiseReductionModes: List<String>? = null,
)

/** Only non-null fields are applied. */
@Serializable
data class Control(
    val cameraId: String? = null,
    val zoom: Double? = null,
    val torch: Boolean? = null,
    val focusMode: String? = null,
    val lensPosition: Double? = null,
    val focusPoint: NormalizedPoint? = null,
    val exposureBias: Double? = null,
    val mirror: Boolean? = null,
    val rotation: Int? = null,
    val width: Int? = null,
    val height: Int? = null,
    val fps: Int? = null,
    val bitrateKbps: Int? = null,
    val whiteBalanceMode: String? = null,
    val whiteBalanceTemperature: Double? = null,
    val whiteBalanceTint: Double? = null,
    val exposureMode: String? = null,
    val stabilization: String? = null,
    val noiseReduction: String? = null,
)

@Serializable
data class DeviceStatus(
    val battery: Double,
    val charging: Boolean,
    val thermal: String,
    val fps: Double,
    val bitrateKbps: Int,
    val droppedFrames: Int,
)

@Serializable
data class Bye(val reason: String? = null)

/** Parsed `hitcam://host:port?id=…&name=…` from the QR code on the PC, or a typed "host[:port]". */
@Serializable
data class ServerAddress(val host: String, val port: Int, val serverId: String? = null, val name: String? = null) {
    val display: String get() = if (port == ProtocolInfo.DEFAULT_PORT) host else "$host:$port"

    companion object {
        fun parse(text: String): ServerAddress? {
            val trimmed = text.trim()
            if (trimmed.startsWith("${ProtocolInfo.URI_SCHEME}:", ignoreCase = true)) return parseUri(trimmed)
            // Manual entry: "192.168.1.5" or "192.168.1.5:47800".
            val parts = trimmed.split(":")
            val host = parts.first()
            if (parts.size > 2 || host.isEmpty() || host.any { it.isWhitespace() }) return null
            var port = ProtocolInfo.DEFAULT_PORT
            if (parts.size == 2) {
                port = parts[1].toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
            }
            return ServerAddress(host, port)
        }

        private fun parseUri(text: String): ServerAddress? {
            val uri = try {
                URI(text)
            } catch (_: Exception) {
                return null
            }
            if (!uri.scheme.equals(ProtocolInfo.URI_SCHEME, ignoreCase = true)) return null
            val host = uri.host?.takeIf { it.isNotEmpty() } ?: return null
            val port = when {
                uri.port == -1 && uri.rawAuthority?.endsWith(":") != true && hasPortDigits(uri.rawAuthority) -> return null
                uri.port == -1 -> ProtocolInfo.DEFAULT_PORT
                uri.port in 1..65535 -> uri.port
                else -> return null
            }
            val query = uri.rawQuery.orEmpty().split("&").filter { it.isNotEmpty() }.associate { item ->
                val key = item.substringBefore("=")
                val value = item.substringAfter("=", "")
                decode(key) to decode(value)
            }
            return ServerAddress(host.removeSurrounding("[", "]"), port, query["id"], query["name"])
        }

        // java.net.URI leaves the port at -1 when it does not fit ("host:99999" parses as a registry authority).
        private fun hasPortDigits(authority: String?): Boolean =
            authority != null && Regex(""":\d+$""").containsMatchIn(authority)

        private fun decode(value: String): String = URLDecoder.decode(value.replace("+", "%2B"), "UTF-8")
    }
}
