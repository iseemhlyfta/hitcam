package io.github.hitnes.hitcam.camera

import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.os.Build
import android.util.Range
import io.github.hitnes.hitcam.protocol.CameraInfo

/**
 * One lens offered to the PC and how to open it: a camera id, optionally a physical camera of a logical multi-camera,
 * and a base zoom ratio (an ultra-wide reached through a logical camera's zoom range below 1×).
 */
class CameraSpec(
    val info: CameraInfo,
    val cameraId: String,
    val physicalId: String?,
    /** Characteristics of what actually produces the frames (the physical camera if there is one). */
    val characteristics: CameraCharacteristics,
    /** Characteristics of the opened camera (the logical one for a physical lens). */
    val deviceCharacteristics: CameraCharacteristics,
    val baseZoomRatio: Float,
) {
    val id: String get() = info.id
    val isFront: Boolean get() = info.position == "front"
    val sensorOrientation: Int get() = characteristics.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90

    /** Zoom through CONTROL_ZOOM_RATIO (Android 11+) instead of a crop region. */
    val zoomRatioRange: Range<Float>?
        get() = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && physicalId == null) {
            deviceCharacteristics.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE)
        } else {
            null
        }
}

/** Finds the phone's lenses and maps them to the ids the PC knows: back-wide, back-ultrawide, back-tele, front. */
object CameraCatalog {
    fun discover(manager: CameraManager): List<CameraSpec> {
        val all = manager.cameraIdList.mapNotNull { id ->
            runCatching { id to manager.getCameraCharacteristics(id) }.getOrNull()
        }.filter { (_, c) -> isUsable(c) }

        val backs = all.filter { (_, c) -> c.get(CameraCharacteristics.LENS_FACING) == CameraMetadata.LENS_FACING_BACK }
        val front = all.firstOrNull { (_, c) -> c.get(CameraCharacteristics.LENS_FACING) == CameraMetadata.LENS_FACING_FRONT }
        val result = mutableListOf<CameraSpec>()

        val main = backs.firstOrNull()
        if (main != null) {
            val (mainId, mainChars) = main
            result += spec("back-wide", "Wide", "back", mainId, null, mainChars, mainChars, 1f)
            val mainFocal = normalizedFocal(mainChars)

            // Other lenses: separate camera ids first, then the physical cameras behind the main logical camera.
            val others = backs.drop(1).map { (id, c) -> Triple(id, null as String?, c) } + physicalCameras(manager, mainId, mainChars)
            val wider = others.filter { normalizedFocal(it.third) < mainFocal * 0.75f }.minByOrNull { normalizedFocal(it.third) }
            val longer = others.filter { normalizedFocal(it.third) > mainFocal * 1.5f }.maxByOrNull { normalizedFocal(it.third) }

            val zoomOut = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                mainChars.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE)?.lower?.takeIf { it < 0.9f }
            } else {
                null
            }
            when {
                wider != null && wider.second == null ->
                    result += spec("back-ultrawide", "Ultra Wide", "back", wider.first, null, wider.third, wider.third, 1f)
                zoomOut != null ->
                    result += spec("back-ultrawide", "Ultra Wide", "back", mainId, null, mainChars, mainChars, zoomOut)
                wider != null ->
                    result += spec("back-ultrawide", "Ultra Wide", "back", mainId, wider.second, wider.third, mainChars, 1f)
            }
            if (longer != null) {
                result += if (longer.second == null) {
                    spec("back-tele", "Telephoto", "back", longer.first, null, longer.third, longer.third, 1f)
                } else {
                    spec("back-tele", "Telephoto", "back", mainId, longer.second, longer.third, mainChars, 1f)
                }
            }
        }
        if (front != null) {
            result += spec("front", "Front", "front", front.first, null, front.second, front.second, 1f)
        }
        return result
    }

    private fun spec(
        id: String, name: String, position: String, cameraId: String, physicalId: String?,
        characteristics: CameraCharacteristics, device: CameraCharacteristics, baseZoom: Float,
    ): CameraSpec {
        val capabilities = characteristics.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)?.toSet().orEmpty()
        val maxZoom = if (physicalId != null) {
            1.0
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && device.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE) != null) {
            val upper = device.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE)!!.upper
            // An ultra-wide reached by zooming out stops where the wide lens begins.
            if (baseZoom < 1f) (1f / baseZoom).toDouble() else (upper / baseZoom).toDouble()
        } else {
            (characteristics.get(CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM) ?: 1f).toDouble()
        }
        val awbModes = characteristics.get(CameraCharacteristics.CONTROL_AWB_AVAILABLE_MODES)?.toSet().orEmpty()
        val afModes = characteristics.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES)?.toSet().orEmpty()
        val minimumFocus = characteristics.get(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f
        val info = CameraInfo(
            id = id,
            name = name,
            position = position,
            minZoom = 1.0,
            maxZoom = maxZoom.coerceIn(1.0, 10.0),
            hasTorch = characteristics.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true,
            supportsFocus = minimumFocus > 0f && CameraMetadata.CONTROL_AF_MODE_OFF in afModes &&
                CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_MANUAL_SENSOR in capabilities,
            supportsWhiteBalance = CameraMetadata.CONTROL_AWB_MODE_OFF in awbModes &&
                CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_MANUAL_POST_PROCESSING in capabilities,
            supportsExposureLock = characteristics.get(CameraCharacteristics.CONTROL_AE_LOCK_AVAILABLE) == true,
        )
        return CameraSpec(info, cameraId, physicalId, characteristics, device, baseZoom)
    }

    private fun physicalCameras(manager: CameraManager, logicalId: String, logical: CameraCharacteristics): List<Triple<String, String?, CameraCharacteristics>> {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) return emptyList()
        val capabilities = logical.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)?.toSet().orEmpty()
        if (CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_LOGICAL_MULTI_CAMERA !in capabilities) return emptyList()
        return logical.physicalCameraIds.mapNotNull { physicalId ->
            val chars = runCatching { manager.getCameraCharacteristics(physicalId) }.getOrNull() ?: return@mapNotNull null
            if (!isUsable(chars)) null else Triple(logicalId, physicalId, chars)
        }
    }

    /** A colour camera that can stream at least 1080p to a SurfaceTexture (skips depth, macro and mono sensors). */
    private fun isUsable(characteristics: CameraCharacteristics): Boolean {
        val capabilities = characteristics.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)?.toSet().orEmpty()
        if (CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_BACKWARD_COMPATIBLE !in capabilities) return false
        val map = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return false
        val sizes = map.getOutputSizes(SurfaceTexture::class.java) ?: return false
        return sizes.any { it.width >= 1920 && it.height >= 1080 || it.width >= 1080 && it.height >= 1920 }
    }

    /** Focal length relative to the sensor width: proportional to the 35 mm equivalent focal length. */
    private fun normalizedFocal(characteristics: CameraCharacteristics): Float {
        val focal = characteristics.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.firstOrNull() ?: return 1f
        val width = characteristics.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE)?.width ?: return focal
        return if (width > 0f) focal / width else focal
    }
}
