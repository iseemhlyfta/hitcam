package io.github.hitnes.hitcam.video

import android.hardware.camera2.CameraManager
import android.view.Surface
import io.github.hitnes.hitcam.camera.CameraController
import io.github.hitnes.hitcam.camera.CameraSnapshot
import io.github.hitnes.hitcam.camera.CameraUpdate
import io.github.hitnes.hitcam.camera.OutputSize
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.Control
import io.github.hitnes.hitcam.session.VideoSource

/** Camera2 → OpenGL → MediaCodec, the phone's [VideoSource]. */
class VideoCapture(manager: CameraManager) : VideoSource {
    private val gl = GlPipeline()
    private val camera = CameraController(manager, gl)
    private val encoder = H264Encoder { frame -> onFrame?.invoke(frame) }
    private val lock = Any()

    override val capabilities get() = camera.capabilities
    override val cameraIds get() = camera.cameraIds
    override var onFrame: ((EncodedFrame) -> Unit)? = null
    override var onError: ((String) -> Unit)?
        get() = camera.onError
        set(value) {
            camera.onError = value
        }

    override fun start(initial: CameraState, completion: (Result<CameraSnapshot>) -> Unit) = camera.start(initial, completion)

    override fun stop() {
        camera.stop()
        synchronized(lock) {
            gl.setEncoderSurface(null, 0, 0)
            encoder.release()
        }
    }

    override fun apply(control: Control, completion: (CameraUpdate) -> Unit) = camera.apply(control, completion)

    override fun configureEncoder(size: OutputSize, fps: Int, bitrateKbps: Int) {
        synchronized(lock) {
            // The GL thread must stop drawing into the old encoder's surface before it is released.
            gl.setEncoderSurface(null, 0, 0)
            val surface = encoder.configure(size.width, size.height, fps, bitrateKbps)
            gl.setEncoderSurface(surface, size.width, size.height)
        }
    }

    override fun setBitrate(kbps: Int) = encoder.setBitrate(kbps)

    override fun requestKeyframe() = encoder.requestKeyframe()

    /** The on-screen preview; null when it goes away. Returns only once GL no longer uses the old surface. */
    fun setPreviewSurface(surface: Surface?, width: Int, height: Int) = gl.setPreviewSurface(surface, width, height)

    fun setDisplayRotation(degrees: Int) = camera.setDisplayRotation(degrees)
}
