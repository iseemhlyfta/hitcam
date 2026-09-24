package io.github.hitnes.hitcam.session

import io.github.hitnes.hitcam.camera.CameraSnapshot
import io.github.hitnes.hitcam.camera.CameraUpdate
import io.github.hitnes.hitcam.camera.OutputSize
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.Capabilities
import io.github.hitnes.hitcam.protocol.Control
import io.github.hitnes.hitcam.protocol.ServerAddress
import io.github.hitnes.hitcam.video.EncodedFrame

/** Camera plus encoder, as the session sees them (a fake in unit tests). */
interface VideoSource {
    val capabilities: Capabilities
    val cameraIds: List<String>

    /** Encoded frames, on any thread. */
    var onFrame: ((EncodedFrame) -> Unit)?

    /** The camera was lost while running. */
    var onError: ((String) -> Unit)?

    fun start(initial: CameraState, completion: (Result<CameraSnapshot>) -> Unit)
    fun stop()
    fun apply(control: Control, completion: (CameraUpdate) -> Unit)

    /** (Re)creates the encoder for this format; frames follow, the first one a keyframe. */
    fun configureEncoder(size: OutputSize, fps: Int, bitrateKbps: Int)
    fun setBitrate(kbps: Int)
    fun requestKeyframe()
}

/** Everything else the session needs from the phone. */
interface SessionEnvironment {
    val deviceId: String
    val deviceName: String
    val model: String
    val appVersion: String

    fun token(key: String): String?
    fun saveToken(token: String, key: String)
    fun removeToken(key: String)

    fun remember(server: ServerAddress)
    var cameraState: CameraState?

    /** Battery level 0…1 and whether it is charging. */
    fun battery(): Pair<Double, Boolean>

    /** "nominal" | "fair" | "serious" | "critical" */
    fun thermal(): String
}
