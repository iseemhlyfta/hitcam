package io.github.hitnes.hitcam.net

import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Pairing over TLS without letting a man in the middle learn the PIN; see PinProof.cs on the PC for the whole
 * scheme. The PC commits to (its certificate, PIN) first; the phone commits to (the certificate it sees, the typed
 * PIN) before the PC opens; the phone opens only if the PC's commitment matches what it sees.
 */
object PinProof {
    const val PC_LABEL = "hitcam-pc-v2"
    const val PHONE_LABEL = "hitcam-phone-v2"
    const val NONCE_SIZE = 32

    private val random = SecureRandom()

    fun newNonce(): ByteArray = ByteArray(NONCE_SIZE).also { random.nextBytes(it) }

    /** SHA-256 of label, 0, fingerprint (32), PIN (6 ASCII digits), nonce (32). */
    fun commit(label: String, fingerprint: ByteArray, pin: String, nonce: ByteArray): ByteArray {
        require(fingerprint.size == 32 && nonce.size == NONCE_SIZE && pin.length == 6)
        val digest = MessageDigest.getInstance("SHA-256")
        digest.update(label.toByteArray(Charsets.US_ASCII))
        digest.update(0)
        digest.update(fingerprint)
        digest.update(pin.toByteArray(Charsets.US_ASCII))
        digest.update(nonce)
        return digest.digest()
    }
}

fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }

/** Lowercase or uppercase hex of exactly [size] bytes, or null. */
fun String.hexBytes(size: Int): ByteArray? {
    if (length != size * 2) return null
    val out = ByteArray(size)
    for (i in 0 until size) {
        val high = Character.digit(this[2 * i], 16)
        val low = Character.digit(this[2 * i + 1], 16)
        if (high < 0 || low < 0) return null
        out[i] = (high * 16 + low).toByte()
    }
    return out
}
