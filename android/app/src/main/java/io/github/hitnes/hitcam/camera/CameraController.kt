package io.github.hitnes.hitcam.camera

import android.annotation.SuppressLint
import android.graphics.Rect
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.CaptureResult
import android.hardware.camera2.TotalCaptureResult
import android.hardware.camera2.params.ColorSpaceTransform
import android.hardware.camera2.params.MeteringRectangle
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.RggbChannelVector
import android.hardware.camera2.params.SessionConfiguration
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Range
import android.util.Size
import android.view.Surface
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.Capabilities
import io.github.hitnes.hitcam.protocol.Control
import io.github.hitnes.hitcam.protocol.NormalizedPoint
import io.github.hitnes.hitcam.video.FrameGeometry
import io.github.hitnes.hitcam.video.GlPipeline
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt

/** A consistent copy of the camera state and the matching encoder size. */
data class CameraSnapshot(val state: CameraState, val outputSize: OutputSize)

/** Result of a control request. */
data class CameraUpdate(
    val snapshot: CameraSnapshot,
    /** Frame size or rate changed: the encoder must be recreated. */
    val formatChanged: Boolean,
    val bitrateChanged: Boolean,
    /** The requested format could not be applied and the previous one was restored. */
    val failed: Boolean,
)

/**
 * Owns the Camera2 device. Configuration runs on one thread ([ops]) and may block on camera callbacks,
 * which arrive on [cameraThread]. Frames go to the [gl] pipeline's surface.
 */
class CameraController(private val manager: CameraManager, private val gl: GlPipeline) {
    val cameras: List<CameraSpec> = CameraCatalog.discover(manager)
    val cameraIds: List<String> = cameras.map { it.id }
    val capabilities = Capabilities(cameras.map { it.info }, CameraRules.presets)

    private val ops = Executors.newSingleThreadExecutor { Thread(it, "hitcam-camera-ops") }
    private val cameraThread = HandlerThread("hitcam-camera").apply { start() }
    private val cameraHandler = Handler(cameraThread.looper)

    // Owned by `ops`.
    private var state = CameraRules.defaultState
    private var spec: CameraSpec? = null
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var surface: Surface? = null
    private var fpsRange: Range<Int>? = null
    private var displayRotation = 90
    private var wbCalibration = WhiteBalance.Calibration.NONE
    private var wbGains: RggbChannelVector? = null
    private var wbTransform: ColorSpaceTransform? = null
    private var focusRegion: MeteringRectangle? = null

    // Written by capture results on the camera thread.
    @Volatile private var lastGains: RggbChannelVector? = null
    @Volatile private var lastTransform: ColorSpaceTransform? = null

    /** Called when the camera is lost (another app took it, or it failed) while streaming. */
    var onError: ((String) -> Unit)? = null

    fun start(initial: CameraState, completion: (Result<CameraSnapshot>) -> Unit) {
        ops.execute {
            try {
                state = initial
                configureSession()
                completion(Result.success(snapshot()))
            } catch (e: Exception) {
                closeCamera()
                completion(Result.failure(e))
            }
        }
    }

    fun stop() {
        ops.execute { closeCamera() }
    }

    /** Applies a control request; [completion] gets the resulting state and what changed. */
    fun apply(control: Control, completion: (CameraUpdate) -> Unit) {
        ops.execute {
            val before = state
            val next = CameraRules.applying(control, before, cameraIds)
            val needsReconfigure = next.cameraId != before.cameraId || next.width != before.width ||
                next.height != before.height || next.fps != before.fps
            state = next
            var failed = false
            if (needsReconfigure && device != null) {
                try {
                    configureSession()
                } catch (e: Exception) {
                    // Not supported by this phone after all: go back to the configuration that worked.
                    failed = true
                    state = before
                    runCatching { configureSession() }
                }
            } else if (next.mirror != before.mirror || next.rotation != before.rotation) {
                updateGeometry()
            }

            if (control.whiteBalanceMode != null || control.whiteBalanceTemperature != null || control.whiteBalanceTint != null) {
                // A temperature or tint alone means "lock at this value".
                setWhiteBalance(control.whiteBalanceMode ?: "locked", control.whiteBalanceTemperature, control.whiteBalanceTint)
            }
            control.exposureMode?.let { setExposureMode(it) }
            control.zoom?.let { setZoom(it) }
            control.torch?.let { setTorch(it) }
            control.exposureBias?.let { setExposureBias(it) }
            var triggerFocus = false
            when {
                control.focusPoint != null -> triggerFocus = focus(control.focusPoint)
                control.focusMode != null -> triggerFocus = setFocus(control.focusMode, control.lensPosition)
                control.lensPosition != null -> setFocus("locked", control.lensPosition)
            }
            if (next.stabilization != before.stabilization) state = state.copy(stabilization = next.stabilization)
            submit(triggerFocus)

            val snapshot = snapshot()
            val formatChanged = snapshot.outputSize != CameraRules.outputSize(before) || snapshot.state.fps != before.fps
            completion(CameraUpdate(snapshot, formatChanged, snapshot.state.bitrateKbps != before.bitrateKbps, failed))
        }
    }

