package io.github.hitnes.hitcam

import io.github.hitnes.hitcam.net.FingerprintMismatchException
import io.github.hitnes.hitcam.net.PinProof
import io.github.hitnes.hitcam.net.TlsSecurity
import io.github.hitnes.hitcam.net.hexBytes
import io.github.hitnes.hitcam.net.toHex
import io.github.hitnes.hitcam.protocol.ServerAddress
import io.github.hitnes.hitcam.session.StoredPairing
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test
import java.net.InetSocketAddress
import java.net.Socket
import java.security.KeyStore
import java.security.MessageDigest
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import kotlin.concurrent.thread

class PinProofTest {
    @Test
    fun commitmentsMatchThePcAndTheIphone() {
        // The same vectors are in TlsServerTests.cs (PC) and PinProofTests.swift (iPhone).
        val fingerprint = ByteArray(32) { 1 }
        val nonce = ByteArray(32) { 2 }
        assertEquals(
            "cec2d2dd935ef81984f804ae036b0070aebe85dbbb5553d8904ef361c277e7e1",
            PinProof.commit(PinProof.PC_LABEL, fingerprint, "123456", nonce).toHex(),
        )
        assertEquals(
            "09e06dfbc17294fa4b4c4e1789a1c306ff506ac5020569463c2191e782dba7f3",
            PinProof.commit(PinProof.PHONE_LABEL, fingerprint, "123456", nonce).toHex(),
        )
    }

    @Test
    fun hexRoundTripsAndRejectsGarbage() {
        val bytes = ByteArray(32) { it.toByte() }
        assertArrayEquals(bytes, bytes.toHex().hexBytes(32))
        assertNull("zz".repeat(32).hexBytes(32))
        assertNull("00".hexBytes(32))
    }

    @Test
    fun storedPairingsKeepTheCertificateTheTokenBelongsTo() {
        val stored = StoredPairing("ab".repeat(32), "token").encode()
        assertEquals(StoredPairing("ab".repeat(32), "token"), StoredPairing.parse(stored))
        // A bare token from app 0.3.0 has no certificate: not a pairing to send it with.
        assertNull(StoredPairing.parse("token"))
        assertNull(StoredPairing.parse("v2:short:token"))
    }

    @Test
    fun theQrCodeCarriesTheFingerprint() {
        val fp = "0f".repeat(32)
        assertEquals(fp, ServerAddress.parse("hitcam://10.0.0.2:47800?id=pc&name=PC&fp=$fp")?.fingerprint)
        assertEquals(fp, ServerAddress.parse("hitcam://10.0.0.2:47800?id=pc&fp=${fp.uppercase()}")?.fingerprint)
        // A damaged fingerprint rejects the whole code rather than connecting without it.
        assertNull(ServerAddress.parse("hitcam://10.0.0.2:47800?id=pc&fp=1234"))
        assertNull(ServerAddress.parse("hitcam://10.0.0.2:47800?id=pc").let { it?.fingerprint })
    }

    @Test
    fun tlsReportsThePcsFingerprintAndRefusesAnotherOne() {
        val keyStore = KeyStore.getInstance("PKCS12").apply {
            PinProofTest::class.java.getResourceAsStream("/test-pc.p12").use { load(it, PASSWORD) }
        }
        val certificate = keyStore.getCertificate("hitcam")
        val expected = MessageDigest.getInstance("SHA-256").digest(certificate.encoded)
        val keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(keyStore, PASSWORD) }
        val context = SSLContext.getInstance("TLS").apply { init(keys.keyManagers, null, null) }
        val server = context.serverSocketFactory.createServerSocket(0) as SSLServerSocket
        thread(isDaemon = true) {
            repeat(3) {
                runCatching {
                    server.accept().use { socket ->
                        socket.getOutputStream().write(42)
                        socket.getInputStream().read()
                    }
                }
            }
        }
        server.use {
            // Pairing: any certificate, and the phone learns which.
            connect(server.localPort, null).use { assertArrayEquals(expected, it.fingerprint) }
            // Paired: only that one.
            connect(server.localPort, expected).use { assertEquals(42, it.socket.getInputStream().read()) }
            assertThrows(FingerprintMismatchException::class.java) { connect(server.localPort, ByteArray(32)) }
        }
    }

    private fun connect(port: Int, expected: ByteArray?) =
        TlsSecurity.secure(Socket().apply { connect(InetSocketAddress("127.0.0.1", port), 2_000) }, "127.0.0.1", port, expected)

    private fun io.github.hitnes.hitcam.net.SecuredSocket.use(block: (io.github.hitnes.hitcam.net.SecuredSocket) -> Unit) {
        try {
            block(this)
        } finally {
            socket.close()
        }
    }

    private companion object {
        val PASSWORD = "hitcam-test".toCharArray()
    }
}
