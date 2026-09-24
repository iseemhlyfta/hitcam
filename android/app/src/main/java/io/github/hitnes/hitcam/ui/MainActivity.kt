package io.github.hitnes.hitcam.ui

import android.content.pm.ActivityInfo
import android.hardware.display.DisplayManager
import android.os.Bundle
import android.view.Surface
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.hitnes.hitcam.HitCamApp
import io.github.hitnes.hitcam.session.Phase

class MainActivity : ComponentActivity() {
    private val app get() = application as HitCamApp

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) = Unit
        override fun onDisplayRemoved(displayId: Int) = Unit
        override fun onDisplayChanged(displayId: Int) = reportRotation()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            HitCamTheme {
                val phase by app.session.phase.collectAsStateWithLifecycle()
                LaunchedEffect(phase) { onPhase(phase) }
                Root(app, phase)
            }
        }
    }

    override fun onStart() {
        super.onStart()
        getSystemService(DisplayManager::class.java).registerDisplayListener(displayListener, null)
        reportRotation()
        app.session.resumeCamera()
    }

    override fun onStop() {
        // Android does not let a background app use the camera: release it, keep the connection.
        app.session.pauseCamera()
        getSystemService(DisplayManager::class.java).unregisterDisplayListener(displayListener)
        super.onStop()
    }

    /** Streaming is landscape-only, like a webcam; the screen stays on while it runs. */
    private fun onPhase(phase: Phase) {
        val live = phase is Phase.Streaming || phase is Phase.Reconnecting
        requestedOrientation = if (live) ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE else ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        if (live) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    private fun reportRotation() {
        @Suppress("DEPRECATION")
        val rotation = windowManager.defaultDisplay.rotation
        val degrees = when (rotation) {
            Surface.ROTATION_90 -> 90
            Surface.ROTATION_180 -> 180
            Surface.ROTATION_270 -> 270
            else -> 0
        }
        app.video.setDisplayRotation(degrees)
    }
}
