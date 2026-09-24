package io.github.hitnes.hitcam

import android.app.Application
import android.content.Context
import android.hardware.camera2.CameraManager
import android.os.BatteryManager
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.ServerAddress
import io.github.hitnes.hitcam.session.SessionEnvironment
import io.github.hitnes.hitcam.session.StreamSession
import io.github.hitnes.hitcam.storage.LocalStore
import io.github.hitnes.hitcam.storage.TokenStore
import io.github.hitnes.hitcam.video.VideoCapture

/** Holds the one streaming session for the whole process, so it survives activity recreation. */
class HitCamApp : Application() {
    val localStore by lazy { LocalStore(this) }
    val video by lazy { VideoCapture(getSystemService(CameraManager::class.java)) }
    val session by lazy { StreamSession(video, AndroidEnvironment(this, localStore)) }
}

private class AndroidEnvironment(private val context: Context, private val store: LocalStore) : SessionEnvironment {
    private val tokens = TokenStore(context)

    override val deviceId: String get() = store.deviceId

    override val deviceName: String
        get() = Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() }
            ?: model

    override val model: String
        get() {
            val manufacturer = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
            return if (Build.MODEL.startsWith(Build.MANUFACTURER, ignoreCase = true)) Build.MODEL else "$manufacturer ${Build.MODEL}"
        }

    override val appVersion: String get() = BuildConfig.VERSION_NAME

    override fun token(key: String) = tokens.token(key)
    override fun saveToken(token: String, key: String) = tokens.save(token, key)
    override fun removeToken(key: String) = tokens.remove(key)
    override fun remember(server: ServerAddress) = store.remember(server)

    override var cameraState: CameraState?
        get() = store.cameraState
        set(value) {
            store.cameraState = value
        }

    override fun battery(): Pair<Double, Boolean> {
        val manager = context.getSystemService(BatteryManager::class.java) ?: return 0.0 to false
        val level = manager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY).coerceIn(0, 100) / 100.0
        return level to manager.isCharging
    }

    override fun thermal(): String {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return "nominal"
        return when (context.getSystemService(PowerManager::class.java)?.currentThermalStatus ?: PowerManager.THERMAL_STATUS_NONE) {
            PowerManager.THERMAL_STATUS_NONE, PowerManager.THERMAL_STATUS_LIGHT -> "nominal"
            PowerManager.THERMAL_STATUS_MODERATE -> "fair"
            PowerManager.THERMAL_STATUS_SEVERE -> "serious"
            else -> "critical"
        }
    }
}
