package io.github.hitnes.hitcam.session

/**
 * A pairing token as stored: `v2:<fingerprint>:<token>`, sent only to the PC behind that certificate (SHA-256 hex).
 * Tokens stored bare by apps up to 0.3.0 (protocol v1, no certificate) do not parse: that PC is paired again once,
 * with its PIN, and its certificate pinned from then on.
 */
data class StoredPairing(val fingerprint: String, val token: String) {
    fun encode(): String = "$PREFIX$fingerprint:$token"

    companion object {
        private const val PREFIX = "v2:"

        fun parse(stored: String): StoredPairing? {
            if (!stored.startsWith(PREFIX)) return null
            val fingerprint = stored.substring(PREFIX.length).substringBefore(':', "")
            val token = stored.substring(PREFIX.length).substringAfter(':', "")
            return if (fingerprint.length == 64 && token.isNotEmpty()) StoredPairing(fingerprint, token) else null
        }
    }
}
