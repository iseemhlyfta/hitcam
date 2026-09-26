package io.github.hitnes.hitcam.net

import java.net.Socket
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** The PC's certificate is not the one pinned for it: another PC, or someone in between. */
class FingerprintMismatchException : CertificateException("the PC's certificate is not the pinned one")

/** A secured socket and the fingerprint (SHA-256 of the certificate) the PC presented. */
class SecuredSocket(val socket: Socket, val fingerprint: ByteArray)

/** Turns a connected socket into the protocol's transport (TLS; a fake in unit tests). Blocking. */
fun interface SocketSecurity {
    /** @throws FingerprintMismatchException when [expected] is set and the PC presents another certificate. */
    fun secure(socket: Socket, host: String, port: Int, expected: ByteArray?): SecuredSocket
}

/**
 * TLS with the PC's self-signed certificate pinned by fingerprint, no certificate authority: [expected] from the QR
 * code or an earlier pairing, or none while pairing (then the PIN commitments check the certificate).
 */
object TlsSecurity : SocketSecurity {
    private const val HANDSHAKE_TIMEOUT_MS = 5_000

    override fun secure(socket: Socket, host: String, port: Int, expected: ByteArray?): SecuredSocket {
        var seen: ByteArray? = null
        val trust = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
                throw CertificateException("no client certificates")

            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val leaf = chain?.firstOrNull() ?: throw CertificateException("no certificate")
                val fingerprint = MessageDigest.getInstance("SHA-256").digest(leaf.encoded)
                seen = fingerprint
                if (expected != null && !MessageDigest.isEqual(fingerprint, expected)) throw FingerprintMismatchException()
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        val context = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), null) }
        val tls = context.socketFactory.createSocket(socket, host, port, true) as SSLSocket
        tls.enabledProtocols = tls.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
        val timeout = socket.soTimeout
        tls.soTimeout = HANDSHAKE_TIMEOUT_MS
        try {
            tls.startHandshake()
        } catch (e: Exception) {
            // The trust manager's own exception arrives wrapped in an SSLHandshakeException.
            var cause: Throwable? = e
            while (cause != null) {
                if (cause is FingerprintMismatchException) throw cause
                cause = cause.cause
            }
            throw e
        }
        tls.soTimeout = timeout
        return SecuredSocket(tls, seen ?: throw CertificateException("no certificate"))
    }
}
