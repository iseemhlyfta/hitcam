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

    /** Creates a new encoder session and returns the surface to draw frames into. */
    fun configure(width: Int, height: Int, fps: Int, bitrateKbps: Int): Surface {
        release()
        val name = MediaCodecList(MediaCodecList.REGULAR_CODECS).findEncoderForFormat(format(width, height, fps, bitrateKbps, high = false))
            ?: throw IllegalStateException("No H.264 encoder for ${width}x$height@$fps")
        val info = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.first { it.name == name }
        val capabilities = info.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC)
        val supportsHigh = capabilities.profileLevels.any { it.profile == MediaCodecInfo.CodecProfileLevel.AVCProfileHigh }
        val cbr = capabilities.encoderCapabilities?.isBitrateModeSupported(MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR) == true

        val current = ++generation
        val codec = MediaCodec.createByCodecName(name)
        codec.setCallback(Callback(current), handler)
        // High profile without B-frames (Constrained High) where offered; otherwise the encoder's default profile.
        val attempts = if (supportsHigh) listOf(true, false) else listOf(false)
        var lastError: Exception? = null
        for (high in attempts) {
            try {
                val format = format(width, height, fps, bitrateKbps, high)
                if (cbr) format.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
                codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                lastError = null
                break
            } catch (e: Exception) {
                lastError = e
                codec.reset()
                codec.setCallback(Callback(current), handler)
            }
        }
        if (lastError != null) {
            codec.release()
            throw lastError
        }
        val surface = codec.createInputSurface()
        codec.start()
        this.codec = codec
        inputSurface = surface
        return surface
    }

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

    private fun format(width: Int, height: Int, fps: Int, bitrateKbps: Int, high: Boolean) =
        MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrateKbps * 1000)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            setInteger(MediaFormat.KEY_PRIORITY, 0)
            setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
            setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED)
            setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO)
            // Without new frames (a still scene) the encoder repeats the last one so the PC keeps receiving video.
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 1_000_000L / fps * 3)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) setInteger(MediaFormat.KEY_LATENCY, 1)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            if (high) {
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
