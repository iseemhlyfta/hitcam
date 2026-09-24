package io.github.hitnes.hitcam.video

/** Annex-B helpers for MediaCodec output (start-code delimited H.264). */
object AnnexB {
    const val NAL_SPS = 7
    const val NAL_PPS = 8

    /** NAL unit types in order of appearance; 3- and 4-byte start codes are both accepted. */
    fun nalTypes(data: ByteArray): List<Int> {
        val types = mutableListOf<Int>()
        var i = 0
        while (i + 3 < data.size) {
            if (data[i].toInt() == 0 && data[i + 1].toInt() == 0) {
                val header = when {
                    data[i + 2].toInt() == 1 -> i + 3
                    data[i + 2].toInt() == 0 && data[i + 3].toInt() == 1 -> i + 4
                    else -> -1
                }
                if (header in data.indices) {
                    types += data[header].toInt() and 0x1F
                    i = header + 1
                    continue
                }
            }
            i++
        }
        return types
    }

    /**
     * Keyframes must carry SPS and PPS so the PC can start decoding at any of them (as on the iPhone).
     * Most encoders emit them once as codec config; some repeat them in every keyframe already.
     */
    fun withParameterSets(config: ByteArray?, keyframe: ByteArray): ByteArray {
        if (config == null || config.isEmpty() || NAL_SPS in nalTypes(keyframe).take(4)) return keyframe
        return config + keyframe
    }
}