    /** The screen turned: keep the stream level with the horizon. Only landscape rotations (90/270) are used. */
    fun setDisplayRotation(degrees: Int) {
        ops.execute {
            if ((degrees == 90 || degrees == 270) && degrees != displayRotation) {
                displayRotation = degrees
                updateGeometry()
            }
        }
    }

    fun release() {
        ops.execute {
            closeCamera()
            cameraThread.quitSafely()
        }
        ops.shutdown()
    }

    // MARK: Configuration (ops thread)

    private fun snapshot(): CameraSnapshot {
        // Auto white balance: report what the camera currently uses, so locking starts from it.
        var reported = state
        if (reported.whiteBalanceMode != "locked") {
            lastGains?.let { gains ->
                reported = reported.copy(whiteBalanceTemperature = WhiteBalance.estimateTemperature(gains.toGains()).roundToInt().toDouble(), whiteBalanceTint = 0.0)
            }
        }
        state = reported
        return CameraSnapshot(reported, CameraRules.outputSize(reported))
    }

    @SuppressLint("MissingPermission")
    private fun configureSession() {
        val spec = cameras.firstOrNull { it.id == state.cameraId } ?: cameras.firstOrNull()
            ?: throw IllegalStateException("No camera")
        closeCamera()
        this.spec = spec

        val map = spec.characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            ?: throw IllegalStateException("Camera has no stream configurations")
        val sizes = map.getOutputSizes(SurfaceTexture::class.java).orEmpty().toList()
        val size = chooseSize(sizes, state.width, state.height) ?: throw IllegalStateException("Unsupported resolution")

        // The requested rate, falling back to 30 fps like the iPhone does.
        val ranges = spec.deviceCharacteristics.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES).orEmpty().toList()
        val minFrameNanos = map.getOutputMinFrameDuration(SurfaceTexture::class.java, size)
        var fps = state.fps
        var range = chooseFpsRange(ranges, fps, minFrameNanos)
        if (range == null && fps > 30) {
            fps = 30
            range = chooseFpsRange(ranges, fps, minFrameNanos)
        }
        if (range == null) throw IllegalStateException("Unsupported frame rate")
        fpsRange = range

        val surface = gl.cameraSurface(size.width, size.height)
        this.surface = surface
        val device = openDevice(spec.cameraId)
        this.device = device
        session = createSession(device, surface, spec)

