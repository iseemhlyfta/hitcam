package io.github.hitnes.hitcam.camera

import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.Control
import io.github.hitnes.hitcam.protocol.VideoPreset

/** Output frame size after rotation. */
data class OutputSize(val width: Int, val height: Int)

/** Pure rules for camera states requested by the PC or restored from storage (no Android APIs, unit-tested). */
object CameraRules {
    val presets = listOf(
        VideoPreset(1280, 720, listOf(30, 60)),
        VideoPreset(1920, 1080, listOf(30, 60)),
    )
    val bitrateRange = 500..50_000
    val rotations = listOf(0, 90, 180, 270)

    val defaultState = CameraState(
        cameraId = "back-wide", zoom = 1.0, torch = false, focusMode = "continuous", lensPosition = 0.5,
        exposureBias = 0.0, mirror = false, rotation = 0, width = 1920, height = 1080, fps = 30, bitrateKbps = 8000,
    )

    /** Applies the stream-related fields of [control]. Values the phone does not offer are ignored, never stored. */
    fun applying(control: Control, state: CameraState, cameraIds: List<String>, presets: List<VideoPreset> = this.presets): CameraState {
        var next = state
        control.cameraId?.let { if (it in cameraIds) next = next.copy(cameraId = it) }
        val width = control.width
        val height = control.height
        if (width != null && height != null && presets.any { it.width == width && it.height == height }) {
            next = next.copy(width = width, height = height)
        }
        val preset = presets.firstOrNull { it.width == next.width && it.height == next.height }
        control.fps?.let { if (preset != null && it in preset.fps) next = next.copy(fps = it) }
        // A new size may not support the current frame rate.
        if (preset != null && next.fps !in preset.fps && preset.fps.isNotEmpty()) next = next.copy(fps = preset.fps.first())
        control.bitrateKbps?.let { next = next.copy(bitrateKbps = it.coerceIn(bitrateRange)) }
        control.mirror?.let { next = next.copy(mirror = it) }
        control.rotation?.let { if (it in rotations) next = next.copy(rotation = it) }
        control.stabilization?.let { if (it in (state.stabilizationModes ?: listOf("off"))) next = next.copy(stabilization = it) }
        return next
    }

    /** Whether a stored state can be used to start the camera. */
    fun isValid(state: CameraState, cameraIds: List<String>, presets: List<VideoPreset> = this.presets): Boolean =
        state.cameraId in cameraIds &&
            presets.any { it.width == state.width && it.height == state.height && state.fps in it.fps } &&
            state.bitrateKbps in bitrateRange &&
            state.rotation in rotations

    /** The stored state if it is usable, otherwise the defaults. */
    fun sanitized(stored: CameraState?, cameraIds: List<String>, presets: List<VideoPreset> = this.presets): CameraState =
        if (stored != null && isValid(stored, cameraIds, presets)) stored else defaultState

    /** Portrait rotations swap width and height. */
    fun outputSize(state: CameraState): OutputSize =
        if (state.rotation == 90 || state.rotation == 270) OutputSize(state.height, state.width) else OutputSize(state.width, state.height)

    /** Lens position 0 (near) … 1 (far) as Camera2 focus distance in diopters (0 = infinity). */
    fun focusDistance(lensPosition: Double, minimumFocusDistance: Float): Float =
        ((1 - lensPosition.coerceIn(0.0, 1.0)) * minimumFocusDistance).toFloat()

    fun lensPosition(focusDistance: Float, minimumFocusDistance: Float): Double =
        if (minimumFocusDistance <= 0f) 0.5 else (1 - focusDistance / minimumFocusDistance).toDouble().coerceIn(0.0, 1.0)
}
