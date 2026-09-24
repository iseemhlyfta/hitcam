package io.github.hitnes.hitcam

import io.github.hitnes.hitcam.camera.WhiteBalance
import io.github.hitnes.hitcam.camera.WhiteBalanceGains
import io.github.hitnes.hitcam.video.AnnexB
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class VideoTest {
    private val sps = byteArrayOf(0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0x11)
    private val idr = byteArrayOf(0, 0, 0, 1, 0x65, 0x22, 0x33)

    @Test
    fun keyframeGetsParameterSets() {
        assertArrayEquals(sps + idr, AnnexB.withParameterSets(sps, idr))
    }

    @Test
    fun keyframeThatAlreadyHasThemIsLeftAlone() {
        val full = sps + idr
        assertSame(full, AnnexB.withParameterSets(sps, full))
        assertSame(idr, AnnexB.withParameterSets(null, idr))
    }

    @Test
    fun nalTypesAcceptShortStartCodes() {
        assertEquals(listOf(7, 8, 5), AnnexB.nalTypes(byteArrayOf(0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 0, 0, 1, 0x65, 9)))
    }

    @Test
    fun whiteBalanceModelRoundTrips() {
        for (kelvin in listOf(2500.0, 4000.0, 5500.0, 8000.0)) {
            val (red, blue) = WhiteBalance.modelGains(kelvin)
            val estimate = WhiteBalance.estimateTemperature(WhiteBalanceGains(red.toFloat(), 1f, blue.toFloat()))
            assertEquals(kelvin, estimate, kelvin * 0.001)
        }
    }

    @Test
    fun lockingAtTheCurrentValueKeepsTheAutoGains() {
        val auto = WhiteBalanceGains(2.3f, 1f, 1.4f)
        val calibration = WhiteBalance.Calibration.from(auto)
        val locked = WhiteBalance.gains(WhiteBalance.estimateTemperature(auto), 0.0, calibration)

        assertEquals(auto.red, locked.red, 1e-3f)
        assertEquals(auto.green, locked.green, 1e-3f)
        assertEquals(auto.blue, locked.blue, 1e-3f)
    }

    @Test
    fun warmerLightNeedsMoreBlueGain() {
        val cold = WhiteBalance.gains(7000.0, 0.0, WhiteBalance.Calibration.NONE)
        val warm = WhiteBalance.gains(3000.0, 0.0, WhiteBalance.Calibration.NONE)
        assertTrue(warm.blue / warm.red > cold.blue / cold.red)
    }

    @Test
    fun magentaTintLowersGreenAndGainsStayAboveOne() {
        val neutral = WhiteBalance.gains(5000.0, 0.0, WhiteBalance.Calibration.NONE)
        val magenta = WhiteBalance.gains(5000.0, 100.0, WhiteBalance.Calibration.NONE)
        assertTrue(magenta.green / magenta.red < neutral.green / neutral.red)
        for (gains in listOf(neutral, magenta, WhiteBalance.gains(2000.0, -150.0, WhiteBalance.Calibration.NONE))) {
            assertTrue(minOf(gains.red, gains.green, gains.blue) >= 0.999f)
        }
    }
}
