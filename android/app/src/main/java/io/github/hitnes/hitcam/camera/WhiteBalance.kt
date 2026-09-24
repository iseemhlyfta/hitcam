package io.github.hitnes.hitcam.camera

import kotlin.math.exp
import kotlin.math.ln

/** White balance gains relative to green (Camera2 COLOR_CORRECTION_GAINS with both greens equal). */
data class WhiteBalanceGains(val red: Float, val green: Float, val blue: Float)

/**
 * Kelvin/tint ↔ Camera2 gains. Camera2 has no temperature API, so a typical phone sensor is modelled: the log of the
 * red and blue gains is linear in mireds (fitted to ~2.0/1.6 in daylight and ~1.3/2.6 under incandescent light).
 * Locking starts from the camera's own auto gains ([Calibration]), so locking alone never changes the picture and
 * the model only decides how the colour moves from there.
 */
object WhiteBalance {
    const val MIN_TEMPERATURE = 2000.0
    const val MAX_TEMPERATURE = 10_000.0
    const val MAX_TINT = 150.0

    private const val REFERENCE_MIRED = 1e6 / 5500
    private val redAtReference = ln(2.0)
    private val blueAtReference = ln(1.6)
    private val redSlope = (ln(1.3) - ln(2.0)) / (1e6 / 2850 - REFERENCE_MIRED)
    private val blueSlope = (ln(2.6) - ln(1.6)) / (1e6 / 2850 - REFERENCE_MIRED)

    /** How much a given sensor differs from the model, taken from its auto gains at the moment of locking. */
    data class Calibration(val red: Double, val blue: Double) {
        companion object {
            val NONE = Calibration(1.0, 1.0)

            fun from(auto: WhiteBalanceGains): Calibration {
                val (red, blue) = modelGains(estimateTemperature(auto))
                return Calibration((auto.red / auto.green) / red, (auto.blue / auto.green) / blue)
            }
        }
    }

    fun modelGains(temperature: Double): Pair<Double, Double> {
        val mired = 1e6 / temperature.coerceIn(MIN_TEMPERATURE, MAX_TEMPERATURE)
        val offset = mired - REFERENCE_MIRED
        return exp(redAtReference + redSlope * offset) to exp(blueAtReference + blueSlope * offset)
    }

    /** Colour temperature that best explains [gains], clamped to 2000–10000 K. */
    fun estimateTemperature(gains: WhiteBalanceGains): Double {
        if (gains.green <= 0f || gains.red <= 0f || gains.blue <= 0f) return 5500.0
        val red = ln((gains.red / gains.green).toDouble()) - redAtReference
        val blue = ln((gains.blue / gains.green).toDouble()) - blueAtReference
        val offset = (redSlope * red + blueSlope * blue) / (redSlope * redSlope + blueSlope * blueSlope)
        val mired = REFERENCE_MIRED + offset
        if (mired <= 0) return MAX_TEMPERATURE
        return (1e6 / mired).coerceIn(MIN_TEMPERATURE, MAX_TEMPERATURE)
    }

    /** Gains for a temperature and tint (positive = magenta, i.e. less green); all gains are at least 1. */
    fun gains(temperature: Double, tint: Double, calibration: Calibration): WhiteBalanceGains {
        val (red, blue) = modelGains(temperature)
        val green = 1 - tint.coerceIn(-MAX_TINT, MAX_TINT) / 500
        val r = red * calibration.red
        val b = blue * calibration.blue
        val scale = 1 / minOf(r, green, b)
        return WhiteBalanceGains((r * scale).toFloat(), (green * scale).toFloat(), (b * scale).toFloat())
    }
}