        val stabilizationModes = buildList {
            add("off")
            val modes = spec.deviceCharacteristics.get(CameraCharacteristics.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES)
            if (modes?.contains(CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_ON) == true) add("standard")
        }
        val stabilization = state.stabilization?.takeIf { it in stabilizationModes } ?: "off"
        wbCalibration = WhiteBalance.Calibration.NONE
        wbGains = null
        wbTransform = null
        focusRegion = null
        lastGains = null
        lastTransform = null
        state = state.copy(
            fps = fps, zoom = 1.0, torch = false, focusMode = "continuous", exposureBias = 0.0, exposureMode = "auto",
            whiteBalanceMode = "auto", whiteBalanceTemperature = state.whiteBalanceTemperature ?: 5000.0, whiteBalanceTint = 0.0,
            stabilization = stabilization, stabilizationModes = stabilizationModes,
        )
        updateGeometry()
        submit(false)
        // Let auto white balance settle once so the reported temperature is the camera's, not a default.
        waitForFirstResult()
    }

    private fun chooseSize(sizes: List<Size>, width: Int, height: Int): Size? {
        sizes.firstOrNull { it.width == width && it.height == height }?.let { return it }
        val sameAspect = sizes.filter { it.width * height == it.height * width }
        return sameAspect.filter { it.width >= width }.minByOrNull { it.width }
            ?: sameAspect.maxByOrNull { it.width }
            ?: sizes.filter { it.width >= width && it.height >= height }.minByOrNull { it.width * it.height }
    }

    private fun chooseFpsRange(ranges: List<Range<Int>>, fps: Int, minFrameNanos: Long): Range<Int>? {
        if (minFrameNanos > 0 && minFrameNanos > 1_000_000_000L / fps + 1_000_000) return null
        // A fixed rate like on the iPhone if offered, otherwise the range with the highest floor.
        return ranges.filter { it.upper == fps }.maxByOrNull { it.lower }
    }

    private fun openDevice(cameraId: String): CameraDevice {
        var opened: CameraDevice? = null
        var error: String? = null
        val done = CountDownLatch(1)
        manager.openCamera(cameraId, object : CameraDevice.StateCallback() {
            override fun onOpened(camera: CameraDevice) {
                opened = camera
                done.countDown()
            }

            override fun onDisconnected(camera: CameraDevice) {
                camera.close()
                if (done.count > 0) {
                    error = "Camera disconnected"
                    done.countDown()
                } else {
                    lost(camera, "Camera disconnected")
                }
            }

            override fun onError(camera: CameraDevice, code: Int) {
                camera.close()
                if (done.count > 0) {
                    error = "Camera error $code"
                    done.countDown()
                } else {
                    lost(camera, "Camera error $code")
                }
            }
        }, cameraHandler)
        if (!done.await(5, TimeUnit.SECONDS)) throw IllegalStateException("Camera did not open")
        return opened ?: throw IllegalStateException(error ?: "Camera did not open")
    }

    private fun lost(camera: CameraDevice, message: String) {
        ops.execute {
            if (device === camera) {
                device = null
                session = null
                onError?.invoke(message)
            }
        }
    }

    private fun createSession(device: CameraDevice, surface: Surface, spec: CameraSpec): CameraCaptureSession {
        var created: CameraCaptureSession? = null
        val done = CountDownLatch(1)
        val callback = object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(session: CameraCaptureSession) {
                created = session
                done.countDown()
            }

            override fun onConfigureFailed(session: CameraCaptureSession) {
                done.countDown()
            }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            val output = OutputConfiguration(surface)
            spec.physicalId?.let { output.setPhysicalCameraId(it) }
            val executor = java.util.concurrent.Executor { cameraHandler.post(it) }
            device.createCaptureSession(SessionConfiguration(SessionConfiguration.SESSION_REGULAR, listOf(output), executor, callback))
        } else {
            @Suppress("DEPRECATION")
            device.createCaptureSession(listOf(surface), callback, cameraHandler)
        }
        if (!done.await(5, TimeUnit.SECONDS)) throw IllegalStateException("Camera session timed out")
        return created ?: throw IllegalStateException("Camera session could not be configured")
    }

    private fun waitForFirstResult() {
        val deadline = SystemClock.uptimeMillis() + 500
        while (lastGains == null && SystemClock.uptimeMillis() < deadline) Thread.sleep(20)
    }

    private fun closeCamera() {
        try {
            session?.close()
        } catch (_: Exception) {
        }
        device?.close()
        session = null
        device = null
    }

    private fun updateGeometry() {
        val spec = spec ?: return
        gl.setGeometry(
            FrameGeometry(
                displayRotation = displayRotation,
                userRotation = state.rotation,
                mirror = state.mirror,
                frontFacing = spec.isFront,
                sensorOrientation = spec.sensorOrientation,
            ),
        )
    }

    /** Sends the repeating request built from the current state; [triggerFocus] starts a one-shot autofocus. */
    private fun submit(triggerFocus: Boolean) {
        val session = session ?: return
        val device = device ?: return
        val surface = surface ?: return
        val spec = spec ?: return
        try {
            val builder = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD)
            builder.addTarget(surface)
            fill(builder, spec)
            session.setRepeatingRequest(builder.build(), resultCallback, cameraHandler)
            if (triggerFocus) {
                builder.set(CaptureRequest.CONTROL_AF_TRIGGER, CameraMetadata.CONTROL_AF_TRIGGER_START)
                session.capture(builder.build(), null, cameraHandler)
            }
        } catch (e: Exception) {
            // The session closed meanwhile (camera lost); onError reports it.
        }
    }

    private val resultCallback = object : CameraCaptureSession.CaptureCallback() {
        override fun onCaptureCompleted(session: CameraCaptureSession, request: CaptureRequest, result: TotalCaptureResult) {
            result.get(CaptureResult.COLOR_CORRECTION_GAINS)?.let { lastGains = it }
            result.get(CaptureResult.COLOR_CORRECTION_TRANSFORM)?.let { lastTransform = it }
        }
    }

    private fun fill(builder: CaptureRequest.Builder, spec: CameraSpec) {
        val chars = spec.deviceCharacteristics
        builder.set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
        builder.set(CaptureRequest.CONTROL_AE_MODE, CameraMetadata.CONTROL_AE_MODE_ON)
        fpsRange?.let { builder.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) }
        builder.set(CaptureRequest.CONTROL_AE_LOCK, state.exposureMode == "locked")
        builder.set(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION, exposureSteps(state.exposureBias, chars))
        builder.set(CaptureRequest.FLASH_MODE, if (state.torch) CameraMetadata.FLASH_MODE_TORCH else CameraMetadata.FLASH_MODE_OFF)
        builder.set(
            CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,
            if (state.stabilization == "standard") CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_ON else CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_OFF,
        )

        // Zoom.
        val range = spec.zoomRatioRange
        if (range != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            builder.set(CaptureRequest.CONTROL_ZOOM_RATIO, (spec.baseZoomRatio * state.zoom.toFloat()).coerceIn(range.lower, range.upper))
        } else if (state.zoom > 1.0) {
            builder.set(CaptureRequest.SCALER_CROP_REGION, cropRegion(chars, state.zoom))
        }

        // Focus.
        when (state.focusMode) {
            "locked" -> {
                builder.set(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_OFF)
                val minimum = spec.characteristics.get(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f
                builder.set(CaptureRequest.LENS_FOCUS_DISTANCE, CameraRules.focusDistance(state.lensPosition, minimum))
            }
            "auto" -> builder.set(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_AUTO)
            else -> builder.set(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
        }
        focusRegion?.let { region ->
            if ((chars.get(CameraCharacteristics.CONTROL_MAX_REGIONS_AF) ?: 0) > 0) builder.set(CaptureRequest.CONTROL_AF_REGIONS, arrayOf(region))
            if ((chars.get(CameraCharacteristics.CONTROL_MAX_REGIONS_AE) ?: 0) > 0) builder.set(CaptureRequest.CONTROL_AE_REGIONS, arrayOf(region))
        }

        // White balance.
        val gains = wbGains
        val transform = wbTransform
        if (state.whiteBalanceMode == "locked" && gains != null && transform != null) {
            builder.set(CaptureRequest.CONTROL_AWB_MODE, CameraMetadata.CONTROL_AWB_MODE_OFF)
            builder.set(CaptureRequest.COLOR_CORRECTION_MODE, CameraMetadata.COLOR_CORRECTION_MODE_TRANSFORM_MATRIX)
            builder.set(CaptureRequest.COLOR_CORRECTION_GAINS, gains)
            builder.set(CaptureRequest.COLOR_CORRECTION_TRANSFORM, transform)
        } else {
            builder.set(CaptureRequest.CONTROL_AWB_MODE, CameraMetadata.CONTROL_AWB_MODE_AUTO)
        }
    }

    private fun exposureSteps(bias: Double, chars: CameraCharacteristics): Int {
        val step = chars.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_STEP)?.toDouble() ?: return 0
        val range = chars.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE) ?: return 0
        if (step <= 0) return 0
        return (bias / step).roundToInt().coerceIn(range.lower, range.upper)
    }

    private fun cropRegion(chars: CameraCharacteristics, zoom: Double): Rect {
        val active = chars.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE) ?: Rect(0, 0, 1920, 1080)
        val limit = (chars.get(CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM) ?: 1f).toDouble()
        val factor = zoom.coerceIn(1.0, limit)
        val width = (active.width() / factor).toInt()
        val height = (active.height() / factor).toInt()
        val left = active.left + (active.width() - width) / 2
        val top = active.top + (active.height() - height) / 2
        return Rect(left, top, left + width, top + height)
    }

    // MARK: Controls (ops thread; `submit` sends them)

    private fun info() = spec?.info

    private fun setZoom(zoom: Double) {
        val info = info() ?: return
        state = state.copy(zoom = zoom.coerceIn(info.minZoom, info.maxZoom))
    }

    private fun setTorch(on: Boolean) {
        if (info()?.hasTorch != true) return
        state = state.copy(torch = on)
    }

    private fun setExposureBias(bias: Double) {
        val chars = spec?.deviceCharacteristics ?: return
        val step = chars.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_STEP)?.toDouble() ?: return
        state = state.copy(exposureBias = exposureSteps(bias, chars) * step)
    }

    /** "locked": fixed gains for the given temperature/tint (missing values keep the current ones); otherwise auto. */
    private fun setWhiteBalance(mode: String, temperature: Double?, tint: Double?) {
        if (mode != "locked") {
            wbGains = null
            state = state.copy(whiteBalanceMode = "auto")
            return
        }
        if (info()?.supportsWhiteBalance != true) return
        val transform = wbTransform ?: lastTransform ?: return
        if (state.whiteBalanceMode != "locked") {
            // Start from what auto white balance uses right now, so locking alone never changes the colours.
            val auto = lastGains?.toGains()
            wbCalibration = auto?.let { WhiteBalance.Calibration.from(it) } ?: WhiteBalance.Calibration.NONE
            auto?.let { state = state.copy(whiteBalanceTemperature = WhiteBalance.estimateTemperature(it), whiteBalanceTint = 0.0) }
        }
        val kelvin = (temperature ?: state.whiteBalanceTemperature ?: 5000.0).coerceIn(WhiteBalance.MIN_TEMPERATURE, WhiteBalance.MAX_TEMPERATURE)
        val green = (tint ?: state.whiteBalanceTint ?: 0.0).coerceIn(-WhiteBalance.MAX_TINT, WhiteBalance.MAX_TINT)
        val gains = WhiteBalance.gains(kelvin, green, wbCalibration)
        wbGains = RggbChannelVector(gains.red, gains.green, gains.green, gains.blue)
        wbTransform = transform
        state = state.copy(whiteBalanceMode = "locked", whiteBalanceTemperature = kelvin.roundToInt().toDouble(), whiteBalanceTint = green.roundToInt().toDouble())
    }

    /** "locked" freezes the current exposure (bias no longer applies); otherwise auto. */
    private fun setExposureMode(mode: String) {
        if (mode == "locked") {
            if (info()?.supportsExposureLock != true) return
            state = state.copy(exposureMode = "locked")
        } else {
            state = state.copy(exposureMode = "auto")
        }
    }

    /** Returns whether a one-shot autofocus must be triggered. */
    private fun setFocus(mode: String, lensPosition: Double?): Boolean {
        val info = info() ?: return false
        focusRegion = null
        return when (mode) {
            "locked" -> {
                if (!info.supportsFocus) return false
                state = state.copy(focusMode = "locked", lensPosition = (lensPosition ?: state.lensPosition).coerceIn(0.0, 1.0))
                false
            }
            "auto" -> {
                state = state.copy(focusMode = "auto")
                true
            }
            else -> {
                state = state.copy(focusMode = "continuous")
                false
            }
        }
    }

    /** Focus and exposure on a point of the streamed picture (0…1, y down). */
    private fun focus(point: NormalizedPoint): Boolean {
        val spec = spec ?: return false
        val (x, y) = gl.outputToBuffer(point.x.toFloat().coerceIn(0f, 1f), point.y.toFloat().coerceIn(0f, 1f))
        val area = currentCrop(spec)
        val size = (minOf(area.width(), area.height()) * 0.15f).toInt().coerceAtLeast(1)
        val cx = area.left + (x * area.width()).toInt()
        val cy = area.top + (y * area.height()).toInt()
        val left = (cx - size / 2).coerceIn(area.left, area.right - size)
        val top = (cy - size / 2).coerceIn(area.top, area.bottom - size)
        focusRegion = MeteringRectangle(left, top, size, size, MeteringRectangle.METERING_WEIGHT_MAX - 1)
        state = state.copy(focusMode = "auto", exposureMode = "auto")
        return true
    }

    private fun currentCrop(spec: CameraSpec): Rect {
        val chars = spec.deviceCharacteristics
        val active = chars.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE) ?: Rect(0, 0, 1920, 1080)
        // With CONTROL_ZOOM_RATIO, regions are given in the zoomed field of view mapped onto the whole array.
        if (spec.zoomRatioRange != null || state.zoom <= 1.0) return Rect(0, 0, active.width(), active.height())
        val crop = cropRegion(chars, state.zoom)
        return Rect(crop.left - active.left, crop.top - active.top, crop.right - active.left, crop.bottom - active.top)
    }

    private fun RggbChannelVector.toGains() = WhiteBalanceGains(red, (greenEven + greenOdd) / 2, blue)
}
