package io.github.hitnes.hitcam.video

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface

class EncodedFrame(
    /** Annex-B access unit; keyframes start with SPS and PPS. */
    val data: ByteArray,
    val isKeyframe: Boolean,
    val timestampMicros: Long,
)

/**
 * Hardware H.264 encoder fed through an input surface, tuned for low latency like the iPhone's:
 * no B-frames, real-time priority, a keyframe every 2 seconds and on request, BT.709 limited range.
 */
class H264Encoder(private val output: (EncodedFrame) -> Unit) {
    private val thread = HandlerThread("hitcam-encoder").apply { start() }
    private val handler = Handler(thread.looper)
    private var codec: MediaCodec? = null
    private var parameterSets: ByteArray? = null
    private var inputSurface: Surface? = null
    @Volatile private var generation = 0

    /**
     * Creates a new encoder session and returns the surface to draw frames into.
     *
     * Encoders are tried one by one, hardware first, each with the full low-latency setup, then without the High
     * profile, then with only the required keys. MediaCodecList.findEncoderForFormat is not used: many phones
     * (Xiaomi among them) under-report what their hardware encoder can do, so it finds none for plain 1080p30.
     */
    fun configure(width: Int, height: Int, fps: Int, bitrateKbps: Int): Surface {
        release()
        val current = ++generation
        var lastError: Exception? = null
        for (info in encoders()) {
            val capabilities = runCatching { info.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC) }.getOrNull() ?: continue
            val supportsHigh = capabilities.profileLevels.any { it.profile == MediaCodecInfo.CodecProfileLevel.AVCProfileHigh }
            val cbr = capabilities.encoderCapabilities?.isBitrateModeSupported(MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR) == true
            val variants = buildList {
                if (supportsHigh) add(Variant.HIGH)
                add(Variant.DEFAULT_PROFILE)
                add(Variant.MINIMAL)
            }
            val codec = try {
                MediaCodec.createByCodecName(info.name)
            } catch (e: Exception) {
                lastError = e
                continue
            }
            for (variant in variants) {
                try {
                    codec.setCallback(Callback(current), handler)
                    val format = format(width, height, fps, bitrateKbps, variant)
                    if (cbr && variant != Variant.MINIMAL) {
                        format.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
                    }
                    codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                    val surface = codec.createInputSurface()
                    codec.start()
                    this.codec = codec
                    inputSurface = surface
                    return surface
                } catch (e: Exception) {
                    lastError = e
                    runCatching { codec.reset() }
                }
            }
            codec.release()
        }
        throw IllegalStateException("No H.264 encoder for ${width}x$height@$fps" + (lastError?.message?.let { ": $it" } ?: ""), lastError)
    }

    /** H.264 encoders, hardware ones first. */
    private fun encoders(): List<MediaCodecInfo> {
        val all = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            info.isEncoder && info.supportedTypes.any { it.equals(MediaFormat.MIMETYPE_VIDEO_AVC, ignoreCase = true) }
        }
        return all.sortedBy { if (isSoftware(it)) 1 else 0 }
    }

    private fun isSoftware(info: MediaCodecInfo): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            info.isSoftwareOnly
        } else {
            val name = info.name.lowercase()
            name.startsWith("omx.google.") || name.startsWith("c2.android.") || name.contains(".sw.")
        }

    private enum class Variant { HIGH, DEFAULT_PROFILE, MINIMAL }

    fun setBitrate(kbps: Int) {
        codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, kbps * 1000) })
    }

    fun requestKeyframe() {
        try {
            codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
        } catch (_: IllegalStateException) {
            // Released meanwhile.
        }
    }

    /** Stops the encoder; its input surface must no longer be drawn into. */
    fun release() {
        generation++
        val old = codec ?: return
        codec = null
        parameterSets = null
        try {
            old.stop()
        } catch (_: Exception) {
        }
        old.release()
        inputSurface?.release()
        inputSurface = null
    }

    private fun format(width: Int, height: Int, fps: Int, bitrateKbps: Int, variant: Variant) =
        MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrateKbps * 1000)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            // Only the keys every encoder must accept; the rest is tuning some encoders reject.
            if (variant == Variant.MINIMAL) return@apply
            setInteger(MediaFormat.KEY_PRIORITY, 0)
            setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
            setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED)
            setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO)
            // Without new frames (a still scene) the encoder repeats the last one so the PC keeps receiving video.
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 1_000_000L / fps * 3)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) setInteger(MediaFormat.KEY_LATENCY, 1)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            if (variant == Variant.HIGH) {
                setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileHigh)
                setInteger(MediaFormat.KEY_LEVEL, if (width * height * fps > 1920 * 1080 * 30) MediaCodecInfo.CodecProfileLevel.AVCLevel42 else MediaCodecInfo.CodecProfileLevel.AVCLevel41)
            }
        }

    private inner class Callback(private val owner: Int) : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) = Unit

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            if (owner != generation) return
            try {
                val buffer = codec.getOutputBuffer(index)
                if (buffer != null && info.size > 0) {
                    buffer.position(info.offset)
                    buffer.limit(info.offset + info.size)
                    val bytes = ByteArray(info.size).also { buffer.get(it) }
                    if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                        parameterSets = bytes
                    } else {
                        val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                        val data = if (key) AnnexB.withParameterSets(parameterSets, bytes) else bytes
                        output(EncodedFrame(data, key, info.presentationTimeUs))
                    }
                }
                codec.releaseOutputBuffer(index, false)
            } catch (_: IllegalStateException) {
                // Released while this callback was queued.
            }
        }

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            if (owner == generation) requestKeyframe()
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
            // Some encoders report SPS/PPS here instead of in a codec config buffer.
            if (owner != generation) return
            val sps = format.getByteBuffer("csd-0")
            val pps = format.getByteBuffer("csd-1")
            if (sps != null && pps != null) {
                parameterSets = ByteArray(sps.remaining()).also { sps.duplicate().get(it) } +
                    ByteArray(pps.remaining()).also { pps.duplicate().get(it) }
            }
        }
    }
}
