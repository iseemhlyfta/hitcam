package io.github.hitnes.hitcam

import io.github.hitnes.hitcam.camera.CameraRules
import io.github.hitnes.hitcam.camera.OutputSize
import io.github.hitnes.hitcam.protocol.Control
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CameraRulesTest {
    private val ids = listOf("back-wide", "front")
    private val base = CameraRules.defaultState

    @Test
    fun unknownCameraIsIgnored() {
        assertEquals("back-wide", CameraRules.applying(Control(cameraId = "back-tele"), base, ids).cameraId)
        assertEquals("front", CameraRules.applying(Control(cameraId = "front"), base, ids).cameraId)
    }

    @Test
    fun onlyPresetSizesAreAccepted() {
        val odd = CameraRules.applying(Control(width = 640, height = 480), base, ids)
        assertEquals(1920 to 1080, odd.width to odd.height)
        val hd = CameraRules.applying(Control(width = 1280, height = 720, fps = 60), base, ids)
        assertEquals(Triple(1280, 720, 60), Triple(hd.width, hd.height, hd.fps))
    }

    @Test
    fun unsupportedFpsIsIgnored() {
        assertEquals(30, CameraRules.applying(Control(fps = 24), base, ids).fps)
    }

    @Test
    fun bitrateIsClamped() {
        assertEquals(500, CameraRules.applying(Control(bitrateKbps = 1), base, ids).bitrateKbps)
        assertEquals(50_000, CameraRules.applying(Control(bitrateKbps = 999_999), base, ids).bitrateKbps)
    }

    @Test
    fun rotationMustBeARightAngle() {
        assertEquals(0, CameraRules.applying(Control(rotation = 45), base, ids).rotation)
        assertEquals(270, CameraRules.applying(Control(rotation = 270), base, ids).rotation)
    }

    @Test
    fun stabilizationIsLimitedToTheOfferedModes() {
        assertNull(CameraRules.applying(Control(stabilization = "standard"), base, ids).stabilization)
        val offered = base.copy(stabilization = "off", stabilizationModes = listOf("off", "standard"))
        assertEquals("standard", CameraRules.applying(Control(stabilization = "standard"), offered, ids).stabilization)
        assertEquals("off", CameraRules.applying(Control(stabilization = "cinematic"), offered, ids).stabilization)
    }

    @Test
    fun noiseReductionIsLimitedToTheOfferedModes() {
        assertNull(CameraRules.applying(Control(noiseReduction = "off"), base, ids).noiseReduction)
        val offered = base.copy(noiseReduction = "high", noiseReductionModes = listOf("off", "fast", "high"))
        assertEquals("off", CameraRules.applying(Control(noiseReduction = "off"), offered, ids).noiseReduction)
        assertEquals("fast", CameraRules.applying(Control(noiseReduction = "fast"), offered, ids).noiseReduction)
        assertEquals("high", CameraRules.applying(Control(noiseReduction = "minimal"), offered, ids).noiseReduction)
        val noHigh = base.copy(noiseReduction = "fast", noiseReductionModes = listOf("off", "fast"))
        assertEquals("fast", CameraRules.applying(Control(noiseReduction = "high"), noHigh, ids).noiseReduction)
    }

    @Test
    fun noiseReductionModesNeedAChoice() {
        assertEquals(listOf("off", "fast", "high"), CameraRules.noiseReductionModes(setOf("high", "off", "fast")))
        assertEquals(listOf("off", "fast"), CameraRules.noiseReductionModes(setOf("fast", "off")))
        // LEGACY cameras list only FAST: nothing to choose.
        assertNull(CameraRules.noiseReductionModes(setOf("fast")))
        assertNull(CameraRules.noiseReductionModes(emptySet()))
    }

    @Test
    fun noiseReductionDefaultsToFastAndSurvivesLensChanges() {
        val all = listOf("off", "fast", "high")
        val noHigh = listOf("off", "fast")
        // "fast", as the recording template had it: "high" may cost frames at 60 fps.
        assertEquals("fast", CameraRules.noiseReduction(null, all))
        assertEquals("fast", CameraRules.noiseReduction(null, noHigh))
        assertEquals("off", CameraRules.noiseReduction(null, listOf("off", "high")))
        assertEquals("off", CameraRules.noiseReduction(null, listOf("off")))
        assertNull(CameraRules.noiseReduction("high", null))
        // A choice the new camera supports is kept, otherwise the new camera's default is used.
        assertEquals("off", CameraRules.noiseReduction("off", noHigh))
        assertEquals("fast", CameraRules.noiseReduction("high", noHigh))
        assertEquals("high", CameraRules.noiseReduction("high", all))
        assertEquals("fast", CameraRules.noiseReduction("bogus", all))
    }

    @Test
    fun anExplicitNoiseReductionRequestIsRememberedEvenWhenTheModeStaysTheSame() {
        // "high" chosen, then a lens without it fell back to "fast": asking for "fast" there is a new choice.
        val fellBack = base.copy(noiseReduction = "fast", noiseReductionModes = listOf("off", "fast"))
        assertEquals("fast", CameraRules.noiseReductionChoice("high", Control(noiseReduction = "fast"), fellBack))
        // Not offered (the camera kept "fast"), or not asked for at all: the choice stays.
        assertEquals("high", CameraRules.noiseReductionChoice("high", Control(noiseReduction = "high"), fellBack))
        assertEquals("high", CameraRules.noiseReductionChoice("high", Control(zoom = 2.0), fellBack))
    }

    @Test
    fun storedStateIsSanitized() {
        assertEquals(base, CameraRules.sanitized(null, ids))
        assertEquals(base, CameraRules.sanitized(base.copy(cameraId = "back-tele"), ids))
        assertEquals(base, CameraRules.sanitized(base.copy(fps = 24), ids))
        assertEquals(base, CameraRules.sanitized(base.copy(bitrateKbps = 10), ids))
        val front = base.copy(cameraId = "front", rotation = 90)
        assertEquals(front, CameraRules.sanitized(front, ids))
        assertTrue(CameraRules.isValid(front, ids))
        assertFalse(CameraRules.isValid(base.copy(rotation = 45), ids))
    }

    @Test
    fun portraitRotationSwapsTheOutputSize() {
        assertEquals(OutputSize(1920, 1080), CameraRules.outputSize(base))
        assertEquals(OutputSize(1080, 1920), CameraRules.outputSize(base.copy(rotation = 90)))
        assertEquals(OutputSize(1920, 1080), CameraRules.outputSize(base.copy(rotation = 180)))
    }

    @Test
    fun lensPositionMapsToDiopters() {
        assertEquals(10f, CameraRules.focusDistance(0.0, 10f), 0f)
        assertEquals(0f, CameraRules.focusDistance(1.0, 10f), 0f)
        assertEquals(0.3, CameraRules.lensPosition(CameraRules.focusDistance(0.3, 8f), 8f), 1e-6)
    }
}
