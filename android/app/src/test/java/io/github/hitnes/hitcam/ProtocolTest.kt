package io.github.hitnes.hitcam

import io.github.hitnes.hitcam.camera.CameraRules
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.Control
import io.github.hitnes.hitcam.protocol.Hello
import io.github.hitnes.hitcam.protocol.HelloAck
import io.github.hitnes.hitcam.protocol.HelloStatus
import io.github.hitnes.hitcam.protocol.MessageFlags
import io.github.hitnes.hitcam.protocol.MessageHeader
import io.github.hitnes.hitcam.protocol.MessageType
import io.github.hitnes.hitcam.protocol.ProtocolException
import io.github.hitnes.hitcam.protocol.ProtocolInfo
import io.github.hitnes.hitcam.protocol.ProtocolJson
import io.github.hitnes.hitcam.protocol.ServerAddress
import io.github.hitnes.hitcam.protocol.pongPayload
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class ProtocolTest {
    @Test
    fun headerMatchesTheWindowsEncoding() {
        // Same bytes as windows/HitCam.Core.Tests ProtocolTests.Header_round_trips and the iOS tests.
        val header = MessageHeader(MessageType.VideoFrame, length = 1234, timestamp = 0x0102030405060708, flags = MessageFlags.KEYFRAME)
        val bytes = header.encode()

        assertArrayEquals(
            byteArrayOf(0x11, 0x01, 0, 0, 0xD2.toByte(), 0x04, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1),
            bytes,
        )
        assertEquals(header, MessageHeader.decode(bytes))
        assertEquals(MessageType.VideoFrame, MessageHeader.decode(bytes).type)
    }

    @Test
    fun headerDecodesAtAnOffset() {
        val header = MessageHeader(MessageType.Ping, length = 0, timestamp = 42)
        assertEquals(header, MessageHeader.decode(byteArrayOf(9, 9, 9) + header.encode(), offset = 3))
    }

    @Test
    fun headerRejectsBadInput() {
        val reserved = MessageHeader(MessageType.Ping, 0, 0).encode().also { it[2] = 1 }
        assertThrows(ProtocolException::class.java) { MessageHeader.decode(reserved) }

        val big = MessageHeader(MessageType.Ping, 0, 0).encode()
        byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte(), 0x7F).copyInto(big, 4)
        assertThrows(ProtocolException::class.java) { MessageHeader.decode(big) }

        val huge = MessageHeader(MessageType.Ping, 0, 0).encode()
        byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte()).copyInto(huge, 4)
        assertThrows(ProtocolException::class.java) { MessageHeader.decode(huge) }

        assertThrows(ProtocolException::class.java) { MessageHeader.decode(ByteArray(15)) }
    }

    @Test
    fun unknownTypeDecodesWithoutAName() {
        assertNull(MessageHeader.decode(MessageHeader(0x7E, 0, 0, 0).encode()).type)
    }

    @Test
    fun pongEchoesThePingTimestamp() {
        assertArrayEquals(byteArrayOf(8, 7, 6, 5, 4, 3, 2, 1), pongPayload(0x0102030405060708))
    }

    @Test
    fun controlJsonOmitsMissingFields() {
        val json = ProtocolJson.parseToJsonElement(ProtocolJson.encodeToString(Control(zoom = 2.0, torch = true))).jsonObject

        assertEquals(setOf("zoom", "torch"), json.keys)
        assertEquals("2.0", json["zoom"]!!.jsonPrimitive.content)
    }

    @Test
    fun helloOmitsAMissingToken() {
        val hello = Hello(ProtocolInfo.VERSION, "id", "Phone", model = "Xiaomi 2201117TG")
        val json = ProtocolJson.parseToJsonElement(ProtocolJson.encodeToString(hello)).jsonObject

        assertFalse("token" in json)
        assertEquals("Xiaomi 2201117TG", json["model"]!!.jsonPrimitive.content)
    }

    @Test
    fun stateSavedByAnOlderVersionStillDecodes() {
        val json = """{"cameraId":"back-wide","zoom":1,"torch":false,"focusMode":"continuous","lensPosition":0.5,"exposureBias":0,"mirror":false,"rotation":0,"width":1920,"height":1080,"fps":30,"bitrateKbps":8000}"""
        val state = ProtocolJson.decodeFromString<CameraState>(json)

        assertEquals("back-wide", state.cameraId)
        assertNull(state.whiteBalanceMode)
        assertNull(state.stabilization)
        assertNull(state.noiseReduction)
        assertNull(state.noiseReductionModes)
    }

    @Test
    fun noiseReductionIsOmittedWhenTheCameraHasNoChoice() {
        val state = CameraRules.defaultState
        val plain = ProtocolJson.parseToJsonElement(ProtocolJson.encodeToString(state)).jsonObject
        assertFalse("noiseReduction" in plain)
        assertFalse("noiseReductionModes" in plain)

        val offered = state.copy(noiseReduction = "high", noiseReductionModes = listOf("off", "fast", "high"))
        val json = ProtocolJson.parseToJsonElement(ProtocolJson.encodeToString(offered)).jsonObject
        assertEquals("high", json["noiseReduction"]!!.jsonPrimitive.content)
        assertEquals(listOf("off", "fast", "high"), json["noiseReductionModes"]!!.jsonArray.map { it.jsonPrimitive.content })
        assertEquals(offered, ProtocolJson.decodeFromString<CameraState>(ProtocolJson.encodeToString(offered)))
    }

    @Test
    fun pcCanSetNoiseReduction() {
        val control = ProtocolJson.decodeFromString<Control>("""{"noiseReduction":"fast"}""")
        assertEquals("fast", control.noiseReduction)
        assertNull(control.stabilization)
        val encoded = ProtocolJson.parseToJsonElement(ProtocolJson.encodeToString(Control(zoom = 2.0))).jsonObject
        assertFalse("noiseReduction" in encoded)
    }

    @Test
    fun pcCanLockWhiteBalanceExposureAndSetStabilization() {
        val json = """{"whiteBalanceMode":"locked","whiteBalanceTemperature":4200,"whiteBalanceTint":-10,"exposureMode":"locked","stabilization":"standard","somethingNew":1}"""
        val control = ProtocolJson.decodeFromString<Control>(json)

        assertEquals("locked", control.whiteBalanceMode)
        assertEquals(4200.0, control.whiteBalanceTemperature!!, 0.0)
        assertEquals(-10.0, control.whiteBalanceTint!!, 0.0)
        assertEquals("locked", control.exposureMode)
        assertEquals("standard", control.stabilization)
        assertNull(control.zoom)
    }

    @Test
    fun helloAckFromThePcDecodes() {
        val ack = ProtocolJson.decodeFromString<HelloAck>("""{"protocolVersion":1,"status":"pairingRequired","serverName":"PC","serverId":"abc"}""")
        assertEquals(HelloStatus.PAIRING_REQUIRED, ack.status)
    }

    @Test
    fun serverAddressParsing() {
        assertEquals(
            ServerAddress("192.168.1.5", 47800, "abc", "My PC"),
            ServerAddress.parse("hitcam://192.168.1.5:47800?id=abc&name=My%20PC"),
        )
        assertEquals(ServerAddress("10.0.0.2", 47800), ServerAddress.parse(" 10.0.0.2 "))
        assertEquals(5000, ServerAddress.parse("10.0.0.2:5000")?.port)
        assertEquals("Мой ПК", ServerAddress.parse("hitcam://pc.local?id=x&name=%D0%9C%D0%BE%D0%B9%20%D0%9F%D0%9A")?.name)
        assertNull(ServerAddress.parse("10.0.0.2:abc"))
        assertNull(ServerAddress.parse("10.0.0.2:0"))
        assertNull(ServerAddress.parse("10.0.0.2:70000"))
        assertNull(ServerAddress.parse(""))
        assertNull(ServerAddress.parse("a b"))
    }

    @Test
    fun qrCodeWithAnImpossiblePortIsRejected() {
        assertNull(ServerAddress.parse("hitcam://192.168.1.5:99999?id=abc"))
        assertNull(ServerAddress.parse("hitcam://192.168.1.5:0?id=abc"))
        assertEquals(ProtocolInfo.DEFAULT_PORT, ServerAddress.parse("hitcam://192.168.1.5?id=abc")?.port)
        assertEquals(65535, ServerAddress.parse("hitcam://192.168.1.5:65535?id=abc")?.port)
        assertNull(ServerAddress.parse("hitcam://?id=abc"))
    }
}
