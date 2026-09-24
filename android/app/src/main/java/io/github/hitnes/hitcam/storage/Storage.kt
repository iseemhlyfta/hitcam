package io.github.hitnes.hitcam.storage

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.ProtocolJson
import io.github.hitnes.hitcam.protocol.ServerAddress
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Pairing tokens, keyed by the PC's server id (or host:port for manual entries) like on the iPhone.
 * They are encrypted with an AES-GCM key that never leaves the Android Keystore.
 */
class TokenStore(context: Context) {
    private val prefs: SharedPreferences = context.getSharedPreferences("tokens", Context.MODE_PRIVATE)

    fun token(key: String): String? {
        val stored = prefs.getString(key, null) ?: return null
        return try {
            val bytes = Base64.decode(stored, Base64.NO_WRAP)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, bytes, 0, IV_SIZE))
            String(cipher.doFinal(bytes, IV_SIZE, bytes.size - IV_SIZE), Charsets.UTF_8)
        } catch (_: Exception) {
            // Key lost (e.g. restored backup) or data damaged: the PC will simply ask for a PIN again.
            remove(key)
            null
        }
    }

    fun save(token: String, key: String) {
        try {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.ENCRYPT_MODE, secretKey())
            val sealed = cipher.iv + cipher.doFinal(token.toByteArray(Charsets.UTF_8))
            prefs.edit().putString(key, Base64.encodeToString(sealed, Base64.NO_WRAP)).apply()
        } catch (_: Exception) {
            // Without a key the token is not kept; pairing works for this session only.
        }
    }

    fun remove(key: String) {
        prefs.edit().remove(key).apply()
    }

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "hitcam.tokens"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
    }
}

class LocalStore(context: Context) {
    private val prefs: SharedPreferences = context.getSharedPreferences("hitcam", Context.MODE_PRIVATE)

    /** Random per-install identity presented to the PC (not tied to any hardware identifier). */
    val deviceId: String
        get() = prefs.getString("deviceId", null) ?: UUID.randomUUID().toString().uppercase().also {
            prefs.edit().putString("deviceId", it).apply()
        }

    var recentServers: List<ServerAddress>
        get() = prefs.getString("recentServers", null)?.let {
            runCatching { ProtocolJson.decodeFromString<List<ServerAddress>>(it) }.getOrNull()
        } ?: emptyList()
        set(value) {
            prefs.edit().putString("recentServers", ProtocolJson.encodeToString(value.take(5))).apply()
        }

    fun remember(server: ServerAddress) {
        recentServers = listOf(server) + recentServers.filter { it.host != server.host || it.port != server.port }
    }

    var cameraState: CameraState?
        get() = prefs.getString("cameraState", null)?.let {
            runCatching { ProtocolJson.decodeFromString<CameraState>(it) }.getOrNull()
        }
        set(value) {
            prefs.edit().apply {
                if (value == null) remove("cameraState") else putString("cameraState", ProtocolJson.encodeToString(value))
            }.apply()
        }
}
